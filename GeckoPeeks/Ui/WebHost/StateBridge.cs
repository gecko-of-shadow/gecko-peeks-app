using System.Reflection;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using GeckoPeeks.Ui.State;

namespace GeckoPeeks.Ui.WebHost;

/// <summary>
/// Builds the Contract 2 state from the probe, the log, the cloud poller, the updater and the
/// settings, and posts it at most four times a second while the page is listening. The log is
/// read off the UI thread, and only again when its revision changed.
/// </summary>
internal sealed class StateBridge : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Control _ui;
    private readonly ProbeScheduler _scheduler;
    private readonly CoreServices _core;
    private readonly CloudPoller _cloud;
    private readonly HostSettings _settings;
    private readonly StartupToggle _startup;
    private readonly WebSurface _surface;
    private readonly bool _demo;
    private readonly System.Windows.Forms.Timer _debounce;
    private UpdateService? _updates;
    private string _historyJson = "[]";
    private int _historyTotal;
    private string? _historyPosted;
    private HistoryRelabel.Lookups _lookups = HistoryRelabel.Lookups.None;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _lookupTries = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _localTried = new(StringComparer.OrdinalIgnoreCase);
    private bool? _lookupsCloud;
    private int _refDataGeneration;
    private int _historyRevision = int.MinValue;
    private int _historyLoading;
    private int _hopsRevision = int.MinValue;
    private bool _startupChecked;
    private bool _dirtyWhileHidden;
    private bool _disposed;

    public StateBridge(
        Control ui,
        ProbeScheduler scheduler,
        CoreServices core,
        HostSettings settings,
        StartupToggle startup,
        WebSurface surface,
        bool demo)
    {
        _ui = ui;
        _scheduler = scheduler;
        _core = core;
        _cloud = core.Cloud;
        _settings = settings;
        _startup = startup;
        _surface = surface;
        _demo = demo;
        _debounce = new System.Windows.Forms.Timer { Interval = (int)Debounce.TotalMilliseconds };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            PostNow();
        };

        _scheduler.Updated += OnProbeUpdated;
        _cloud.Changed += OnCoreChanged;
        _core.Region.Changed += OnRegionChanged;
        _core.RefData.Changed += OnRefDataChanged;
        if (_demo)
        {
            // Demo: fixed history and no ASN lookups for the demo hops.
            _historyJson = UiStateJson.History(DemoData.History(DateTime.Now));
        }
        else
        {
            OnProbeUpdated(_scheduler.Capture());
        }
    }

    /// <summary>True when a state change came while the window was hidden.</summary>
    public bool DirtyWhileHidden => _dirtyWhileHidden;

    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public UpdateService? Updates
    {
        get => _updates;
        set
        {
            if (_updates != null)
                _updates.Changed -= OnUpdateChanged;
            _updates = value;
            if (_updates != null)
                _updates.Changed += OnUpdateChanged;
            MarkDirty();
        }
    }

    /// <summary>The page said ready: read the settings fresh and send everything now.</summary>
    public void OnPageReady()
    {
        // A new page has no history yet.
        _historyPosted = null;
        _startupChecked = _startup.Checked;
        LoadHistoryIfStale();
        PostNow();
    }

    /// <summary>The web view is gone: drop the cached history so the tray copy stays small.</summary>
    public void ReleaseCache()
    {
        _debounce.Stop();
        _historyPosted = null;
        if (_demo)
            return;
        _historyJson = "[]";
        _historyTotal = 0;
        _historyRevision = int.MinValue;
    }

    public void RefreshStartup()
    {
        _startupChecked = _startup.Checked;
        MarkDirty();
    }

    /// <summary>While the window is hidden this only remembers the change; the show posts once.</summary>
    public void MarkDirty()
    {
        if (_disposed || !_surface.PageReady)
            return;
        if (_surface.IsHidden)
        {
            _dirtyWhileHidden = true;
            return;
        }

        if (!_debounce.Enabled)
            _debounce.Start();
    }

    public void Toast(string text, string tone = "normal") => _surface.Post(UiStateJson.Toast(text, tone));

    public void PostNow()
    {
        if (_disposed || !_surface.PageReady)
            return;
        _debounce.Stop();
        if (_surface.IsHidden)
        {
            _dirtyWhileHidden = true;
            return;
        }

        _dirtyWhileHidden = false;
        try
        {
            // The history goes out only when the page does not have this copy yet.
            var history = _historyJson;
            var send = !ReferenceEquals(history, _historyPosted);
            if (_surface.Post(BuildJson(send)) && send)
                _historyPosted = history;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    public string BuildJson(bool includeHistory = true)
    {
        var snapshot = _scheduler.Capture();
        var selection = _core.Region.Current;
        ProbeView probe;
        LogView log;
        CloudView cloud;
        string lastTestTarget;
        if (_demo)
        {
            // Demo: fixed route, fixed cloud data, no lookups and no live calls.
            probe = DemoData.Probe(DateTime.Now);
            var saved = DateTime.Now.AddMinutes(-6);
            log = UiMapping.Log(false, LogHealth.Saved, snapshot.LogFolder, snapshot.LogFileName,
                LogStatusText.Saved(saved, AppendResult.Main), "", "", 0);
            cloud = DemoData.Cloud(DateTime.UtcNow, selection.Region, RefData.BuiltIn);
            lastTestTarget = DemoData.TargetIp;
        }
        else
        {
            probe = ProbeMapper.Map(snapshot, _cloud);
            log = UiMapping.Log(snapshot.Paused, snapshot.LogHealth, snapshot.LogFolder, snapshot.LogFileName,
                snapshot.LogWriteStatus, snapshot.ReadProblem, snapshot.LogLocation, snapshot.LogUnsavedRows);
            var c = _cloud.Snapshot();
            cloud = new CloudView(c.Enabled, c.Status, c.Error, c.EdgeAgeSec, c.PulseAgeSec, c.EdgeJson, c.PulseJson,
                c.RiotStatusJson, c.RiotStatusAgeSec, UiMapping.RefDataInfo(c.RefData ?? _core.RefData.Status));
            lastTestTarget = snapshot.TestTargetIp;
        }

        var status = _updates?.Status ?? new UpdateStatus(UpdateState.Idle, AppUpdate.CurrentVersion(), null, null, "",
            AppUpdate.ReleasesPageUrl, "", null, false);
        var state = new UiState(
            new AppInfo(Version, AppIsolation.Profile.ToString(), _demo),
            probe,
            log,
            Array.Empty<HistoryRow>(),
            cloud,
            UiMapping.Update(status),
            new SettingsView(_startupChecked, _updates?.AutoInstall ?? false, _demo || _settings.CloudEnabled,
                UiMapping.RegionSetting(selection)),
            UiMapping.Region(selection, lastTestTarget),
            UiMapping.Regions(_core.RefData.Current));
        return UiStateJson.Build(state, _historyJson, includeHistory, _demo ? null : _historyTotal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _debounce.Stop();
        _debounce.Dispose();
        _scheduler.Updated -= OnProbeUpdated;
        _cloud.Changed -= OnCoreChanged;
        _core.Region.Changed -= OnRegionChanged;
        _core.RefData.Changed -= OnRefDataChanged;
        if (_updates != null)
            _updates.Changed -= OnUpdateChanged;
    }

    private void OnProbeUpdated(ProbeSnapshot snapshot)
    {
        if (_disposed || _ui.IsDisposed)
            return;
        if (_ui.InvokeRequired)
        {
            _ui.BeginInvoke(new Action<ProbeSnapshot>(OnProbeUpdated), snapshot);
            return;
        }

        if (!_demo && snapshot.HopsRevision != _hopsRevision)
        {
            _hopsRevision = snapshot.HopsRevision;
            _cloud.SetHops(ProbeMapper.PublicHops(snapshot));
        }

        if (!_surface.PageReady)
            return;
        LoadHistoryIfStale(snapshot.LogRevision);
        MarkDirty();
    }

    /// <summary>
    /// Cloud poller, region and refdata events come on any thread. A change of the Cloudflare
    /// live data setting also names the old rows again, with the lookups that setting allows.
    /// </summary>
    private void OnCoreChanged() => OnUi(() =>
    {
        if (!_demo && _lookupsCloud is { } cloud && cloud != _settings.CloudEnabled)
        {
            // A load already running used the old setting: the generation check reloads after it.
            Interlocked.Increment(ref _refDataGeneration);
            _historyRevision = int.MinValue;
            if (_surface.PageReady)
                LoadHistoryIfStale();
        }

        MarkDirty();
    });

    private void OnRegionChanged(RegionSelection selection) => OnUi(MarkDirty);

    /// <summary>New reference data can rename handoff cities: read the history again.</summary>
    private void OnRefDataChanged() => OnUi(() =>
    {
        Interlocked.Increment(ref _refDataGeneration);
        if (!_demo)
        {
            _historyRevision = int.MinValue;
            if (_surface.PageReady)
                LoadHistoryIfStale();
        }

        MarkDirty();
    });

    private void OnUi(Action action)
    {
        if (_disposed || _ui.IsDisposed || !_ui.IsHandleCreated)
            return;
        try
        {
            _ui.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void OnUpdateChanged(UpdateStatus status) => MarkDirty();

    private void LoadHistoryIfStale(int? revision = null)
    {
        if (_demo)
            return;
        var wanted = revision ?? _scheduler.Capture().LogRevision;
        if (wanted == _historyRevision || Interlocked.CompareExchange(ref _historyLoading, 1, 0) != 0)
            return;

        var log = _scheduler.Log;
        var resolver = _core.Resolver;
        var generation = Volatile.Read(ref _refDataGeneration);

        // Answers found with Cloudflare live data off (system DNS only) are not kept after it is
        // turned on, and the other way round: start the lookups again.
        var cloudOn = _settings.CloudEnabled;
        if (_lookupsCloud != cloudOn)
        {
            Volatile.Write(ref _lookups, HistoryRelabel.Lookups.None);
            _lookupTries.Clear();
            _localTried.Clear();
            _lookupsCloud = cloudOn;
        }

        var tries = _lookupTries;
        var localTried = _localTried;
        _ = Task.Run(async () =>
        {
            HistoryJson first;
            IReadOnlyList<RouteRow> rows;
            HistoryRelabel.Lookups known;
            try
            {
                rows = log.ReadAll();

                // Step 1: show the rows at once, named with what earlier loads already answered.
                known = Volatile.Read(ref _lookups);
                first = BuildHistory(rows, known);
            }
            catch
            {
                // The log could not be read this time: keep what the page shows, try again later.
                return null;
            }

            var missing = HistoryRelabel.AddressesToLookUp(rows, ip =>
                known.Done.Contains(ip) || (tries.TryGetValue(ip, out var n) && n >= MaxLookupTries));
            var local = HistoryRelabel.LocalAddresses(rows).Where(ip => !localTried.ContainsKey(ip)).ToList();
            if (missing.Count == 0 && local.Count == 0)
                return first;
            OnUi(() => ShowInterim(first));

            // Step 2: look up the routers not answered yet, then show the rows again. A failure
            // here keeps the step 1 rows.
            try
            {
                var sent = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var merged = await LookUpAsync(missing, local, known, resolver, sent, localTried).ConfigureAwait(false);

                // Only a router whose lookup really went out and got no answer uses up a try.
                foreach (var ip in missing)
                {
                    if (sent.ContainsKey(ip) && !merged.Done.Contains(ip))
                        tries.AddOrUpdate(ip, 1, (_, n) => n + 1);
                }

                Volatile.Write(ref _lookups, merged);
                return BuildHistory(rows, merged);
            }
            catch
            {
                return first;
            }
        }).ContinueWith(task =>
        {
            void Apply()
            {
                Volatile.Write(ref _historyLoading, 0);
                if (_disposed)
                    return;
                if (task.Result is not { } loaded)
                    return; // read failed: the revision stays stale, so the next update reads again
                SetHistory(loaded);

                // New reference data came during the load: this copy used the old data, read again.
                if (generation != Volatile.Read(ref _refDataGeneration))
                {
                    _historyRevision = int.MinValue;
                    if (_surface.PageReady)
                        LoadHistoryIfStale();
                    return;
                }

                _historyRevision = wanted;

                // A test finished while the log was being read: read again.
                var now = _scheduler.Capture().LogRevision;
                if (now != wanted && _surface.PageReady)
                    LoadHistoryIfStale(now);
            }

            if (_ui.IsDisposed || !_ui.IsHandleCreated)
            {
                Volatile.Write(ref _historyLoading, 0);
                return;
            }

            try
            {
                _ui.BeginInvoke(new Action(Apply));
            }
            catch (InvalidOperationException)
            {
                Volatile.Write(ref _historyLoading, 0);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>The history JSON and how many log rows had a readable time (all of them, not only the rows sent).</summary>
    private sealed record HistoryJson(string Json, int Total);

    private static HistoryJson BuildHistory(IReadOnlyList<RouteRow> rows, HistoryRelabel.Lookups lookups)
    {
        var refData = RefData.Current;
        var mapped = UiMapping.HistoryRows(rows, row => HistoryRelabel.Name(row, lookups, refData));
        var total = rows.Count(r => UiMapping.HistoryTime(r.Timestamp) != null);
        return new HistoryJson(UiStateJson.History(mapped), total);
    }

    private void SetHistory(HistoryJson history)
    {
        _historyJson = history.Json;
        _historyTotal = history.Total;
        MarkDirty();
    }

    /// <summary>Step 1 of a load: the rows go out before the router lookups finish.</summary>
    private void ShowInterim(HistoryJson history)
    {
        if (_disposed)
            return;
        SetHistory(history);
    }

    /// <summary>Router name lookups for old rows stop after this; the ASN lookups have their own time after that.</summary>
    private static readonly TimeSpan NameBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AsnBudget = TimeSpan.FromSeconds(20);

    /// <summary>A router not answered after this many loads is given up for this session; its rows stay Unknown.</summary>
    private const int MaxLookupTries = 2;

    /// <summary>
    /// Looks up router names and network numbers for <paramref name="ips"/> with the resolver a live
    /// test uses (so the Cloudflare live data setting applies), and adds them to <paramref name="known"/>.
    /// A router is done only when it was answered: its network number came back and its name lookup
    /// finished. The network numbers go in batches of <see cref="CloudClient.MaxIpsPerCall"/>, the
    /// most one Worker call takes.
    /// </summary>
    private static async Task<HistoryRelabel.Lookups> LookUpAsync(
        IReadOnlyList<string> ips,
        IReadOnlyList<string> localIps,
        HistoryRelabel.Lookups known,
        IHopResolver resolver,
        System.Collections.Concurrent.ConcurrentDictionary<string, bool> sent,
        System.Collections.Concurrent.ConcurrentDictionary<string, bool> localTried)
    {
        var hosts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var named = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using (var budget = new CancellationTokenSource(NameBudget))
        using (var gate = new SemaphoreSlim(6))
        {
            // Hop 1, the gateway and private hops: this PC's DNS only (allowDoh false), as in a live test.
            var localTasks = localIps.Select(async ip =>
            {
                try
                {
                    await gate.WaitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    var answer = await resolver.ReverseWithStatusAsync(ip, allowDoh: false, budget.Token).ConfigureAwait(false);
                    if (answer.Status == PtrStatus.Name && !string.IsNullOrWhiteSpace(answer.Host))
                        hosts[ip] = answer.Host;
                    localTried[ip] = true; // tried once, as a live test does; not again this session
                }
                catch
                {
                    // Best effort, as in a live test.
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(localTasks.Concat(ips.Select(async ip =>
            {
                try
                {
                    await gate.WaitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    // Only a real answer counts: a name, or a definite "this router has no name".
                    // A timeout or a DNS failure is not done, so its rows stay Unknown.
                    sent[ip] = true;
                    var answer = await resolver.ReverseWithStatusAsync(ip, allowDoh: true, budget.Token).ConfigureAwait(false);
                    if (answer.Status == PtrStatus.Name && !string.IsNullOrWhiteSpace(answer.Host))
                        hosts[ip] = answer.Host;
                    if (answer.Status != PtrStatus.Unknown)
                        named[ip] = true;
                }
                catch
                {
                    // Not done: a row with this router stays as saved.
                }
                finally
                {
                    gate.Release();
                }
            }))).ConfigureAwait(false);
        }

        var asns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var budget = new CancellationTokenSource(AsnBudget))
        {
            foreach (var batch in ips.Chunk(CloudClient.MaxIpsPerCall))
            {
                if (budget.IsCancellationRequested)
                    break;
                try
                {
                    foreach (var (ip, asn) in await resolver.AsnAsync(batch, budget.Token).ConfigureAwait(false))
                        asns[ip] = asn;
                }
                catch
                {
                    // This batch is not answered; its routers are tried again on a later load.
                }
            }
        }

        var hostMap = new Dictionary<string, string>(known.HostByIp, StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, host) in hosts)
            hostMap[ip] = host;
        var asnMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ip, asn) in known.AsnByIp)
            asnMap[ip] = asn;
        foreach (var (ip, asn) in asns)
            asnMap[ip] = asn;
        var done = new HashSet<string>(known.Done, StringComparer.OrdinalIgnoreCase);
        foreach (var ip in ips)
        {
            if (named.ContainsKey(ip) && asns.ContainsKey(ip))
                done.Add(ip);
        }

        return new HistoryRelabel.Lookups(hostMap, asnMap, done);
    }
}
