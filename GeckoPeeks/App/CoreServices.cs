using System.Reflection;
using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;

namespace GeckoPeeks.App;

/// <summary>
/// The v2.1 app core in one place: reference data, ASN cache, region choice, the Worker client
/// and poller, and the hop resolver. It does not start the poller; the caller calls Cloud.Start().
/// Dev stores live in the dev root; with no dev root they are memory only.
/// </summary>
public sealed class CoreServices : IDisposable
{
    private readonly DohClient _doh;
    private bool _disposed;

    private CoreServices(RefDataStore refData, AsnCache asn, RegionService region, CloudClient client,
        CloudPoller cloud, DohClient doh, IHopResolver resolver, DiagnosticsStore diagnostics)
    {
        Diagnostics = diagnostics;
        RefData = refData;
        Asn = asn;
        Region = region;
        Client = client;
        Cloud = cloud;
        _doh = doh;
        Resolver = resolver;
        RefData.Changed += Region.OnRefDataChanged;
        Cloud.Changed += SaveCloud;
    }

    /// <summary>The app version as "major.minor.build".</summary>
    public static string AppVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public static CoreServices Create(RunProfile profile, string? devRoot, bool cloudEnabled) =>
        Create(profile, devRoot, cloudEnabled, null, null);

    /// <summary>Test seam: fake handlers for the Worker and DoH. Null uses real ones.</summary>
    internal static CoreServices Create(RunProfile profile, string? devRoot, bool cloudEnabled,
        HttpMessageHandler? workerHandler, HttpMessageHandler? dohHandler)
    {
        var refData = new RefDataStore(RefDataStore.PathFor(profile, devRoot));
        var asn = new AsnCache(AsnCache.PathFor(profile, devRoot));
        var region = new RegionService(new RegionSettings(RegionSettings.PathFor(profile, devRoot)), () => refData.Current);
        var client = workerHandler == null
            ? new CloudClient()
            : new CloudClient(workerHandler, CloudClient.ResolveBaseUrl(Environment.GetEnvironmentVariable(CloudClient.BaseUrlVariable)));
        var cloud = new CloudPoller(client, cloudEnabled, null, new CloudPollerOptions(asn, refData, region));
        var doh = new DohClient(dohHandler);
        var resolver = new HopResolver(asn, () => cloud.Enabled, client, doh);
        var diagnostics = new DiagnosticsStore(DiagnosticsStore.PathFor(profile, devRoot), AppVersion);
        return new CoreServices(refData, asn, region, client, cloud, doh, resolver, diagnostics);
    }

    public RefDataStore RefData { get; }
    public AsnCache Asn { get; }
    public RegionService Region { get; }
    public CloudClient Client { get; }
    public CloudPoller Cloud { get; }
    public IHopResolver Resolver { get; }

    /// <summary>Every test, cloud answer and League ping row, kept as JSON lines (never deleted).</summary>
    public DiagnosticsStore Diagnostics { get; }

    /// <summary>
    /// Keeps each Cloudflare and Riot status answer that differs from the last one. Changed can fire
    /// on the UI thread, so the parse and the file write run on the thread pool.
    /// </summary>
    private void SaveCloud() => ThreadPool.QueueUserWorkItem(_ => SaveCloudNow());

    private readonly object _cloudSaveGate = new();

    private void SaveCloudNow()
    {
        if (_disposed)
            return;
        // One save at a time, snapshot and write together, so an older snapshot is never written
        // after a newer one.
        lock (_cloudSaveGate)
        {
            SaveCloudLocked();
        }
    }

    private void SaveCloudLocked()
    {
        try
        {
            var snapshot = Cloud.Snapshot();
            if (!snapshot.Enabled)
                return;
            var now = DateTime.UtcNow;
            Diagnostics.RecordCloud("edge", snapshot.EdgeJson, now);
            Diagnostics.RecordCloud("pulse", snapshot.PulseJson, now);
            Diagnostics.RecordCloud("riotStatus", snapshot.RiotStatusJson, now);
        }
        catch
        {
            // Diagnostics never stop the app.
        }
    }

    /// <summary>TargetIp from Region.Current, Resolver, RefData.Current.</summary>
    public ProbeRequest BuildRequest(bool leagueRunning) =>
        new(leagueRunning, Region.Current.Region.TargetIp, Resolver, RefData.Current);

    /// <summary>Flushes the ASN cache, disposes the poller, the client and the DoH client.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        RefData.Changed -= Region.OnRefDataChanged;
        Cloud.Changed -= SaveCloud;
        try
        {
            Asn.Flush();
        }
        catch
        {
            // Best effort.
        }

        Cloud.Dispose();
        Client.Dispose();
        _doh.Dispose();
    }
}
