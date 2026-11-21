using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GeckoPeeks.App;

/// <summary>
/// Reads the update feed (the gecko-peeks-api Worker) and downloads the exe. Failures are
/// thrown as short messages the caller turns into a status line — never a crash.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    /// <summary>Smallest file we accept as GeckoPeeks.exe. A truncated HTML page is far smaller.</summary>
    public const long MinimumExeBytes = 1_000_000;

    /// <summary>The download fails when no bytes arrive for this long.</summary>
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The whole download must finish in this time.</summary>
    private static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(20);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private int _disposed;

    public UpdateClient(HttpClient? http = null, UpdateFeed? feed = null)
    {
        Feed = feed ?? UpdateFeed.Current;
        if (http != null)
        {
            _http = http;
            _ownsClient = false;
            return;
        }

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsClient = true;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("GeckoPeeks", AppUpdate.FormatVersion(AppUpdate.CurrentVersion())));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public UpdateFeed Feed { get; }

    /// <summary>True when this client may talk to the feed for <paramref name="profile"/>.</summary>
    public bool Serves(RunProfile profile) => Feed.Enabled && Feed.Profile == profile;

    /// <summary>Null when this build is already current. A non-success HTTP status throws.</summary>
    public Task<UpdateOffer?> CheckAsync(Version current, CancellationToken cancellationToken = default) =>
        CheckAsync(current, AppIsolation.Profile, cancellationToken);

    public async Task<UpdateOffer?> CheckAsync(Version current, RunProfile profile, CancellationToken cancellationToken = default)
    {
        if (!Serves(profile))
            return null;

        using var response = await _http.GetAsync(Feed.LatestUrl, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 404 {"error":"no_release"} means nothing is published on this channel yet.
        // Every other failure throws, so the UI shows an error instead of "latest".
        if (response.StatusCode == HttpStatusCode.NotFound && IsNoRelease(text))
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                "The update server returned " + (int)response.StatusCode + " (" + response.ReasonPhrase + ").");

        return AppUpdate.PickOffer(text, current, Feed.BaseUrl);
    }

    internal static bool IsNoRelease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() == "no_release";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the exe next to the running exe as <c>&lt;exe&gt;.new</c>. Checks the MZ header,
    /// the exact size and the SHA-256 from the manifest. A failed check deletes the file and throws.
    /// </summary>
    public Task<string> DownloadAsync(UpdateOffer offer, string exePath, CancellationToken cancellationToken = default) =>
        DownloadAsync(offer, exePath, AppIsolation.Profile, null, cancellationToken);

    public Task<string> DownloadAsync(
        UpdateOffer offer,
        string exePath,
        RunProfile profile,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(offer, exePath, profile, null, cancellationToken);

    /// <summary>Same as the other overloads, and also reports progress from 0 to 1.</summary>
    public async Task<string> DownloadAsync(
        UpdateOffer offer,
        string exePath,
        RunProfile profile,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!Serves(profile))
            throw new InvalidOperationException("Dev builds do not download updates.");

        // A missing check must not look like a passed check.
        var expected = AppUpdate.NormalizeSha256(offer.Sha256)
            ?? throw new InvalidOperationException("The update has no SHA-256 checksum, so it was not downloaded.");

        var destination = exePath + ".new";
        var temp = destination + ".partial";
        try
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (UnauthorizedAccessException ex)
            {
                // A leftover file in a folder Windows now protects: same cause, same message.
                throw new UpdateFolderBlockedException(ex);
            }

            string actual;
            // HttpClient.Timeout stops at the headers. The body gets its own deadlines,
            // so a stalled connection ends in an error and the next check can retry.
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(DownloadDeadline);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            idle.CancelAfter(DownloadIdleTimeout);
            try
            {
                // Open the file first. A folder Windows protects (Controlled Folder Access) throws
                // UnauthorizedAccessException here, before any bytes are downloaded.
                FileStream opened;
                try
                {
                    opened = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UpdateFolderBlockedException(ex);
                }

                await using var output = opened;
                using var response = await _http.GetAsync(offer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? (offer.SizeBytes > 0 ? offer.SizeBytes : 0);
                await using var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                actual = await CopyAndHashAsync(input, output, total, progress, idle, DownloadIdleTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The download stalled. It will try again later.");
            }

            var info = new FileInfo(temp);
            if (info.Length < MinimumExeBytes)
                throw new InvalidOperationException("The download was too small to be GeckoPeeks.exe.");
            if (offer.SizeBytes > 0 && info.Length != offer.SizeBytes)
                throw new InvalidOperationException("The download size did not match the release.");

            await using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                    throw new InvalidOperationException("The download is not a Windows executable.");
            }

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException("The download did not match its SHA-256 checksum.");

            File.Move(temp, destination, overwrite: true);
            progress?.Report(1.0);
            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup.
            }

            throw;
        }
    }

    private static async Task<string> CopyAndHashAsync(
        Stream input,
        Stream output,
        long total,
        IProgress<double>? progress,
        CancellationTokenSource idle,
        TimeSpan idleTimeout)
    {
        var cancellationToken = idle.Token;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long written = 0;
        progress?.Report(0);
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            // Data arrived, so the idle clock starts again.
            idle.CancelAfter(idleTimeout);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (progress != null && total > 0)
                progress.Report(Math.Min(0.99, (double)written / total));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_ownsClient)
            _http.Dispose();
    }
}

/// <summary>Windows refused to create the download file next to the exe (for example Controlled Folder Access).</summary>
public sealed class UpdateFolderBlockedException : IOException
{
    public UpdateFolderBlockedException(Exception inner)
        : base("Windows blocked saving the update next to the exe.", inner)
    {
    }
}
