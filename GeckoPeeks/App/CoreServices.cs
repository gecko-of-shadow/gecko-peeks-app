using GeckoPeeks.Cloud;
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
        CloudPoller cloud, DohClient doh, IHopResolver resolver)
    {
        RefData = refData;
        Asn = asn;
        Region = region;
        Client = client;
        Cloud = cloud;
        _doh = doh;
        Resolver = resolver;
        RefData.Changed += Region.OnRefDataChanged;
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
        return new CoreServices(refData, asn, region, client, cloud, doh, resolver);
    }

    public RefDataStore RefData { get; }
    public AsnCache Asn { get; }
    public RegionService Region { get; }
    public CloudClient Client { get; }
    public CloudPoller Cloud { get; }
    public IHopResolver Resolver { get; }

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
