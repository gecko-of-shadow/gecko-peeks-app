using System.Net;
using System.Text;
using GeckoPeeks.Routing;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// Tests that change <see cref="RefData.Current"/> (directly or through RefDataStore and
/// CoreServices) run alone, and put the built-in copy back at the end.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RefDataGlobalCollection
{
    public const string Name = "RefData global state";
}

/// <summary>Answers each request from a function and records it. No real network.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<HttpRequestMessage> _requests = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Json("{}"));

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_lock)
                return _requests.ToList();
        }
    }

    public int Count => Requests.Count;

    public int CountPath(string path) => Requests.Count(r => r.RequestUri!.AbsolutePath == path);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
            _requests.Add(request);
        return Respond(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Never answers until the request is cancelled.</summary>
    public static async Task<HttpResponseMessage> Hang(HttpRequestMessage _, CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        throw new OperationCanceledException(token);
    }
}

internal static class V21
{
    /// <summary>The embedded refdata.json text, as the app ships it.</summary>
    public static string BuiltInJson()
    {
        using var stream = typeof(RefData).Assembly.GetManifestResourceStream(RefData.ResourceName)
            ?? throw new InvalidOperationException("refdata.json is not embedded in the test assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static HopObservation Hop(int n, string ip, string host = "", long? rtt = 10, bool reached = false) =>
        new(n, ip, host, rtt, true, reached);

    public static HopObservation Silent(int n) => new(n, "*", "", null, false, false);

    /// <summary>A fresh folder under the user temp folder. Never a Production path.</summary>
    public static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "GeckoPeeksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void RemoveFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && path.Contains("GeckoPeeksTests", StringComparison.Ordinal))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temp clean-up is best effort.
        }
    }

    /// <summary>
    /// The frozen neutral demo route (contract section 6.4). Hops 4 and 7 have no hostname.
    /// </summary>
    public static (HopObservation[] Hops, Dictionary<string, long> Asns) DemoRoute()
    {
        var hops = new[]
        {
            Hop(1, "192.168.1.1", "", 1),
            Hop(2, "100.64.0.1", "", 6),
            Silent(3),
            Hop(4, "184.105.64.1", "", 17),
            Hop(5, "184.105.80.1", "100ge0-1.core2.chi1.he.net", 38),
            Hop(6, "104.160.131.1", "ae1.er01.chi01.riotdirect.net", 39),
            Hop(7, "192.207.0.5", "", 40, reached: true),
        };
        var asns = new Dictionary<string, long>
        {
            ["184.105.64.1"] = 6939,
            ["184.105.80.1"] = 6939,
            ["104.160.131.1"] = 6507,
            ["192.207.0.5"] = 6507,
        };
        return (hops, asns);
    }
}
