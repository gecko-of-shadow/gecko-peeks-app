using System.Text.Json;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Cloud;

/// <summary>One relay POP from the Worker's /v1/sdr answer.</summary>
public sealed record SdrPop(string Code, string Desc, double Lat, double Lon, IReadOnlyList<string> Relays);

/// <summary>Valve's relay list for one app, as the Worker sent it.</summary>
public sealed record SdrConfig(int App, long Revision, DateTime FetchedUtc, IReadOnlyList<SdrPop> Pops)
{
    /// <summary>Parses the Worker answer. Null when the shape is not the one the Worker sends.</summary>
    public static SdrConfig? Parse(string? json, DateTime fetchedUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("app", out var appEl) || !appEl.TryGetInt32(out var app)
                || !root.TryGetProperty("revision", out var revEl) || !revEl.TryGetInt64(out var revision)
                || !root.TryGetProperty("pops", out var popsEl) || popsEl.ValueKind != JsonValueKind.Array)
                return null;
            var pops = new List<SdrPop>();
            foreach (var p in popsEl.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                    continue;
                var code = p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                var desc = p.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                if (code.Length is 0 or > 8 || desc.Length is 0 or > 80
                    || !p.TryGetProperty("lat", out var lat) || !lat.TryGetDouble(out var la) || Math.Abs(la) > 90
                    || !p.TryGetProperty("lon", out var lon) || !lon.TryGetDouble(out var lo) || Math.Abs(lo) > 180
                    || !p.TryGetProperty("relays", out var rel) || rel.ValueKind != JsonValueKind.Array)
                    continue;
                var relays = rel.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String && PublicIp.IsPublic(r.GetString()) && System.Net.IPAddress.TryParse(r.GetString(), out var ip)
                        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(r => r.GetString()!)
                    .Take(4)
                    .ToList();
                if (relays.Count > 0)
                    pops.Add(new SdrPop(code, desc, la, lo, relays));
            }

            return pops.Count == 0 ? null : new SdrConfig(app, revision, fetchedUtc, pops);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A status feed answer as the Worker sent it, with the time it came.</summary>
public sealed record FeedAnswer(string FeedId, string Json, DateTime FetchedUtc, bool HasStatus);

/// <summary>
/// Fetches what the services that are on need, through the Worker only: Valve's relay list (once a
/// day) and each status feed (every 5 minutes). Nothing is fetched while Cloudflare live data is
/// off; the data is then dropped, so the window shows "not available". A failed call keeps no old
/// data past its age limit and is tried again after <see cref="RetryAfterError"/>.
/// </summary>
public sealed class ServiceFeeds : IDisposable
{
    public static readonly TimeSpan SdrInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>A relay list older than this is not used.</summary>
    public static readonly TimeSpan SdrMaxAge = TimeSpan.FromHours(48);

    /// <summary>A status answer older than this is not shown.</summary>
    public static readonly TimeSpan StatusMaxAge = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private readonly CloudClient _client;
    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<ServiceInfo>> _wanted;
    private readonly Func<string?> _continent;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<int, SdrConfig> _sdr = new();
    private readonly Dictionary<string, FeedAnswer> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _failedAt = new(StringComparer.Ordinal);
    private System.Threading.Timer? _timer;
    private int _busy;
    private bool _disposed;

