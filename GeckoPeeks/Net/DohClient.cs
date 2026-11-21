using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeckoPeeks.Net;

public enum DohStatus { Name, NoName, Failed }

public readonly record struct DohPtr(DohStatus Status, string Hostname);

/// <summary>
/// Reverse DNS and Team Cymru ASN lookups over DNS-over-HTTPS (JSON API). Used only when the
/// system resolver gives no name for a public router address. Only the router address goes out.
/// </summary>
public sealed class DohClient : IDisposable
{
    public const string GoogleUrl = "https://dns.google/resolve";
    public const string CloudflareUrl = "https://cloudflare-dns.com/dns-query";

    /// <summary>Asks Google Public DNS not to send the client's subnet (EDNS Client Subnet) on.</summary>
    public const string GoogleNoEcs = "&edns_client_subnet=0.0.0.0/0";

    public const int MaxBodyBytes = 64 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly Regex HostPattern = new(
        "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private readonly HttpClient _http;

    public DohClient(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // each request has its own deadline
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GeckoPeeks", AssemblyVersion()));
    }

    /// <summary>Google, Google again on SERVFAIL or timeout, then Cloudflare. NXDOMAIN or no PTR stops at once.</summary>
    public async Task<DohPtr> PtrAsync(string ip, CancellationToken cancellationToken)
    {
        // The rule is enforced where the data leaves the PC: a private, CGNAT or link-local
        // address never goes out, whatever the caller says.
        if (!GeckoPeeks.Cloud.PublicIp.IsPublic(ip) || !TryReverseName(ip, out var name))
            return new DohPtr(DohStatus.Failed, "");

        var query = "?name=" + Uri.EscapeDataString(name) + "&type=PTR";
        var attempts = new[] { GoogleUrl, GoogleUrl, CloudflareUrl };
        foreach (var url in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Google forwards the client's /24 to the name servers unless ECS is turned off.
            // Cloudflare DoH sends no ECS.
            var target = url == GoogleUrl ? url + query + GoogleNoEcs : url + query;
            var answer = await QueryAsync(target, url == CloudflareUrl, cancellationToken).ConfigureAwait(false);
            if (answer == null)
                continue; // timeout, transport error, bad body or SERVFAIL

            var (status, records) = answer.Value;
            if (status == 3)
                return new DohPtr(DohStatus.NoName, "");
            if (status != 0)
                continue;

            var data = records.FirstOrDefault(r => r.Type == 12).Data;
            return ValidHost(data, out var host)
                ? new DohPtr(DohStatus.Name, host)
                : new DohPtr(DohStatus.NoName, "");
        }

        return new DohPtr(DohStatus.Failed, "");
    }

    /// <summary>The origin ASN from Team Cymru's TXT record (Google DoH only), or null.</summary>
    public async Task<long?> CymruAsnAsync(string ip, CancellationToken cancellationToken)
    {
        if (!GeckoPeeks.Cloud.PublicIp.IsPublic(ip)
            || !IPAddress.TryParse(ip?.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var b = address.GetAddressBytes();
        var name = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}.origin.asn.cymru.com", b[3], b[2], b[1], b[0]);
        var answer = await QueryAsync(GoogleUrl + "?name=" + Uri.EscapeDataString(name) + "&type=TXT" + GoogleNoEcs, false, cancellationToken)
            .ConfigureAwait(false);
        if (answer is not { Status: 0 } ok)
            return null;

        foreach (var record in ok.Records)
        {
            if (record.Type != 16 || record.Data == null)
                continue;
            if (ParseCymru(record.Data) is { } asn)
                return asn;
        }

        return null;
    }

    public void Dispose() => _http.Dispose();

    internal static long? ParseCymru(string txt)
    {
        var text = txt.Replace("\"", "").Trim();
        var bar = text.IndexOf('|');
        var field = (bar >= 0 ? text[..bar] : text).Trim();
        var first = field.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (first.Length == 0 || first.Length > 10 || !first.All(char.IsAsciiDigit))
            return null;
        return long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var asn) && asn is >= 1 and <= 4294967295
            ? asn
            : null;
    }

    internal static bool ValidHost(string? data, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(data))
            return false;
        var text = data.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0 || text.Length > 253)
            return false;
        try
        {
            if (!HostPattern.IsMatch(text))
                return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        host = text;
        return true;
    }

    internal static bool TryReverseName(string? ip, out string name)
    {
        name = "";
        if (!IPAddress.TryParse(ip?.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        name = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}.in-addr.arpa", b[3], b[2], b[1], b[0]);
        return true;
    }

    private readonly record struct Record(int Type, string? Data);

    /// <summary>One JSON DoH request. Null on timeout, transport error, a non-200 answer or a bad body.</summary>
    private async Task<(int Status, List<Record> Records)?> QueryAsync(string url, bool cloudflare, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cloudflare)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;

            var body = await ReadCappedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            if (body == null)
                return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Status", out var statusEl)
                || statusEl.ValueKind != JsonValueKind.Number
                || !statusEl.TryGetInt32(out var status))
                return null;

            var records = new List<Record>();
            if (root.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in answers.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object)
                        continue;
                    var type = a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var tv) ? tv : 0;
                    var data = a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    records.Add(new Record(type, data));
                }
            }

            return (status, records);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // this request timed out
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken cancel)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static string AssemblyVersion()
    {
        var v = typeof(DohClient).Assembly.GetName().Version;
        return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
    }
}
