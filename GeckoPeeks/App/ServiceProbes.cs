using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;

namespace GeckoPeeks.App;

/// <summary>How the Valve relay was chosen. Every result and record names it.</summary>
/// <param name="Candidates">The POP codes that were pinged, nearest first.</param>
public sealed record SdrChoice(int App, long Revision, string PopCode, string PopDesc, string RelayIp, string How,
    IReadOnlyList<string> Candidates, DateTime ChosenUtc);

/// <summary>One test of one service.</summary>
/// <param name="Outcome">measured, noReply, noTarget, cancelled or stopped.</param>
/// <param name="TargetName">What the target is, in words ("Amsterdam (Netherlands)").</param>
/// <param name="OwnerMatch">True when the target's ASN is one of the owner's ASNs; null when the ASN is not known.</param>
public sealed record ServiceResult(
    string ServiceId,
    DateTime AtUtc,
    string Outcome,
    string Reason,
    string TargetIp,
    string TargetName,
    long? TargetAsn,
    bool? OwnerMatch,
    RouteRow? Row,
    IReadOnlyList<HopName> HopNames,
    SdrChoice? Pop);

/// <summary>
/// Tests the other services that are on. The probe scheduler calls <see cref="RunDueAsync"/> from
/// its one runner task, after the Riot test, so ICMP never overlaps. Each service has its own due
/// time (every 15 minutes), so a League match never makes the other services run every 5 minutes.
/// </summary>
public sealed class ServiceProbes
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>A service with no target yet (no relay list, no location) is tried again after this.</summary>
    public static readonly TimeSpan RetryNoTarget = TimeSpan.FromMinutes(2);

    /// <summary>A result older than this is not shown.</summary>
    public static readonly TimeSpan ResultMaxAge = TimeSpan.FromMinutes(60);

    /// <summary>A relay choice is kept this long, unless the list changes or the relay stops answering.</summary>
    public static readonly TimeSpan ChoiceMaxAge = TimeSpan.FromHours(24);

    public const int Candidates = 3;
    public const int PickPings = 3;

    private readonly object _lock = new();
    private readonly Func<RefData> _refData;
    private readonly ServiceSettings _settings;
    private readonly ServiceFeeds _feeds;
    private readonly IHopResolver _resolver;
    private readonly DiagnosticsStore _diagnostics;
    private readonly Func<bool> _cloudOn;
    private readonly Func<(double Lat, double Lon)?> _location;
    private readonly Dictionary<string, ServiceResult> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _nextDue = new(StringComparer.Ordinal);
    private readonly Dictionary<int, SdrChoice> _choice = new();
    private readonly Dictionary<int, (double Lat, double Lon)> _choiceHere = new();

    /// <summary>A Cloudflare location this far from the one a relay was chosen for chooses again (VPN, travel).</summary>
    public const double MovedKm = 300;
    private readonly Dictionary<string, string> _lastRecordedNoTarget = new(StringComparer.Ordinal);

    public ServiceProbes(Func<RefData> refData, ServiceSettings settings, ServiceFeeds feeds, IHopResolver resolver,
        DiagnosticsStore diagnostics, Func<bool> cloudOn, Func<(double Lat, double Lon)?> location)
    {
        _refData = refData;
        _settings = settings;
        _feeds = feeds;
        _resolver = resolver;
        _diagnostics = diagnostics;
        _cloudOn = cloudOn;
        _location = location;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Test seams. Null uses the real clock, ICMP and test runner.</summary>
    internal Func<DateTime>? UtcNow { get; set; }
    internal Func<string, CancellationToken, Task<double?>>? PingAvg { get; set; }
    internal Func<ProbeRequest, IProgress<ProbeProgress>?, CancellationToken, Task<RouteRow>>? Runner { get; set; }

    /// <summary>Raised after a result changed.</summary>
    public event Action? Changed;

    private DateTime Now => UtcNow?.Invoke() ?? DateTime.UtcNow;

    /// <summary>The services of the refdata that this build can show.</summary>
    public IReadOnlyList<ServiceInfo> Known => _refData().Services;

    /// <summary>The known services that are on.</summary>
    public IReadOnlyList<ServiceInfo> Enabled() => Known.Where(s => _settings.IsOn(s.Id)).ToList();

    public ServiceResult? Last(string id)
    {
        lock (_lock)
            return _last.TryGetValue(id, out var r) ? r : null;
    }

    /// <summary>The newest result if it is younger than <see cref="ResultMaxAge"/>, else null.</summary>
    public ServiceResult? Fresh(string id)
    {
        var r = Last(id);
        return r != null && Now - r.AtUtc <= ResultMaxAge ? r : null;
    }

    public bool AnyDue()
    {
        var now = Now;
        lock (_lock)
            return Enabled().Any(s => !_nextDue.TryGetValue(s.Id, out var due) || now >= due);
    }

    /// <summary>When the next service test is due, for the window. Null when none is on.</summary>
    public DateTime? NextDueUtc()
    {
        lock (_lock)
        {
            var times = Enabled().Select(s => _nextDue.TryGetValue(s.Id, out var due) ? due : Now).ToList();
            return times.Count == 0 ? null : times.Min();
        }
    }

    private void OnSettingsChanged()
    {
        lock (_lock)
        {
            // A service that was just turned on runs at the next check.
            foreach (var id in _nextDue.Keys.Where(id => !_settings.IsOn(id)).ToList())
                _nextDue.Remove(id);
        }

        _feeds.Poke();
        RaiseChanged();
    }

    /// <summary>
    /// Runs every service that is on and due (or every service that is on, when <paramref name="all"/>).
    /// Never throws; a cancelled run stops after the current service.
    /// </summary>
    public async Task RunDueAsync(bool all, Action<string> phase, CancellationToken token)
    {
        foreach (var service in Enabled())
        {
            if (token.IsCancellationRequested)
                return;
            bool due;
            lock (_lock)
                due = all || !_nextDue.TryGetValue(service.Id, out var at) || Now >= at;
            if (!due)
                continue;

            ServiceResult result;
            try
            {
                result = await RunOneAsync(service, text => phase(service.Name + ": " + text), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Result(service, "cancelled", "The test was cancelled.");
            }
            catch (Exception ex)
            {
                result = Result(service, "stopped", "The test stopped: " + ex.Message);
            }

            Keep(service, result);
        }
    }

    private void Keep(ServiceInfo service, ServiceResult result)
    {
        var record = true;
        lock (_lock)
        {
            _last[service.Id] = result;
            _nextDue[service.Id] = result.AtUtc + (result.Outcome is "measured" or "noReply" or "stopped" ? Interval : RetryNoTarget);

            // The same "no target" reason again and again is kept once, not every 2 minutes.
            if (result.Outcome == "noTarget")
            {
                record = !(_lastRecordedNoTarget.TryGetValue(service.Id, out var was) && was == result.Reason);
                _lastRecordedNoTarget[service.Id] = result.Reason;
            }
            else
            {
                _lastRecordedNoTarget.Remove(service.Id);
            }

            // A relay that gave no reply is chosen again next time.
            if (result.Pop is { } pop && result.Row is { } row && Num(row.RiotHopAvg) == null)
                _choice.Remove(pop.App);
        }

        if (record)
            _diagnostics.RecordService(service, result);
        RaiseChanged();
    }

    private ServiceResult Result(ServiceInfo s, string outcome, string reason) =>
        new(s.Id, Now, outcome, reason, "", "", null, null, null, Array.Empty<HopName>(), null);

    private async Task<ServiceResult> RunOneAsync(ServiceInfo service, Action<string> phase, CancellationToken token)
    {
        if (service.Target.Kind != ServiceInfo.KindSdr || service.Target.AppId is not { } app)
            return Result(service, "noTarget", "This app version cannot measure this target.");
        if (!_cloudOn())
            return Result(service, "noTarget", "Cloudflare live data is off. Valve's relay list comes through the Gecko Peeks service, so there is no target.");
        var config = _feeds.Sdr(app);
        if (config == null)
            return Result(service, "noTarget", "Valve's relay list is not available yet.");
        if (_location() is not { } here)
            return Result(service, "noTarget", "Your approximate location is not known yet, so the nearest relay cannot be chosen.");

        phase("Choosing a relay");
        var pop = await ChooseAsync(config, here, token).ConfigureAwait(false);
        phase("Tracing " + pop.RelayIp);
        var refData = _refData();
        var request = new ProbeRequest(false, pop.RelayIp, _resolver, refData, "Valve relay");
        var progress = new PhaseProgress(phase);
        var row = await (Runner ?? TestRunner.RunAsync)(request, progress, token).ConfigureAwait(false);
        if (row.Cancelled)
            return Result(service, "cancelled", "The test was cancelled.") with { TargetIp = pop.RelayIp, TargetName = pop.PopDesc, Pop = pop };

        // An error before any hop (the network dropped, a socket failed) is not a measurement.
        if (row.Hops.Count == 0)
            return Result(service, "stopped", string.IsNullOrWhiteSpace(row.Error) ? "The test recorded nothing." : "The test stopped: " + row.Error)
                with { TargetIp = pop.RelayIp, TargetName = pop.PopDesc, Pop = pop, Row = row };

        var targetAsn = await TargetAsnAsync(row, pop.RelayIp, token).ConfigureAwait(false);
        bool? ownerMatch = targetAsn is { } asn ? service.Owner.Asns.Contains(asn) : null;
        var names = RouteClassifier.NameHops(row.Hops, row.HopAsns, refData);
        var silent = row.Hops.Count > 0 && row.Hops.All(h => !h.Responded);
        return new ServiceResult(service.Id, Now, silent ? "noReply" : "measured",
            silent ? "No router answered." : string.IsNullOrWhiteSpace(row.Error) ? "" : "Problem during the test: " + row.Error,
            pop.RelayIp, pop.PopDesc, targetAsn, ownerMatch, row, names, pop);
    }

    /// <summary>The target's ASN from the test, else one lookup (the cache or the Worker).</summary>
    private async Task<long?> TargetAsnAsync(RouteRow row, string ip, CancellationToken token)
    {
        if (row.HopAsns.TryGetValue(ip, out var known))
            return known;
        try
        {
            var found = await _resolver.AsnAsync(new[] { ip }, token).ConfigureAwait(false);
            return found != null && found.TryGetValue(ip, out var asn) ? asn : null;
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

    /// <summary>
    /// The relay to trace: the kept choice while it holds, else the lowest ping among the first
    /// relay of the <see cref="Candidates"/> POPs nearest to the Cloudflare location. When none of
    /// them answers, the nearest by distance.
    /// </summary>
    internal async Task<SdrChoice> ChooseAsync(SdrConfig config, (double Lat, double Lon) here, CancellationToken token)
    {
        lock (_lock)
        {
            if (_choice.TryGetValue(config.App, out var kept) && kept.Revision == config.Revision
                && Now - kept.ChosenUtc <= ChoiceMaxAge
                && _choiceHere.TryGetValue(config.App, out var then)
                && DistanceKm(then.Lat, then.Lon, here.Lat, here.Lon) < MovedKm
                && config.Pops.Any(p => p.Code == kept.PopCode && p.Relays.Contains(kept.RelayIp)))
                return kept;
        }

        var near = config.Pops
            .OrderBy(p => DistanceKm(here.Lat, here.Lon, p.Lat, p.Lon))
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .Take(Candidates)
            .ToList();
        (SdrPop Pop, double Avg)? best = null;
        foreach (var pop in near)
        {
            var avg = await (PingAvg ?? PingAverageAsync)(pop.Relays[0], token).ConfigureAwait(false);
            if (avg is { } ms && (best == null || ms < best.Value.Avg))
                best = (pop, ms);
        }

        var choice = best is { } b
            ? new SdrChoice(config.App, config.Revision, b.Pop.Code, b.Pop.Desc, b.Pop.Relays[0],
                $"Lowest ping of the {near.Count} relays nearest to your Cloudflare location.",
                near.Select(p => p.Code).ToList(), Now)
            : new SdrChoice(config.App, config.Revision, near[0].Code, near[0].Desc, near[0].Relays[0],
                $"None of the {near.Count} nearest relays answered ping, so the nearest by distance was used.",
                near.Select(p => p.Code).ToList(), Now);
        lock (_lock)
        {
            _choice[config.App] = choice;
            _choiceHere[config.App] = here;
        }

        return choice;
    }

    /// <summary>Great-circle distance in km.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Average of the answered echoes out of <see cref="PickPings"/>, or null when none answered.</summary>
    private static async Task<double?> PingAverageAsync(string ip, CancellationToken token)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return null;
        using var ping = new Ping();
        var buffer = new byte[32];
        var times = new List<double>();
        for (var i = 0; i < PickPings; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(TestRunner.HopTimeoutMs), buffer,
                    new PingOptions(), token).ConfigureAwait(false);
                clock.Stop();
                if (reply.Status == IPStatus.Success)
                    times.Add(IcmpTiming.ResolveRttMs(reply.RoundtripTime, clock.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A lost echo.
            }
        }

        return times.Count == 0 ? null : times.Average();
    }

    internal static double? Num(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // The window is optional.
        }
    }

    /// <summary>Hands only the phase text on; service hops never reach the Riot route view.</summary>
    private sealed class PhaseProgress : IProgress<ProbeProgress>
    {
        private readonly Action<string> _phase;

        public PhaseProgress(Action<string> phase) => _phase = phase;

        public void Report(ProbeProgress value)
        {
            try
            {
                _phase(value.Phase);
            }
            catch
            {
                // Progress must not fail the test.
            }
        }
    }
}