    /// <param name="enabled">The Cloudflare live data setting.</param>
    /// <param name="wanted">The services that are on now.</param>
    /// <param name="continent">The edge continent ("NA", "EU"...), for feeds chosen by continent.</param>
    public ServiceFeeds(CloudClient client, Func<bool> enabled, Func<IReadOnlyList<ServiceInfo>> wanted,
        Func<string?> continent, Func<DateTime>? utcNow = null)
    {
        _client = client;
        _enabled = enabled;
        _wanted = wanted;
        _continent = continent;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised on a pool thread after new data came or old data was dropped.</summary>
    public event Action? Changed;

    /// <summary>Raised for each new answer, to keep it in the diagnostics store: (kind, json).</summary>
    public event Action<string, string>? Answered;

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _timer != null)
                return;
            _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(2), TickInterval);
        }
    }

    /// <summary>Ask for a pass now, for example after a service was turned on.</summary>
    public void Poke() => _ = TickAsync();

    /// <summary>The relay list for one app if it is fresh enough, else null.</summary>
    public SdrConfig? Sdr(int app)
    {
        lock (_lock)
            return _sdr.TryGetValue(app, out var c) && _utcNow() - c.FetchedUtc <= SdrMaxAge && _enabled() ? c : null;
    }

    /// <summary>The feed id a service reads now: its fixed feed, else the one for the edge continent.</summary>
    public string? FeedFor(ServiceInfo service)
    {
        if (service.Status is not { } st)
            return null;
        var continent = (_continent() ?? "").Trim().ToUpperInvariant();
        if (continent.Length == 2 && st.FeedByContinent.TryGetValue(continent, out var byContinent))
            return byContinent;
        return st.Feed;
    }

    /// <summary>The newest answer of a feed if it is fresh enough, else null.</summary>
    public FeedAnswer? Status(string? feedId)
    {
        if (feedId == null)
            return null;
        lock (_lock)
            return _status.TryGetValue(feedId, out var a) && _utcNow() - a.FetchedUtc <= StatusMaxAge && _enabled() ? a : null;
    }

    public async Task TickAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        var changed = false;
        try
        {
            if (!_enabled())
            {
                lock (_lock)
                {
                    changed = _sdr.Count > 0 || _status.Count > 0;
                    _sdr.Clear();
                    _status.Clear();
                    _failedAt.Clear();
                }

                return;
            }

            IReadOnlyList<ServiceInfo> services;
            try
            {
                services = _wanted();
            }
            catch
            {
                return;
            }

            foreach (var app in services.Where(s => s.Target.Kind == ServiceInfo.KindSdr && s.Target.AppId != null)
                         .Select(s => s.Target.AppId!.Value).Distinct())
            {
                var key = "sdr-" + app;
                bool due;
                lock (_lock)
                    due = Due(key, _sdr.TryGetValue(app, out var have) ? have.FetchedUtc : null, SdrInterval);
                if (!due)
                    continue;
                try
                {
                    var json = await _client.GetSdrJsonAsync(app, CancellationToken.None).ConfigureAwait(false);
                    var config = SdrConfig.Parse(json, _utcNow());
                    if (config == null || config.App != app)
                        throw new CloudException("The relay list was not in the known shape.");
                    lock (_lock)
                    {
                        _sdr[app] = config;
                        _failedAt.Remove(key);
                    }

                    Raise(key, json);
                    changed = true;
                }
                catch
                {
                    lock (_lock)
                        _failedAt[key] = _utcNow();
                }
            }

            foreach (var feed in services.Select(FeedFor).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var key = "status-" + feed;
                bool due;
                lock (_lock)
                    due = Due(key, _status.TryGetValue(feed, out var have) ? have.FetchedUtc : null, StatusInterval);
                if (!due)
                    continue;
                try
                {
                    var json = await _client.GetServiceStatusJsonAsync(feed, CancellationToken.None).ConfigureAwait(false);
                    var hasStatus = HasStatus(json, feed);
                    lock (_lock)
                    {
                        // A feed the Worker could not read is a fresh "not available", not old data.
                        _status[feed] = new FeedAnswer(feed, json, _utcNow(), hasStatus);
                        if (hasStatus)
                            _failedAt.Remove(key);
                        else
                            _failedAt[key] = _utcNow();
                    }

                    Raise(key, json);
                    changed = true;
                }
                catch
                {
                    lock (_lock)
                        _failedAt[key] = _utcNow();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            if (changed)
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
    }

    /// <summary>True when the answer names this feed and has a status object.</summary>
    internal static bool HasStatus(string json, string feed)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == feed
                && root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool Due(string key, DateTime? lastOk, TimeSpan interval)
    {
        var now = _utcNow();
        if (_failedAt.TryGetValue(key, out var failed) && now - failed < RetryAfterError)
            return false;
        return lastOk is not { } ok || now - ok >= interval;
    }

    private void Raise(string kind, string json)
    {
        try
        {
            Answered?.Invoke(kind, json);
        }
        catch
        {
            // Diagnostics never stop polling.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        Changed = null;
        Answered = null;
    }
}
