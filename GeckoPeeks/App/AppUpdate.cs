using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeckoPeeks.App;

/// <summary>A newer release from the update feed that can replace this exe.</summary>
/// <param name="Sha256">Lowercase hex from the manifest. Always set: the feed must give it.</param>
/// <param name="Notes">Release notes as plain text, at most <see cref="AppUpdate.MaxNotesLength"/> chars.</param>
public sealed record UpdateOffer(
    Version Version,
    string DownloadUrl,
    long SizeBytes,
    string Sha256,
    string Notes = "");

/// <summary>
/// Where this build looks for updates. Production reads the "stable" channel. A Dev build reads
/// "devtest", and only when GECKO_PEEKS_DEV_UPDATES=1 is set. Every other Dev build is off.
/// </summary>
public sealed record UpdateFeed(RunProfile Profile, bool Enabled, Uri BaseUrl, string Channel)
{
    public const string DefaultBaseUrl = "https://gecko-peeks-api.gecko-apps.workers.dev/";
    public const string UrlVariable = "GECKO_PEEKS_UPDATE_URL";
    public const string DevUpdatesVariable = "GECKO_PEEKS_DEV_UPDATES";
    public const string DelayVariable = "GECKO_PEEKS_UPDATE_DELAY_S";
    public const string StableChannel = "stable";
    public const string DevChannel = "devtest";

    /// <summary>The feed of this exe, read once from the environment.</summary>
    public static UpdateFeed Current { get; } = For(AppIsolation.Profile, Environment.GetEnvironmentVariable);

    /// <summary>
    /// True when <paramref name="profile"/> may check, download, skip or swap. A profile other
    /// than this exe's own is treated as off, except Production, which is always on.
    /// </summary>
    public static bool IsEnabled(RunProfile profile) =>
        profile == RunProfile.Production || (profile == Current.Profile && Current.Enabled);

    public Uri LatestUrl => new(BaseUrl, "v1/update/latest?channel=" + Channel);

    public static UpdateFeed For(RunProfile profile, Func<string, string?> env)
    {
        // Production always reads the real Worker. The manifest's SHA-256 and the exe come from
        // the same host, so one user variable must not be able to move the live app's updates.
        var baseUrl = (profile == RunProfile.Dev ? ParseBaseUrl(env(UrlVariable), profile) : null)
            ?? new Uri(DefaultBaseUrl);
        return profile switch
        {
            RunProfile.Production => new UpdateFeed(profile, true, baseUrl, StableChannel),
            RunProfile.Dev => new UpdateFeed(profile, string.Equals(env(DevUpdatesVariable)?.Trim(), "1", StringComparison.Ordinal), baseUrl, DevChannel),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unexpected run profile."),
        };
    }

    /// <summary>
    /// An https origin (with an optional path), or null. Plain http is allowed only for a
    /// loopback host in the Dev profile, so a local test server can stand in for the Worker.
    /// </summary>
    public static Uri? ParseBaseUrl(string? text, RunProfile profile)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        var https = uri.Scheme == Uri.UriSchemeHttps;
        var localHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && profile == RunProfile.Dev;
        if (!https && !localHttp)
            return null;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path).Uri;
    }
}

/// <summary>
/// Reads the update feed manifest and compares versions. The download and the file swap live in
/// <see cref="UpdateClient"/> / <see cref="UpdateInstaller"/>.
/// </summary>
public static class AppUpdate
{
    public const string Owner = "gecko-of-shadow";
    /// <summary>The public repository (v2.1.2+). The old private repo keeps the v1 history.</summary>
    public const string Repo = "gecko-peeks-app";
    public const int MaxNotesLength = 4000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The project's releases page. The host only opens github.com links.</summary>
    public static string ReleasesPageUrl =>
        $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>Three-part version from the assembly (1.0.12.0 → 1.0.12).</summary>
    public static Version CurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>Reads <c>v1.0.12</c> or <c>1.0.12</c>. Other text is ignored.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        if (!Version.TryParse(text, out var version))
            return null;

        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public static bool IsNewer(Version candidate, Version current) =>
        Compare(candidate, current) > 0;

    public static int Compare(Version a, Version b)
    {
        var major = a.Major.CompareTo(b.Major);
        if (major != 0)
            return major;
        var minor = a.Minor.CompareTo(b.Minor);
        if (minor != 0)
            return minor;
        return Math.Max(0, a.Build).CompareTo(Math.Max(0, b.Build));
    }

    /// <summary>
    /// Reads <c>/v1/update/latest</c>. Null when the release is not newer than
    /// <paramref name="current"/>. A newer release without a SHA-256, a size, or a download URL
    /// on the feed's own host throws, so the UI shows an error instead of installing it.
    /// </summary>
    public static UpdateOffer? PickOffer(string manifestJson, Version current, Uri feedBase)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The update server sent a manifest that is not JSON.");
        }

        var version = ParseTag(manifest?.Version)
            ?? throw new InvalidDataException("The update server sent a manifest without a version.");
        if (!IsNewer(version, current))
            return null;

        var sha256 = NormalizeSha256(manifest!.Sha256)
            ?? throw new InvalidDataException("The update server did not give a SHA-256 checksum.");
        if (manifest.Size <= 0)
            throw new InvalidDataException("The update server did not give the file size.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || !SameOrigin(url, feedBase))
            throw new InvalidDataException("The update server gave a download link on another host.");

        return new UpdateOffer(version, url.AbsoluteUri, manifest.Size, sha256, TrimNotes(manifest.Notes));
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.IsNullOrEmpty(a.UserInfo);

    /// <summary>Lowercase 64-char hex, or null when the text is not a SHA-256 value.</summary>
    public static string? NormalizeSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (value.Length != 64)
            return null;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        return value.ToLowerInvariant();
    }

    /// <summary>Release notes as plain text, trimmed to <see cref="MaxNotesLength"/> chars.</summary>
    public static string TrimNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length <= MaxNotesLength)
            return text;

        return text[..(MaxNotesLength - 1)].TrimEnd() + "…";
    }

    /// <summary>Deletes a leftover <c>.old</c> from a previous swap. Safe to call every launch.</summary>
    public static void CleanupLeftovers(string? exePath) => CleanupLeftovers(exePath, AppIsolation.Profile);

    public static void CleanupLeftovers(string? exePath, RunProfile profile)
    {
        if (!AppIsolation.AllowsExeSiblingCleanup(profile))
            return;

        if (string.IsNullOrWhiteSpace(exePath))
            return;

        try
        {
            var old = exePath + ".old";
            if (File.Exists(old))
                File.Delete(old);
        }
        catch
        {
            // Antivirus or Explorer may hold it briefly; the next launch tries again.
        }
    }

    public static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}

/// <summary>The JSON of <c>/v1/update/latest</c>.</summary>
internal sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
