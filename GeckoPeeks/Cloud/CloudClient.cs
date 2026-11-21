using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GeckoPeeks.Cloud;

public sealed record AsnResult(string Ip, long? Asn, string? Name, string? Country);

/// <param name="NotModified">True for a 304; Json is then null.</param>
public sealed record RefDataFetch(bool NotModified, string? Json, string? ETag);

/// <summary>
/// Reads the Gecko Peeks Worker (Contract 1). The base URL is the deployed Worker unless
/// GECKO_PEEKS_CLOUD_URL names another https origin. Every call has a 20 s deadline (headers and body) and a size
/// cap, and every body must be one JSON object. No token or user data goes out: the only
/// inputs are an ASN, a country code and public hop addresses.
/// </summary>
public sealed class CloudClient : IDisposable
{
    public const string DefaultBaseUrl = "https://gecko-peeks-api.gecko-apps.workers.dev";
    public const string BaseUrlVariable = "GECKO_PEEKS_CLOUD_URL";

    /// <summary>The Worker refuses more than 32. 15 keeps every IP on its cached path.</summary>
    public const int MaxIpsPerCall = 15;

    public const int MaxBodyBytes = 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public CloudClient()
        : this(null, ResolveBaseUrl(Environment.GetEnvironmentVariable(BaseUrlVariable)))
    {
    }

    /// <param name="handler">Test seam. Null uses a normal handler.</param>
    public CloudClient(HttpMessageHandler? handler, string baseUrl)
    {
        BaseUrl = new Uri(ResolveBaseUrl(baseUrl).TrimEnd('/') + "/", UriKind.Absolute);
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = true;
        _http.Timeout = Timeout;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GeckoPeeks", AssemblyVersion()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseUrl { get; }

    /// <summary>
    /// An https origin, or an http loopback origin for local Worker tests. Anything else,
    /// including a path, query or user info, falls back to the deployed Worker.
    /// </summary>
    public static string ResolveBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return DefaultBaseUrl;

        var secure = uri.Scheme == Uri.UriSchemeHttps;
        var loopback = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!(secure || loopback) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/")
            return DefaultBaseUrl;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public Task<string> GetEdgeJsonAsync(CancellationToken cancel) => GetObjectAsync("v1/edge", cancel);

    /// <summary>Both parameters are optional; the Worker then uses the caller's own network.</summary>
    public Task<string> GetPulseJsonAsync(long? isp, string? country, CancellationToken cancel)
    {
        var query = new List<string>();
        if (isp is { } asn && asn is >= 1 and <= 4294967295)
            query.Add("isp=" + asn.ToString(CultureInfo.InvariantCulture));
        if (country is { Length: 2 } cc && cc.All(c => c is >= 'A' and <= 'Z'))
            query.Add("country=" + cc);
        var path = query.Count == 0 ? "v1/pulse" : "v1/pulse?" + string.Join("&", query);
        return GetObjectAsync(path, cancel);
    }

    /// <summary>
    /// Looks up at most <see cref="MaxIpsPerCall"/> public addresses. Rows are matched by ip;
    /// an address the Worker dropped is simply missing from the result.
    /// </summary>
    public async Task<IReadOnlyList<AsnResult>> LookupAsnAsync(IReadOnlyList<string> ips, CancellationToken cancel)
    {
        var wanted = ips.Where(PublicIp.IsPublic).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxIpsPerCall).ToList();
        if (wanted.Count == 0)
            return Array.Empty<AsnResult>();

        var json = await GetObjectAsync("v1/asn?ips=" + Uri.EscapeDataString(string.Join(",", wanted)), cancel).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new CloudException("The ASN reply had no results.");

        var list = new List<AsnResult>();
        foreach (var row in results.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            var ip = Text(row, "ip");
            if (ip == null)
                continue;
            long? asn = row.TryGetProperty("asn", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var n) ? n : null;
            list.Add(new AsnResult(ip, asn, Text(row, "name"), Text(row, "country")));
        }

        return list;
    }

    /// <summary>GET /v1/refdata. Sends If-None-Match when etag is not null; a 304 gives NotModified.</summary>
    public async Task<RefDataFetch> GetRefDataAsync(string? etag, CancellationToken cancel)
    {
        var (status, body, responseTag) = await SendAsync("v1/refdata", etag, cancel).ConfigureAwait(false);
        if (status == 304)
            return new RefDataFetch(true, null, etag);
        return new RefDataFetch(false, AsObject(body!), responseTag);
    }

    /// <summary>GET /v1/riot-status?region=&lt;id&gt;. The id is a region id or alias.</summary>
    public Task<string> GetRiotStatusJsonAsync(string regionId, CancellationToken cancel)
    {
        var id = (regionId ?? "").Trim().ToLowerInvariant();
        if (id.Length is 0 or > 8 || !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9'))
            throw new CloudException("Unknown region.");
        return GetObjectAsync("v1/riot-status?region=" + Uri.EscapeDataString(id), cancel);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    internal static string AssemblyVersion()
    {
        var v = typeof(CloudClient).Assembly.GetName().Version;
        return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
    }

    private async Task<string> GetObjectAsync(string relative, CancellationToken cancel)
    {
        var (_, body, _) = await SendAsync(relative, null, cancel).ConfigureAwait(false);
        return AsObject(body!);
    }

    /// <summary>One GET. 2xx gives the body; 304 (only when etag was sent) gives a null body; anything else throws.</summary>
    private async Task<(int Status, string? Body, string? ETag)> SendAsync(string relative, string? etag, CancellationToken cancel)
    {
        // HttpClient.Timeout stops at the headers, so the body read gets the same deadline here.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl, relative));
            if (!string.IsNullOrWhiteSpace(etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag.Trim());
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code == 304 && etag != null)
                return (304, null, etag);
            if (!response.IsSuccessStatusCode)
                throw new CloudException("The Gecko Peeks service answered " + code + ".", code);

            var body = await ReadCappedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var tag = response.Headers.ETag?.ToString();
            if (tag == null && response.Headers.TryGetValues("ETag", out var values))
                tag = values.FirstOrDefault();
            return (code, body, tag);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("The Gecko Peeks service did not answer in time.");
        }
    }

    private static string AsObject(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new CloudException("The Gecko Peeks service sent something that is not a JSON object.");
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new CloudException("The Gecko Peeks service sent broken JSON.");
        }
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken cancel)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                throw new CloudException("The Gecko Peeks service sent too much data.");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class CloudException : Exception
{
    public CloudException(string message)
        : base(message)
    {
    }

    public CloudException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status when the Worker answered with an error status, else null.</summary>
    public int? StatusCode { get; }
}
