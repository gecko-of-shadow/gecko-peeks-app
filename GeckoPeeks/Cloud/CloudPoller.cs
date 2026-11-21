namespace GeckoPeeks.Cloud;

/// <param name="Status">"off", "loading", "ok" or "error" (Contract 2).</param>
public sealed record CloudSnapshot(
    bool Enabled,
    string Status,
    string Error,
    double? EdgeAgeSec,
    double? PulseAgeSec,
    string? EdgeJson,
    string? PulseJson,
    string? RiotStatusJson = null,
    double? RiotStatusAgeSec = null,
    RefDataStatus? RefData = null);

/// <param name="Asn">The ASN cache. Null = a memory-only cache.</param>
/// <param name="RefData">When set, refdata is fetched at start and every 24 h.</param>
/// <param name="Region">When set, Riot status is fetched for its region and edge data is passed to it.</param>
public sealed record CloudPollerOptions(AsnCache? Asn = null, RefDataStore? RefData = null, GeckoPeeks.App.RegionService? Region = null);

/// <summary>
/// Keeps the Worker data fresh in the background: edge every 15 min, pulse every 5 min, Riot
/// status every 5 min (and soon after a region change), refdata every 24 h, and ASN names for
/// public hop addresses when the hops change (retried on each tick). It runs on the thread pool,
/// so it never blocks the probe loop. A failed call keeps the older data. Edge and pulse failures
/// set a short error; Riot status and refdata failures never do. A failed call is tried again
/// after <see cref="RetryAfterError"/>.
/// </summary>
public sealed class CloudPoller : IDisposable
{
    public static readonly TimeSpan EdgeInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PulseInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RiotStatusInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RefDataInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(2);

    /// <summary>A region change fetches Riot status again, but not faster than this.</summary>
    public static readonly TimeSpan RegionChangeGap = TimeSpan.FromSeconds(60);

    /// <summary>An address that came back with no ASN is asked again after this time.</summary>
    public static readonly TimeSpan AsnMissRetry = TimeSpan.FromMinutes(30);

    /// <summary>A manual refresh fetches edge again only when it is older than this.</summary>
    public static readonly TimeSpan ManualEdgeGap = TimeSpan.FromSeconds(60);

    /// <summary>A manual retry after a failure waits at least this long.</summary>
    public static readonly TimeSpan ManualRetryGap = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly CloudClient _client;
    private readonly Func<DateTime> _utcNow;
    private readonly AsnCache _asn;
    private readonly RefDataStore? _refData;
    private readonly GeckoPeeks.App.RegionService? _region;
    private readonly Dictionary<string, DateTime> _asnMissAt = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cancel = new();
    private System.Threading.Timer? _timer;
    private bool _forceEdge;
    private bool _enabled;
    private bool _disposed;
    private int _busy;
    private int _asnBusy;
    private int _tickAgain;

    private string? _edgeJson;
    private DateTime? _edgeAt;
    private DateTime? _edgeFailedAt;
    private string _edgeError = "";
    private string? _pulseJson;
    private DateTime? _pulseAt;
    private DateTime? _pulseFailedAt;
    private string _pulseError = "";
    private string? _riotJson;
    private string? _riotRegion;
    private DateTime? _riotAt;
    private DateTime? _riotTriedAt;
    private DateTime? _riotFailedAt;
    private DateTime? _refAt;
    private DateTime? _refFailedAt;
    private IReadOnlyList<string> _wantedIps = Array.Empty<string>();
    private DateTime? _asnFailedAt;

    public CloudPoller(CloudClient client, bool enabled, Func<DateTime>? utcNow = null, CloudPollerOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _enabled = enabled;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _asn = options?.Asn ?? new AsnCache(null, _utcNow);
        _refData = options?.RefData;
        _region = options?.Region;
        if (_region != null)
            _region.Changed += OnRegionChanged;
    }

    /// <summary>Raised on a pool thread after the data, the status or an ASN name changed.</summary>
    public event Action? Changed;

    public bool Enabled
    {
        get
        {
            lock (_lock)
                return _enabled;
        }
    }

    /// <summary>Starts the 30 s timer. The first tick runs after one second.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _timer != null)
                return;
            // Each tick also retries ASN lookups. A lookup that failed at login, before the
            // network was up, must not wait for the next route change.
            _timer = new System.Threading.Timer(_ =>
            {
                _ = TickAsync();
                _ = LookupAsnAsync();
            }, null, TimeSpan.FromSeconds(1), TickInterval);
        }
    }

    public void SetEnabled(bool enabled)
    {
        CancellationTokenSource? old = null;
        lock (_lock)
        {
            if (_disposed || _enabled == enabled)
                return;
            _enabled = enabled;
            if (!enabled)
            {
                old = _cancel;
                _cancel = new CancellationTokenSource();
            }

            _edgeFailedAt = null;
            _pulseFailedAt = null;
            _riotFailedAt = null;
            _refFailedAt = null;
            _asnFailedAt = null;
            _edgeError = "";
            _pulseError = "";
        }

        old?.Cancel();
        old?.Dispose();
        Raise();
        if (enabled)
        {
            _ = TickAsync();
            _ = LookupAsnAsync();
        }
    }

    /// <summary>
    /// The user's Refresh. Edge comes again when it is older than a minute. Pulse comes again
    /// only when it is older than 5 min, because each cold pulse costs the Worker 16 Radar calls.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_lock)
        {
            var now = _utcNow();
            if (_edgeAt is { } e && now - e >= ManualEdgeGap)
                _forceEdge = true;
            if (_edgeFailedAt is { } ef && now - ef >= ManualRetryGap)
                _edgeFailedAt = null;
            if (_pulseFailedAt is { } pf && now - pf >= ManualRetryGap)
                _pulseFailedAt = null;
        }

        return TickAsync();
    }

    /// <summary>Fetches whatever is due. Only one tick runs at a time; a call during a tick runs one more pass after it.</summary>
    public async Task TickAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Volatile.Write(ref _tickAgain, 1);
            return;
        }

        try
        {
            do
            {
                Volatile.Write(ref _tickAgain, 0);
                await TickOnceAsync().ConfigureAwait(false);
            }
            while (Volatile.Read(ref _tickAgain) == 1 && !IsStopped());
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private bool IsStopped()
    {
        lock (_lock)
            return _disposed || !_enabled;
    }

    /// <summary>One pass. True when something was fetched.</summary>
    private async Task<bool> TickOnceAsync()
    {
        bool edgeDue, pulseDue, refDue;
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed || !_enabled)
                return false;
            var now = _utcNow();
            edgeDue = Due(_edgeAt, EdgeInterval, _edgeFailedAt, now) || (_forceEdge && _edgeFailedAt == null);
            _forceEdge = false;
            pulseDue = Due(_pulseAt, PulseInterval, _pulseFailedAt, now);
            refDue = _refData != null && Due(_refAt, RefDataInterval, _refFailedAt, now);
            token = _cancel.Token;
        }

        // Riot status is decided after the edge fetch, which can change the region.
        if (!edgeDue && !pulseDue && !refDue && RiotRegionDue() == null)
            return false;

        Raise(); // status shows "loading" while busy
        if (edgeDue)
            await FetchEdgeAsync(token).ConfigureAwait(false);
        if (pulseDue)
            await FetchPulseAsync(token).ConfigureAwait(false);
        if (refDue)
            await FetchRefDataAsync(token).ConfigureAwait(false);
        if (RiotRegionDue() is { } region)
            await FetchRiotStatusAsync(region, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The public hop addresses of the current route. Addresses that are not cached yet are
    /// looked up in the background. Call it when the hops change.
    /// </summary>
    public void SetHops(IReadOnlyList<string> publicIps)
    {
        lock (_lock)
            _wantedIps = publicIps.Where(PublicIp.IsPublic).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _ = LookupAsnAsync();
    }

    /// <summary>The cached ASN row for an address, or null when it is not known (yet).</summary>
    public AsnResult? Asn(string? ip) => string.IsNullOrWhiteSpace(ip) ? null : _asn.Get(ip.Trim());

    public CloudSnapshot Snapshot()
    {
        var refStatus = _refData?.Status;
        lock (_lock)
        {
            if (!_enabled)
                return new CloudSnapshot(false, "off", "", null, null, null, null, null, null, refStatus);

            var now = _utcNow();
            var error = _pulseError.Length > 0 ? _pulseError : _edgeError;
            var busy = Volatile.Read(ref _busy) != 0;
            var hasData = _edgeJson != null || _pulseJson != null;
            var status = busy ? "loading"
                : error.Length > 0 ? "error"
                : hasData ? "ok"
                : "loading";
            return new CloudSnapshot(true, status, error, Age(_edgeAt, now), Age(_pulseAt, now), _edgeJson, _pulseJson,
                _riotJson, _riotJson == null ? null : Age(_riotAt, now), refStatus);
        }
    }

    public void Dispose()
    {
        System.Threading.Timer? timer;
        CancellationTokenSource cancel;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            timer = _timer;
            _timer = null;
            cancel = _cancel;
        }

        if (_region != null)
            _region.Changed -= OnRegionChanged;
        timer?.Dispose();
        cancel.Cancel();
        cancel.Dispose();
        Changed = null;
        _asn.Flush();
    }

    internal async Task LookupAsnAsync()
    {
        if (Interlocked.CompareExchange(ref _asnBusy, 1, 0) != 0)
            return;

        var changed = false;
        try
        {
            while (true)
            {
                List<string> batch;
                CancellationToken token;
                lock (_lock)
                {
                    if (_disposed || !_enabled)
                        return;
                    if (_asnFailedAt is { } failed && _utcNow() - failed < RetryAfterError)
                        return;
                    var now = _utcNow();
                    batch = _wantedIps
                        .Where(ip => _asn.Get(ip) == null
                            && (!_asnMissAt.TryGetValue(ip, out var missed) || now - missed >= AsnMissRetry))
                        .Take(CloudClient.MaxIpsPerCall)
                        .ToList();
                    token = _cancel.Token;
                }

                if (batch.Count == 0)
                    return;

                IReadOnlyList<AsnResult> rows;
                try
                {
                    rows = await _client.LookupAsnAsync(batch, token).ConfigureAwait(false);
                }
                catch
                {
                    lock (_lock)
                        _asnFailedAt = _utcNow();
                    return;
                }

                lock (_lock)
                {
                    _asnFailedAt = null;
                    var now = _utcNow();
                    foreach (var ip in batch)
                    {
                        var row = rows.FirstOrDefault(r => string.Equals(r.Ip, ip, StringComparison.OrdinalIgnoreCase));
                        if (row?.Asn != null)
                        {
                            _asn.Put(row with { Ip = ip });
                            _asnMissAt.Remove(ip);
                        }
                        else
                        {
                            // A dropped row or a null ASN can be a failed Radar call. Ask again later.
                            _asnMissAt[ip] = now;
                        }
                    }

                    if (_asnMissAt.Count > 512)
                    {
                        var keep = new HashSet<string>(_wantedIps, StringComparer.OrdinalIgnoreCase);
                        foreach (var ip in _asnMissAt.Keys.Where(k => !keep.Contains(k)).ToList())
                            _asnMissAt.Remove(ip);
                    }
                }

                changed = true;
            }
        }
        finally
        {
            Volatile.Write(ref _asnBusy, 0);
            if (changed)
            {
                _asn.Flush();
                Raise();
            }
        }
    }

    private void OnRegionChanged(GeckoPeeks.App.RegionSelection selection)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (!string.Equals(_riotRegion, selection.Region.Id, StringComparison.Ordinal))
            {
                // The old status belongs to another region. Do not show it under the new name.
                _riotJson = null;
                _riotAt = null;
                _riotFailedAt = null;
            }
        }

        _ = TickAsync();
    }

    /// <summary>The region id to fetch Riot status for now, or null.</summary>
    private string? RiotRegionDue()
    {
        if (_region == null)
            return null;
        string id;
        try
        {
            id = _region.Current.Region.Id;
        }
        catch
        {
            return null;
        }

        lock (_lock)
        {
            if (_disposed || !_enabled)
                return null;
            var now = _utcNow();
            if (_riotFailedAt is { } failed && now - failed < RetryAfterError)
                return null;
            if (!string.Equals(_riotRegion, id, StringComparison.Ordinal))
                return _riotTriedAt is { } tried && now - tried < RegionChangeGap ? null : id;
            return _riotAt is not { } at || now - at >= RiotStatusInterval ? id : null;
        }
    }

    private async Task FetchEdgeAsync(CancellationToken token)
    {
        string? json = null;
        try
        {
            json = await _client.GetEdgeJsonAsync(token).ConfigureAwait(false);
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                    return;
                _edgeJson = json;
                _edgeAt = _utcNow();
                _edgeFailedAt = null;
                _edgeError = "";
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                    return;
                _edgeFailedAt = _utcNow();
                _edgeError = Describe(ex);
            }

            return;
        }

        // Outside the lock: the region service raises its own event.
        try
        {
            _region?.OnEdgeJson(json);
        }
        catch
        {
            // Region picking must not stop polling.
        }
    }

    private async Task FetchPulseAsync(CancellationToken token)
    {
        try
        {
            var json = await _client.GetPulseJsonAsync(null, null, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                    return;
                _pulseJson = json;
                _pulseAt = _utcNow();
                _pulseFailedAt = null;
                _pulseError = "";
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                    return;
                _pulseFailedAt = _utcNow();
                _pulseError = Describe(ex);
            }
        }
    }

    private async Task FetchRefDataAsync(CancellationToken token)
    {
        var store = _refData;
        if (store == null)
            return;
        try
        {
            var fetch = await _client.GetRefDataAsync(store.ETag, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return;
            if (fetch.NotModified)
                store.MarkNotModified();
            else if (fetch.Json != null && !store.Apply(fetch.Json, fetch.ETag))
                throw new CloudException("The reference data was not valid.");

            lock (_lock)
            {
                _refAt = _utcNow();
                _refFailedAt = null;
            }
        }
        catch (CloudException ex) when (ex.StatusCode == 404)
        {
            // An old Worker has no /v1/refdata. Keep the data we have and ask again tomorrow.
            lock (_lock)
                _refAt = _utcNow();
        }
        catch
        {
            lock (_lock)
            {
                if (!token.IsCancellationRequested)
                    _refFailedAt = _utcNow();
            }
        }
    }

    private async Task FetchRiotStatusAsync(string region, CancellationToken token)
    {
        lock (_lock)
            _riotTriedAt = _utcNow();
        try
        {
            var json = await _client.GetRiotStatusJsonAsync(region, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                    return;
                _riotJson = json;
                _riotRegion = region;
                _riotAt = _utcNow();
                _riotFailedAt = null;
            }
        }
        catch (CloudException ex) when (ex.StatusCode == 404)
        {
            // An old Worker has no /v1/riot-status. Not an error; ask again later.
            lock (_lock)
            {
                _riotJson = null;
                _riotRegion = region;
                _riotAt = _utcNow();
            }
        }
        catch
        {
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                    return;
                _riotFailedAt = _utcNow();
                if (!string.Equals(_riotRegion, region, StringComparison.Ordinal))
                {
                    _riotJson = null;
                    _riotRegion = region;
                    _riotAt = null;
                }
            }
        }
    }

    private static bool Due(DateTime? lastOk, TimeSpan interval, DateTime? lastFailed, DateTime now)
    {
        if (lastFailed is { } failed && now - failed < RetryAfterError)
            return false;
        return lastOk is not { } ok || now - ok >= interval;
    }

    private static double? Age(DateTime? at, DateTime now) =>
        at is { } t ? Math.Max(0, Math.Round((now - t).TotalSeconds)) : null;

    internal static string Describe(Exception ex) => ex switch
    {
        CloudException cloud => cloud.Message,
        TaskCanceledException or TimeoutException => "The Gecko Peeks service did not answer in time.",
        HttpRequestException => "Could not reach the Gecko Peeks service.",
        _ => "The Gecko Peeks service did not answer.",
    };

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A listener problem must not stop polling.
        }
    }
}
