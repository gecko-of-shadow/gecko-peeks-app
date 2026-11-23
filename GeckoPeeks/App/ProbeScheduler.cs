using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;

namespace GeckoPeeks.App;

public sealed class ProbeSnapshot
{
    public string Mode { get; init; } = "Every 15 min";
    public bool Paused { get; init; }
    public bool LeagueRunning { get; init; }
    public bool TestRunning { get; init; }
    public string Phase { get; init; } = "Idle";
    public DateTime? NextDueLocal { get; init; }
    public string ActivityLine { get; init; } = "";
    public IReadOnlyList<string> Activity { get; init; } = Array.Empty<string>();
    public IReadOnlyList<HopObservation> Hops { get; init; } = Array.Empty<HopObservation>();
    public bool HopsAreAddressesOnly { get; init; }
    public string LastError { get; init; } = "";
    public string ReadProblem { get; init; } = "";
    public int HopsRevision { get; init; }
    public int LogRevision { get; init; }
    public string Tooltip { get; init; } = "Gecko Peeks \u2014 no route yet";

    public string LogFolder { get; init; } = "";
    public string LogFileName { get; init; } = CsvLog.MainFileName;
    public string LogWriteStatus { get; init; } = LogStatusText.Waiting(null);

    /// <summary>Short reason while the log is blocked, else empty. Never raw exception text.</summary>
    public string LogLastError { get; init; } = "";
    public LogHealth LogHealth { get; init; } = LogHealth.Waiting;

    /// <summary>Why the log is in AppData instead of Documents, else empty.</summary>
    public string LogLocation { get; init; } = "";
    public int LogUnsavedRows { get; init; }

    public bool HasRoute { get; init; }
    public string RouteLine { get; init; } = "No route yet";
    public string LastHopFigure { get; init; } = "\u2014";
    public string RiotFigure { get; init; } = "\u2014";
    public string ContextLine { get; init; } = "";
    public string ChangeLine { get; init; } = "";
    public IReadOnlyList<int> ChangedHops { get; init; } = Array.Empty<int>();
    public string TestTimestamp { get; init; } = "";
    public string TestUpstream { get; init; } = "";
    public string TestHandoffNetwork { get; init; } = "";
    public string TestHandoffCity { get; init; } = "";
    public string TestLastHopAvg { get; init; } = "";
    public string TestLastHopLoss { get; init; } = "";
    public string TestRiotAvg { get; init; } = "";
    public string TestRiotLoss { get; init; } = "";
    public string TestLeague { get; init; } = "";
    public string TestAdapter { get; init; } = "";
    public string TestWifi { get; init; } = "";
    public string TestGateway { get; init; } = "";
    public string TestVpn { get; init; } = "";

    // v2.1

    /// <summary>Never null. Old constructors use the built-in na1, Auto=true, Basis="default".</summary>
    public RegionSelection Region { get; init; } = ProbeScheduler.DefaultSelection();

    /// <summary>The current region target.</summary>
    public string TargetIp { get; init; } = TestRunner.DefaultTarget;
    public string TargetLabel { get; init; } = RefData.EdgeLabel;

    /// <summary>The target of the shown test.</summary>
    public string TestTargetIp { get; init; } = "";
    public string TestUpstreamSource { get; init; } = "";
    public string TestHandoffSource { get; init; } = "";
    public long? TestUpstreamAsn { get; init; }
    public string TestHandoffCityName { get; init; } = "";
    public double? TestHandoffLat { get; init; }
    public double? TestHandoffLon { get; init; }
    public IReadOnlyList<HopName> HopNames { get; init; } = Array.Empty<HopName>();

    /// <summary>"NA (North America), automatic" for the diagnosis.</summary>
    public string RegionText { get; init; } = "";

    /// <summary>"&lt;TargetLabel&gt; &lt;ip&gt;", for example "Riot Direct edge (AS6507) 192.207.0.5".</summary>
    public string TargetText { get; init; } = "";

    /// <summary>Why no more tests run in this process (the one Dev probe is spent), else empty.</summary>
    public string NoMoreTests { get; init; } = "";

    /// <summary>"ip:port" of the running League match (from League's match log), else empty.</summary>
    public string MatchServer { get; init; } = "";

    /// <summary>True when the match server is the region target the tests trace anyway.</summary>
    public bool MatchSameAsTarget { get; init; }

    /// <summary>League's own newest in-game ping row (its netlog) while a match runs, else null.</summary>
    public GamePing? GamePing { get; init; }

    /// <summary>League's in-game ping row nearest to the shown test (within 30 s), else null.</summary>
    public GamePing? TestGamePing { get; init; }

    /// <summary>The last-hop address of the shown (saved) test, the router its last-hop figure belongs to.</summary>
    public string TestLastHopIp { get; init; } = "";
}

/// <summary>
/// One runner task, so ICMP never overlaps. The clock task only checks the
/// League process every 30 seconds and sleeps one second between due checks.
/// </summary>
public sealed class ProbeScheduler : IDisposable
{
    private const string SettingsFileName = "gecko-peeks-settings.json";
    private const string ActivityFileName = "activity.log";

    private readonly CsvLog _csv;
    private readonly ActivityLog _activity;
    private readonly RunProfile _profile;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    // One ICMP user at a time: a test run, or the export's support trace.
    private readonly SemaphoreSlim _icmpGate = new(1, 1);
    private SynchronizationContext? _ui;

    private bool _paused;
    private long _settingsRevision;
    private bool _league;
    private bool _testRunning;
    private bool _acceptRuns = true;
    private bool _hopsAreAddressesOnly;
    private bool _riotNoReplyNoted;
    private int _runRequested;
    // v2.2: what the queued run does. A run for the other services alone leaves the Riot due time alone.
    private bool _riotWanted;
    private bool _allServices;
    private int _hopsRevision;
    private int _logRevision = 1;
    private string _phase = "Idle";
    private string _lastError = "";
    private string _activityLine = "Waiting to start.";
    private string _tipUpstream = "";
    private string _tipNetwork = "";
    private string _tipCity = "";
    private string _tipRiot = "";
    private string _tipLastHop = "";
    private string _tipHandoffRtt = "";
    private string _tipLastLoss = "";
    private string _tipRiotLoss = "";
    private string _tipLeague = "";
    private string _tipAdapter = "";
    private string _tipWifi = "";
    private string _tipGateway = "";
    private string _tipVpn = "";
    private string _pathChange = "";
    private string _pendingHopNote = "";
    /// <summary>The previous path and confirmed route for the current region target. Under the gate.</summary>
    private readonly RouteBaseline _baseline = new();
    private List<int> _changedHops = new();
    private string _logWriteStatus = LogStatusText.Waiting(null);
    private string _activityFolder;
    private DateTime? _tipTimestamp;
    private DateTime? _nextDueUtc;
    private DateTime? _lastTestUtc;
    private DateTime _startedUtc;
    private List<HopObservation> _hops = new();
    private Task? _runner;
    private Task? _clock;

    private readonly CoreServices? _core;
    private string _tipTargetIp = "";
    private string _tipUpstreamSource = "";
    private string _tipHandoffSource = "";
    private string _tipHandoffHost = "";
    private long? _tipUpstreamAsn;
    private Dictionary<string, long> _hopAsns = new(StringComparer.OrdinalIgnoreCase);

    // v2.1.3: the server of the running League match, from League's own match log. Under the gate.
    private MatchServer? _match;
    private string _matchNoted = "";
    private string? _gameLogs;
    private DateTime _gameLogsCheckedUtc = DateTime.MinValue;
    private DateTime? _gameStartedUtc;
    // v2.1.4: League's in-game ping (its own netlog): the newest row, and the row nearest the last test.
    private GamePing? _gamePing;
    private GamePing? _testGamePing;
    private string _tipLastHopIp = "";

    /// <summary>Test seam: where League's match logs are, and the clock. Null uses the real ones.</summary>
    internal Func<string?>? FindGameLogs { get; set; }
    internal Func<DateTime>? UtcNow { get; set; }
    internal Func<DateTime?>? GameStartedUtc { get; set; }

    public ProbeScheduler(LogPaths paths)
        : this(paths, AppIsolation.Profile)
    {
    }

    public ProbeScheduler(LogPaths paths, RunProfile profile)
        : this(paths, profile, null)
    {
    }

    public ProbeScheduler(LogPaths paths, RunProfile profile, CoreServices? core)
    {
        _profile = profile;
        _core = core;
        if (_core != null)
        {
            _baseline.SetTarget(_core.Region.Current.Region.TargetIp);
            _core.Region.Changed += OnRegionChanged;
        }

        if (!AppIsolation.AllowsLogPrepare(profile) && paths.FallbackFolder != null)
            throw new InvalidOperationException("Dev logs have no production fallback.");

        _csv = new CsvLog(paths.Folder, fallbackFolder: paths.FallbackFolder);
        if (AppIsolation.AllowsLogPrepare(_profile))
        {
            _csv.PrepareFolder();
            if (_csv.MovedThisSession)
            {
                CarryOver(SettingsFileName);
                CarryOver(ActivityFileName);
            }
        }

        _activityFolder = _csv.Folder;
        _activity = new ActivityLog(Path.Combine(_activityFolder, ActivityFileName));
        if (AppIsolation.AllowsLogPrepare(_profile))
            CrashLog.Folder = _activityFolder;
    }

    /// <summary>Folder is where the log is now: AppData once Controlled folder access moved it there.</summary>
    public LogPaths Paths => new(_csv.Folder, _csv.FallbackFolder);
    public CsvLog Log => _csv;

    private string SettingsPath => Path.Combine(_csv.Folder, SettingsFileName);

    public event Action<ProbeSnapshot>? Updated;
    public event Action<string>? RouteChanged;

    /// <summary>
    /// Dev only (see <see cref="AppIsolation.DevProbeOn"/>): this process may run one real test.
    /// Set it before Start. Production ignores it. Logs stay in the dev root.
    /// </summary>
    public bool DevSingleProbe { get; set; }

    private int _devRunsRequested;

    public const string DevProbeSpentText = "Dev probe mode runs one test per launch.";

    /// <summary>Dev probe mode only: true once its one test was requested. Production: always false.</summary>
    public bool DevProbeSpent
    {
        get
        {
            lock (_gate)
                return DevSpentLocked();
        }
    }

    /// <summary>Caller holds the gate. False while the one test is still queued, so the UI does not flash "no more tests".</summary>
    private bool DevSpentLocked() =>
        !AppIsolation.AllowsProbes(_profile) && DevSingleProbe && _devRunsRequested > 0 && _runRequested == 0;

    private bool ProbesAllowed =>
        AppIsolation.AllowsProbes(_profile) || (_profile == RunProfile.Dev && DevSingleProbe);

    public void Start()
    {
        if (!ProbesAllowed)
            return;

        _ui = SynchronizationContext.Current;
        _startedUtc = DateTime.UtcNow;
        LoadFromDisk();
        if (_csv.MovedThisSession)
            Note(LogStatusText.MovedNote(_csv.CopiedRows));
        else if (_csv.Health == LogHealth.Blocked)
            Note(LogStatusText.BlockedAtStartNote);
        Publish();
        var token = _cts.Token;
        _runner = Task.Run(() => RunnerLoop(token), token);
        _clock = Task.Run(() => ClockLoop(token), token);
    }

    public void RunNow()
    {
        if (!ProbesAllowed)
            return;

        Note("Run test now");
        RequestRun(allServices: true);
    }

    public void TogglePause()
    {
        bool paused;
        lock (_gate)
        {
            _paused = !_paused;
            paused = _paused;
            // The resumed run is requested explicitly. Leave the due time empty
            // until that test finishes so the clock does not queue a second one.
            _nextDueUtc = null;
        }

        SaveSettings();
        Note(paused ? "Logging paused. Run test now still works." : "Logging resumed.");
        if (!paused)
            RequestRun();
    }

    public ProbeSnapshot Capture()
    {
        lock (_gate)
            return BuildSnapshot(DateTime.Now);
    }

    public async Task StopAsync()
    {
        lock (_gate)
            _acceptRuns = false;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Already disposed.
        }

        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            bool running;
            lock (_gate)
                running = _testRunning;
            if (!running)
                break;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    /// <summary>The built-in na1 selection that the old constructors show.</summary>
    internal static RegionSelection DefaultSelection()
    {
        var data = RefData.BuiltIn;
        return new RegionSelection(data.FindRegion("na1") ?? data.DefaultRegion, true, "default");
    }

    private RegionSelection CurrentRegion() => _core?.Region.Current ?? DefaultSelection();

    /// <summary>
    /// A new target starts a new baseline: no balloon and no hop diff for the switch itself.
    /// A test that is still running traces the old target; its row does not become the baseline.
    /// Production then runs a test at once, unless logging is paused. Region changes can come
    /// with no user action (a new edge country, a refdata update), and a paused app runs only
    /// Run test now.
    /// </summary>
    private void OnRegionChanged(RegionSelection selection)
    {
        var target = selection.Region.TargetIp;
        bool newTarget;
        bool paused;
        lock (_gate)
        {
            paused = _paused;
            newTarget = _baseline.SetTarget(target);
            if (newTarget)
            {
                _pathChange = "";
                _changedHops = new List<int>();
                _pendingHopNote = "";
                _riotNoReplyNoted = false;
            }
        }

        if (!newTarget)
        {
            Publish();
            return;
        }

        var region = $"Region set to {selection.Region.ShortName} ({selection.Region.Name}).";
        if (paused)
        {
            Note($"{region} Logging is paused. The next test traces {target}.");
            return;
        }

        Note($"{region} Tests now trace {target}.");
        RequestRun();
    }

    public void Dispose()
    {
        if (_core != null)
            _core.Region.Changed -= OnRegionChanged;
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore a second dispose.
        }

        _cts.Dispose();
        _wake.Dispose();
    }

    private async Task ClockLoop(CancellationToken cancellationToken)
    {
        UpdateLeague(logChange: false);
        lock (_gate)
        {
            if (!_paused)
                _nextDueUtc = DateTime.UtcNow.AddSeconds(5);
        }

        Publish();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool startFirst;
        lock (_gate)
        {
            // Run test now (or Resume) inside these five seconds already started or queued the
            // first test. Asking again would run a second one straight after it.
            var ranSinceStart = _lastTestUtc is DateTime last && last >= _startedUtc;
            startFirst = !_paused && !_testRunning && _runRequested == 0 && !ranSinceStart;
            if (!_paused)
            {
                var interval = _league ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
                _nextDueUtc = DateTime.UtcNow.Add(interval);
            }
        }

        if (startFirst)
        {
            Note("Starting the first test.");
            RequestRun();
        }

        var nextLeagueCheck = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (DateTime.UtcNow >= nextLeagueCheck)
            {
                nextLeagueCheck = DateTime.UtcNow.AddSeconds(30);
                UpdateLeague(logChange: true);
            }

            bool due;
            bool idle;
            lock (_gate)
            {
                idle = !_paused && !_testRunning && _runRequested == 0;
                due = idle && _nextDueUtc is DateTime when && DateTime.UtcNow >= when;
            }

            if (due)
                RequestRun();
            else if (idle && ServicesDue())
                RequestServiceRun();
        }
    }

    /// <summary>
    /// The server of the running match from League's match log, else null. Finding the League
    /// folder is retried at most every 10 minutes when it is not found.
    /// </summary>
    private MatchServer? CurrentMatch()
    {
        try
        {
            var (logs, now, started) = MatchLogs();
            return started is { } since ? LeagueMatch.Current(logs, now, since) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// League's in-game ping rows of the running match (League's own netlog), else empty. Read every
    /// 30 s while the game runs, and after each test to pair the test with League's ping at that time.
    /// </summary>
    private IReadOnlyList<GamePing> CurrentGamePings()
    {
        try
        {
            bool league;
            lock (_gate)
                league = _league;
            if (!league || _core == null)
                return Array.Empty<GamePing>();
            var (logs, now, started) = MatchLogs();
            return started is { } since ? LeagueMatch.CurrentPings(logs, now, since) : Array.Empty<GamePing>();
        }
        catch
        {
            return Array.Empty<GamePing>();
        }
    }

    private void RefreshGamePing()
    {
        var rows = CurrentGamePings();
        _core?.Diagnostics.RecordGamePings(rows);
        lock (_gate)
            _gamePing = rows.Count > 0 ? rows[^1] : null;
    }

    /// <summary>League's GameLogs folder, the time now and the running game's start time (once per game).</summary>
    private (string? Logs, DateTime Now, DateTime? Started) MatchLogs()
    {
        {
            var now = UtcNow?.Invoke() ?? DateTime.UtcNow;
            string? logs;
            lock (_gate)
            {
                // Looked up again every 10 minutes, so a moved or reinstalled League is found.
                if (now - _gameLogsCheckedUtc > TimeSpan.FromMinutes(10) || (_gameLogs != null && !Directory.Exists(_gameLogs)))
                {
                    _gameLogsCheckedUtc = now;
                    _gameLogs = (FindGameLogs ?? LeagueMatch.FindGameLogs)();
                }

                logs = _gameLogs;
            }

            // Only this game's log counts: a log made before the game process started is from an
            // earlier match. Without the start time, no match is claimed. The start time is read
            // once per game (UpdateLeague forgets it when the game closes).
            DateTime? started;
            lock (_gate)
                started = _gameStartedUtc;
            if (started == null)
            {
                started = (GameStartedUtc ?? LeagueProcess.StartedUtc)();
                lock (_gate)
                    _gameStartedUtc = started;
            }

            return (logs, now, started);
        }
    }

    /// <summary>Keeps the match for the window, and writes one activity line per new match server.</summary>
    private void NoteMatch(MatchServer? match)
    {
        string note = "";
        lock (_gate)
        {
            _match = match;
            if (match == null)
                return;
            var key = match.Text;
            if (key == _matchNoted)
                return;
            _matchNoted = key;
            var regionTarget = CurrentRegion().Region.TargetIp;
            note = string.Equals(match.Ip, regionTarget, StringComparison.Ordinal)
                ? $"League match: the game connected to {match.Text}, the address the tests trace."
                : $"League match: the game connected to {match.Text}. The tests keep tracing the region target {regionTarget}.";
        }

        Note(note);
    }

    private void UpdateLeague(bool logChange)
    {
        var running = LeagueProcess.IsRunning();
        var changed = false;
        lock (_gate)
        {
            if (_league != running)
            {
                _league = running;
                changed = true;
                RecomputeDue();
            }

            if (!running)
            {
                _match = null;
                _matchNoted = "";
                _gameStartedUtc = null;
                _gamePing = null;
                _testGamePing = null;
            }
        }

        if (!running)
            _core?.Diagnostics.EndGame();

        // The League card shows the in-game ping live, about every 30 s, not only at each test.
        if (running && _core != null)
        {
            GamePing? before;
            lock (_gate)
                before = _gamePing;
            RefreshGamePing();
            GamePing? after;
            lock (_gate)
                after = _gamePing;
            if (!changed && !Equals(before, after))
                Publish();
        }

        if (!changed)
            return;

        if (logChange)
            Note(running
                ? "League of Legends is running. Tests switch to every 5 min."
                : "League of Legends closed. Tests switch back to every 15 min.");
        else
            Publish();
    }

    private bool ServicesDue()
    {
        try
        {
            return _core != null && AppIsolation.AllowsProbes(_profile) && _core.Services.AnyDue();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Production only: a run for the other services that are due. The Riot test does not run.</summary>
    private void RequestServiceRun()
    {
        if (!AppIsolation.AllowsProbes(_profile))
            return;
        lock (_gate)
        {
            if (!_acceptRuns || _runRequested == 1)
                return;
            _runRequested = 1;
        }

        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void RequestRun(bool allServices = false)
    {
        if (!ProbesAllowed)
            return;

        lock (_gate)
        {
            if (!_acceptRuns)
                return;
            // A Dev probe run is one test per process. Production has no cap.
            if (!AppIsolation.AllowsProbes(_profile) && _devRunsRequested++ > 0)
                return;
            _runRequested = 1;
            _riotWanted = true;
            _allServices |= allServices;
        }

        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async Task RunnerLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_wake.Wait(0))
            {
                // Collapse extra wakes. One follow-up run is enough.
            }

            var shouldRun = false;
            var riot = false;
            var allServices = false;
            var phaseBefore = "Idle";
            DateTime? riotDoneUtc = null;
            lock (_gate)
            {
                if (_runRequested == 1)
                {
                    _runRequested = 0;
                    shouldRun = true;
                    phaseBefore = _phase;
                    riot = _riotWanted;
                    allServices = _allServices;
                    _riotWanted = false;
                    _allServices = false;
                    _testRunning = true;
                    _phase = "Starting test";
                }
            }

            if (!shouldRun)
                continue;

            Publish();
            var gateHeld = false;
            try
            {
                // An export trace that is running finishes first; the test then starts.
                if (!_icmpGate.Wait(0))
                {
                    lock (_gate)
                        _phase = "Waiting for the export trace";
                    Publish();
                    await _icmpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                        _phase = "Starting test";
                }

                gateHeld = true;
                if (riot)
                {
                    try
                    {
                        await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        // The Riot due time counts from the end of the Riot test, not of the services after it.
                        riotDoneUtc = DateTime.UtcNow;
                    }
                }

                // A services-only run puts back the phase it found (for example a last test problem).
                string phaseAfter;
                lock (_gate)
                    phaseAfter = riot ? _phase : phaseBefore;
                await RunServicesAsync(allServices, phaseAfter, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !gateHeld)
            {
                // Shutting down while an export trace held the gate: nothing ran.
            }
            catch (Exception ex)
            {
                bool leagueNow;
                lock (_gate)
                    leagueNow = _league;
                RecordTest("stopped", "Test stopped: " + ex.Message, null, CurrentRegion().Region.Id, leagueNow, null, null);
                Note("Test stopped: " + ex.Message);
            }
            finally
            {
                if (gateHeld)
                    _icmpGate.Release();
                bool again;
                lock (_gate)
                {
                    _testRunning = false;
                    // Every finished Riot run moves the due time, saved or not. Otherwise a run that
                    // cannot write (or gets no replies) leaves it in the past and tests run back to back.
                    // A run for the other services alone does not move it.
                    if (riot)
                    {
                        _lastTestUtc = riotDoneUtc ?? DateTime.UtcNow;
                        if (!_paused)
                            RecomputeDue();
                    }

                    again = _runRequested == 1 && _acceptRuns;
                    if (string.IsNullOrWhiteSpace(_lastError))
                        _phase = _paused ? "Paused" : "Idle";
                }

                Publish();
                // A run asked for during this one is already queued with what it wants: wake for it.
                if (again)
                {
                    try
                    {
                        _wake.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Shutting down.
                    }
                }
            }
        }
    }

    /// <summary>
    /// The other services that are on and due (all of them after Run test now). Their progress only
    /// sets the phase text; their hops never reach the Riot route view. <paramref name="restorePhase"/>
    /// comes back after. Production runs them on their own schedule; a Dev build only in its one
    /// opt-in probe run (GECKO_PEEKS_DEV_PROBE=1), which then tests Riot and the services that are on.
    /// </summary>
    private async Task RunServicesAsync(bool all, string restorePhase, CancellationToken cancellationToken)
    {
        if (_core == null || !ProbesAllowed || cancellationToken.IsCancellationRequested)
            return;
        try
        {
            await _core.Services.RunDueAsync(all, text =>
            {
                lock (_gate)
                    _phase = text;
                Publish();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A service test never stops the Riot tests.
        }
        finally
        {
            lock (_gate)
                _phase = restorePhase;
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var league = false;
        lock (_gate)
            league = _league;

        // Progress<T> captures SynchronizationContext at construction. This runs on
        // the thread pool, so use the UI context saved in Start() instead.
        var progress = new ContextProgress<ProbeProgress>(_ui, update =>
        {
            lock (_gate)
            {
                _phase = update.Phase;
                if (update.Hops != null)
                    SetHops(update.Hops);
            }

            Publish();
        });

        // During a League match, note which server the match connected to. The test target never
        // changes: the window says whether the tests already trace that address.
        var match = league && _core != null ? CurrentMatch() : null;
        NoteMatch(match);
        var regionId = CurrentRegion().Region.Id;

        // Each run reads the region at its start.
        var row = _core == null
            ? await TestRunner.RunAsync(league, progress, cancellationToken).ConfigureAwait(false)
            : await TestRunner.RunAsync(_core.BuildRequest(league), progress, cancellationToken).ConfigureAwait(false);

        // During a match, find League's own ping from the same moment (the netlog row nearest to the
        // end of the test, where its pings run; at most 30 s apart). It is kept only together with
        // the saved test it belongs to (see Remember below), never with an older shown test.
        GamePing? paired = null;
        {
            var pings = !row.Cancelled && league ? CurrentGamePings() : Array.Empty<GamePing>();
            var endUtc = UtcNow?.Invoke() ?? DateTime.UtcNow;
            paired = LeagueMatch.Nearest(pings, endUtc, TimeSpan.FromSeconds(30));
            if (pings.Count > 0)
            {
                lock (_gate)
                    _gamePing = pings[^1];
            }

            _core?.Diagnostics.RecordGamePings(pings);
        }

        void Save(string outcome, string reason) => RecordTest(outcome, reason, row, regionId, league, match, paired);
        if (row.Cancelled)
        {
            // Cancelled probes are not finished samples. Do not append a partial
            // row (blank Riot stats) that would skew the upstream summary.
            lock (_gate)
            {
                _phase = "Idle";
                _lastError = "";
                if (row.Hops.Count > 0)
                    SetHops(row.Hops);
            }

            var cancelNote = row.HasMeasuredData()
                ? "Test cancelled; partial result was not written to the CSV."
                : "Test cancelled before it recorded anything.";
            Save("cancelled", cancelNote);
            Note(cancelNote);
            Publish();
            return;
        }

        if (row.Hops.Count > 0 && row.Hops.All(hop => !hop.Responded))
        {
            lock (_gate)
            {
                _phase = "Idle";
                _lastError = string.IsNullOrWhiteSpace(row.Error) ? "No hops responded." : row.Error;
                SetHops(row.Hops);
            }

            Save("noReply", "No hops responded; nothing was written to the CSV.");
            Note("No hops responded; nothing was written to the CSV.");
            Publish();
            return;
        }

        var folderBefore = _csv.Folder;
        AppendResult where;
        try
        {
            where = _csv.Append(row);
        }
        catch (Exception ex)
        {
            // The LOCAL LOG strip carries the reason; the status panel only points there,
            // so the same error is not shown (and clipped) twice.
            lock (_gate)
            {
                _lastError = row.Error;
                _phase = string.IsNullOrWhiteSpace(row.Error) ? "Idle" : "Last test problem: " + row.Error;
                SetHops(row.Hops);
            }

            Save("notSaved", "CSV row not saved: " + ex.Message);
            Note(_csv.Health == LogHealth.Blocked ? LogStatusText.NotSavedNote : "Row not saved: " + ex.Message);
            SaveSettings(row);
            return;
        }

        if (!string.Equals(folderBefore, _csv.Folder, StringComparison.OrdinalIgnoreCase))
        {
            FollowLogFolder();
            Note(LogStatusText.MovedNote(_csv.CopiedRows));
        }

        var savedAt = _csv.LastSuccessfulWriteLocal ?? DateTime.Now;
        var saved = LogStatusText.Saved(savedAt, where);

        lock (_gate)
        {
            _lastError = row.Error;
            _phase = string.IsNullOrWhiteSpace(row.Error) ? "Idle" : "Last test problem: " + row.Error;
            SetHops(row.Hops);
            _logRevision++;
            _logWriteStatus = saved;
            Remember(row);
            // League's ping belongs to this saved test only; a test without one clears it.
            _testGamePing = paired;
            _tipLastHopIp = row.LastHopIP;
        }

        Save(where == AppendResult.Pending ? "pending" : "saved", "");
        SaveSettings(row);
        if (!string.IsNullOrWhiteSpace(_csv.BackupProblem))
            Note("Row saved. Backup copy could not be refreshed: " + _csv.BackupProblem);
        else if (where == AppendResult.Pending)
            Note("Main log is locked. The row is in riot-route-log.pending.csv and will merge later.");
        else
            Note(string.IsNullOrWhiteSpace(row.Error)
                ? saved
                : "Appended a row with blanks after a problem: " + row.Error);

        if (!_riotNoReplyNoted && LatencyLabels.RiotNoReply(row.RiotHopAvg, row.RiotHopLossPct))
        {
            _riotNoReplyNoted = true;
            var target = string.IsNullOrWhiteSpace(row.TargetIp) ? TestRunner.RiotPingTarget : row.TargetIp;
            // The label of the address this test traced, which can differ from the region target now.
            var label = TestTargetText.LabelFor(target, CurrentRegion().Region, RefData.Current);
            Note($"Riot ICMP ({target}, the {label}, the traceroute target) gave no reply (Riot filters ping). Latency shown is the last hop (path), not in-game ping.");
        }

        if (!string.IsNullOrWhiteSpace(row.Error))
        {
            lock (_gate)
                _phase = "Last test problem: " + row.Error;
        }

        string hopNote;
        lock (_gate)
        {
            hopNote = _pendingHopNote;
            _pendingHopNote = "";
        }

        if (hopNote.Length > 0)
            Note(hopNote);
    }

    /// <summary>
    /// Caller holds the gate. The revision moves only when the path changed, so the window
    /// does not clear and refill the traceroute grid for each of the twenty pings.
    /// </summary>
    private void SetHops(IReadOnlyList<HopObservation> hops)
    {
        var changed = _hopsAreAddressesOnly || !HopList.SameHops(hops, _hops);
        _hopsAreAddressesOnly = false;
        if (!changed)
            return;

        _hops = hops.ToList();
        _hopsRevision++;
    }

    private void Remember(RouteRow row)
    {
        _tipUpstream = row.Upstream;
        _tipNetwork = row.HandoffNetwork;
        _tipCity = row.HandoffCity;
        _tipRiot = row.RiotHopAvg;
        _tipLastHop = row.LastHopAvg;
        _tipHandoffRtt = row.HandoffRttMs;
        CopyTestFields(row);
        _tipTargetIp = row.TargetIp;
        _tipUpstreamSource = row.UpstreamSource;
        _tipHandoffSource = row.HandoffSource;
        _tipHandoffHost = row.HandoffHost;
        _tipUpstreamAsn = row.UpstreamAsn;
        _hopAsns = new Dictionary<string, long>(row.HopAsns, StringComparer.OrdinalIgnoreCase);

        // A row traced to an old target (the region changed during the test) is logged and shown,
        // but it does not become the baseline: no hop diff now, no route change later.
        var compared = _baseline.Observe(row.TargetIp, row.FullPath, row.Upstream, row.HandoffCity, row.HandoffNetwork);
        _pathChange = compared.PathChange;
        _changedHops = compared.ChangedHops.ToList();
        _pendingHopNote = _changedHops.Count > 0 ? _pathChange : "";
        if (DateTime.TryParseExact(
                row.Timestamp,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            _tipTimestamp = local;
        }
        else
        {
            _tipTimestamp = DateTime.Now;
        }

        // Fires only once two logged tests in a row agree on the new upstream and city.
        var message = compared.RouteMessage;
        if (!string.IsNullOrWhiteSpace(message))
            Post(() => RouteChanged?.Invoke(message));
    }

    private void LoadFromDisk()
    {
        var settings = SettingsStore.Load(SettingsPath);
        IReadOnlyList<RouteRow> rows;
        try
        {
            rows = _csv.ReadAll();
        }
        catch
        {
            rows = Array.Empty<RouteRow>();
        }

        RouteRow? last = null;
        for (var i = 0; i < rows.Count; i++)
            last = rows[i];

        lock (_gate)
        {
            _paused = settings.Paused;
            _phase = _paused ? "Paused" : "Idle";
            if (last != null)
                ApplyRemembered(last, settings);
            else if (settings.Last != null)
                ApplySettingsResult(settings);

            // The region changed since the saved test: that test is no baseline for the new target.
            if (!_baseline.Matches(_tipTargetIp))
                _baseline.Clear();

            var recent = _activity.Recent();
            if (recent.Count > 0)
                _activityLine = recent[^1];
            else
                _activityLine = _paused ? "Logging is paused." : "No tests yet.";

            _logWriteStatus = LogStatusText.Waiting(last?.Timestamp);
        }
    }

    /// <summary>Settings, activity history and crash.log go wherever the CSV now lives.</summary>
    private void FollowLogFolder()
    {
        var folder = _csv.Folder;
        if (string.Equals(folder, _activityFolder, StringComparison.OrdinalIgnoreCase))
            return;

        CarryOver(SettingsFileName);
        CarryOver(ActivityFileName);
        _activityFolder = folder;
        _activity.MoveTo(Path.Combine(folder, ActivityFileName));
        CrashLog.Folder = folder;
    }

    private void CarryOver(string fileName)
    {
        try
        {
            var from = Path.Combine(_csv.PrimaryFolder, fileName);
            var to = Path.Combine(_csv.Folder, fileName);
            if (File.Exists(from) && !File.Exists(to))
                File.Copy(from, to);
        }
        catch
        {
            // Settings and activity history are conveniences; the CSV already moved.
        }
    }

    private void ApplyRemembered(RouteRow last, AppSettings settings)
    {
        _baseline.Seed(last.FullPath, last.Upstream, last.HandoffCity, last.HandoffNetwork);
        _tipUpstream = last.Upstream;
        _tipNetwork = last.HandoffNetwork;
        _tipCity = last.HandoffCity;
        _tipRiot = last.RiotHopAvg;
        _tipLastHop = last.LastHopAvg;
        _tipHandoffRtt = last.HandoffRttMs;
        CopyTestFields(last);
        if (DateTime.TryParseExact(
                last.Timestamp,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            _tipTimestamp = local;
            _lastTestUtc = local.ToUniversalTime();
        }

        _tipHandoffHost = last.HandoffHost;
        if (settings.Last != null && string.Equals(settings.Last.Timestamp, last.Timestamp, StringComparison.Ordinal))
            CopySavedV21(settings);

        if (settings.Hops.Count > 0 && string.Equals(settings.Last?.Timestamp, last.Timestamp, StringComparison.Ordinal))
        {
            _hops = settings.Hops.Select(ToHop).ToList();
            _hopsAreAddressesOnly = false;
        }
        else
        {
            _hops = RouteClassifier.HopsFromFullPath(last.FullPath);
            _hopsAreAddressesOnly = _hops.Count > 0;
        }

        _hopsRevision++;
    }

    private void ApplySettingsResult(AppSettings settings)
    {
        var last = settings.Last;
        if (last == null)
            return;

        _baseline.Seed(last.FullPath, last.Upstream, last.HandoffCity, last.HandoffNetwork);
        _tipUpstream = last.Upstream;
        _tipNetwork = last.HandoffNetwork;
        _tipCity = last.HandoffCity;
        _tipRiot = last.RiotHopAvg;
        _tipLastHop = last.LastHopAvg;
        _tipHandoffRtt = last.HandoffRttMs;
        if (DateTime.TryParseExact(
                last.Timestamp,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            _tipTimestamp = local;
            _lastTestUtc = local.ToUniversalTime();
        }

        _hops = settings.Hops.Select(ToHop).ToList();
        CopySavedV21(settings);
        _hopsRevision++;
    }

    /// <summary>The v2.1 fields of the saved last result. Older settings files leave them blank.</summary>
    private void CopySavedV21(AppSettings settings)
    {
        var last = settings.Last;
        if (last == null)
            return;
        _tipTargetIp = last.TargetIp ?? "";
        _tipUpstreamSource = last.UpstreamSource ?? "";
        _tipHandoffSource = last.HandoffSource ?? "";
        _tipUpstreamAsn = last.UpstreamAsn;
        _hopAsns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var hop in settings.Hops)
        {
            if (hop.Asn is { } asn && !string.IsNullOrWhiteSpace(hop.Ip))
                _hopAsns[hop.Ip.Trim()] = asn;
        }
    }

    private void CopyTestFields(RouteRow row)
    {
        _tipLastLoss = row.LastHopLossPct;
        _tipRiotLoss = row.RiotHopLossPct;
        _tipLeague = row.LeagueRunning;
        _tipAdapter = row.Adapter;
        _tipWifi = row.Wifi;
        _tipGateway = row.Gateway;
        _tipVpn = row.Vpn;
    }

    private static HopObservation ToHop(SavedHop hop) =>
        new(hop.Number, hop.Ip, hop.Hostname, hop.RttMs, hop.Responded, hop.ReachedTarget);

    private void RecomputeDue()
    {
        if (_paused)
        {
            _nextDueUtc = null;
            return;
        }

        var interval = _league ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
        var from = _lastTestUtc ?? DateTime.UtcNow;
        _nextDueUtc = from + interval;
    }

    private void SaveSettings(RouteRow? row = null)
    {
        AppSettings settings;
        long revision;
        lock (_gate)
        {
            // Assigned with the copy, not at write time. A test that copied
            // Paused=false cannot land after a later pause snapshot.
            revision = ++_settingsRevision;
            settings = new AppSettings { Paused = _paused };
            if (row != null)
            {
                settings.Last = new SavedResult
                {
                    Timestamp = row.Timestamp,
                    Upstream = row.Upstream,
                    HandoffNetwork = row.HandoffNetwork,
                    HandoffCity = row.HandoffCity,
                    HandoffRttMs = row.HandoffRttMs,
                    RiotHopAvg = row.RiotHopAvg,
                    LastHopAvg = row.LastHopAvg,
                    FullPath = row.FullPath,
                    TargetIp = row.TargetIp,
                    UpstreamSource = row.UpstreamSource,
                    HandoffSource = row.HandoffSource,
                    UpstreamAsn = row.UpstreamAsn,
                };
                settings.Hops = row.Hops.Select(hop => new SavedHop
                {
                    Number = hop.Number,
                    Ip = hop.Ip,
                    Hostname = hop.Hostname,
                    RttMs = hop.RttMs,
                    Responded = hop.Responded,
                    ReachedTarget = hop.ReachedTarget,
                    Asn = row.HopAsns.TryGetValue(hop.Ip ?? "", out var asn) ? asn : null,
                }).ToList();
            }
            else if (_tipTimestamp != null)
            {
                settings.Last = new SavedResult
                {
                    Timestamp = CsvFormat.FormatTimestamp(_tipTimestamp.Value),
                    Upstream = _tipUpstream,
                    HandoffNetwork = _tipNetwork,
                    HandoffCity = _tipCity,
                    HandoffRttMs = _tipHandoffRtt,
                    RiotHopAvg = _tipRiot,
                    LastHopAvg = _tipLastHop,
                    FullPath = RouteClassifier.BuildFullPath(_hops),
                    TargetIp = _tipTargetIp,
                    UpstreamSource = _tipUpstreamSource,
                    HandoffSource = _tipHandoffSource,
                    UpstreamAsn = _tipUpstreamAsn,
                };
                settings.Hops = _hops.Select(hop => new SavedHop
                {
                    Number = hop.Number,
                    Ip = hop.Ip,
                    Hostname = hop.Hostname,
                    RttMs = hop.RttMs,
                    Responded = hop.Responded,
                    ReachedTarget = hop.ReachedTarget,
                    Asn = _hopAsns.TryGetValue(hop.Ip ?? "", out var asn) ? asn : null,
                }).ToList();
            }
        }

        try
        {
            SettingsStore.Save(SettingsPath, settings, revision);
        }
        catch
        {
            // The CSV row is the durable record. Settings only restore the window.
        }
    }

    /// <summary>Keeps one test in the diagnostics files (never throws, never blocks the test).</summary>
    private void RecordTest(string outcome, string reason, RouteRow? row, string regionId, bool league, MatchServer? match, GamePing? ping)
    {
        if (_core == null)
            return;
        try
        {
            Dictionary<int, string>? names = null;
            if (row != null && row.Hops.Count > 0)
            {
                names = new Dictionary<int, string>();
                foreach (var name in RouteClassifier.NameHops(row.Hops, row.HopAsns, RefData.Current))
                    names[name.Number] = name.Network;
            }

            _core.Diagnostics.RecordTest(new TestRecord(
                UtcNow?.Invoke() ?? DateTime.UtcNow, outcome, reason, row, regionId, league, match, ping, names));
        }
        catch
        {
            // Diagnostics never stop a test.
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> when no test is running, and holds tests back until it ends, so
    /// its ICMP never overlaps a test's. Waits at most <paramref name="wait"/>, then throws TimeoutException.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan wait, CancellationToken token)
    {
        if (!await _icmpGate.WaitAsync(wait, token).ConfigureAwait(false))
            throw new TimeoutException("A test is running.");
        try
        {
            return await work(token).ConfigureAwait(false);
        }
        finally
        {
            _icmpGate.Release();
        }
    }

    /// <summary>Writes one line to the activity log (and the window's activity line).</summary>
    public void NoteActivity(string message) => Note(message);

    /// <summary>Where activity.log and its monthly archives are now, and the CSV's first folder.</summary>
    public IReadOnlyList<string> ActivityFolders
    {
        get
        {
            var list = new List<string> { _activityFolder };
            if (!string.Equals(_csv.PrimaryFolder, _activityFolder, StringComparison.OrdinalIgnoreCase))
                list.Add(_csv.PrimaryFolder);
            return list;
        }
    }

    private void Note(string message)
    {
        var line = _activity.Write(message);
        lock (_gate)
            _activityLine = line;
        Publish();
    }

    private void Publish()
    {
        ProbeSnapshot snapshot;
        lock (_gate)
            snapshot = BuildSnapshot(DateTime.Now);

        Post(() => Updated?.Invoke(snapshot));
    }

    private ProbeSnapshot BuildSnapshot(DateTime now)
    {
        var region = CurrentRegion();
        var data = RefData.Current;
        var label = region.Region.TargetLabel;

        // Hop ASNs from the last row, then the ASN cache.
        var asns = new Dictionary<string, long>(_hopAsns, StringComparer.OrdinalIgnoreCase);
        if (_core != null)
        {
            foreach (var hop in _hops)
            {
                if (hop.Responded && !asns.ContainsKey(hop.Ip) && _core.Asn.Get(hop.Ip)?.Asn is { } cached)
                    asns[hop.Ip] = cached;
            }
        }

        var hopNames = RouteClassifier.NameHops(_hops, asns, data);
        var group = data.ProviderForHost(_tipHandoffHost)?.CityGroup;
        var cityName = string.IsNullOrWhiteSpace(_tipCity) ? "" : CityNames.Display(_tipCity, group);
        var coords = string.IsNullOrWhiteSpace(_tipCity) ? null : CityNames.Coordinates(_tipCity, group);
        var devSpent = DevSpentLocked();
        var regionText = $"{region.Region.ShortName} ({region.Region.Name}), {(region.Auto ? "automatic" : "manual")}";

        return new ProbeSnapshot
        {
            Region = region,
            TargetIp = region.Region.TargetIp,
            TargetLabel = label,
            TestTargetIp = _tipTargetIp,
            TestUpstreamSource = _tipUpstreamSource,
            TestHandoffSource = _tipHandoffSource,
            TestUpstreamAsn = _tipUpstreamAsn,
            TestHandoffCityName = cityName,
            TestHandoffLat = coords?.Lat,
            TestHandoffLon = coords?.Lon,
            HopNames = hopNames,
            RegionText = regionText,
            // The shown test's target; after a region change it also names the next one.
            TargetText = TestTargetText.Diagnosis(_tipTargetIp, region.Region, data),
            Mode = _league ? "Every 5 min" : "Every 15 min",
            Paused = _paused,
            LeagueRunning = _league,
            MatchServer = _match?.Text ?? "",
            MatchSameAsTarget = _match != null && string.Equals(_match.Ip, region.Region.TargetIp, StringComparison.Ordinal),
            GamePing = _league ? _gamePing : null,
            TestGamePing = _league ? _testGamePing : null,
            TestLastHopIp = _tipLastHopIp,
            TestRunning = _testRunning,
            Phase = _phase,
            NextDueLocal = devSpent ? null : _nextDueUtc?.ToLocalTime(),
            NoMoreTests = devSpent && !_testRunning ? "No more tests in this Dev run." : "",
            ActivityLine = _activityLine,
            Activity = _activity.Recent(),
            Hops = _hops.ToArray(),
            HopsAreAddressesOnly = _hopsAreAddressesOnly,
            LastError = _lastError,
            ReadProblem = _csv.ReadProblem ?? "",
            HopsRevision = _hopsRevision,
            LogRevision = _logRevision,
            Tooltip = TooltipText.Format(_tipUpstream, _tipNetwork, _tipCity, _tipRiot, _tipLastHop, _tipHandoffRtt, _tipTimestamp, now),
            LogFolder = _csv.Folder,
            LogFileName = CsvLog.MainFileName,
            LogWriteStatus = _logWriteStatus,
            LogLastError = _csv.LastWriteError ?? "",
            LogHealth = _csv.Health,
            LogLocation = LogStatusText.Location(_csv.UsingFallback, _csv.MovedThisSession, _csv.CopiedRows),
            LogUnsavedRows = _csv.UnsavedRows,
            HasRoute = _tipTimestamp != null,
            RouteLine = _tipTimestamp == null
                ? "No route yet"
                : RouteCardText.RouteLine(_tipUpstream, _tipNetwork, _tipCity),
            LastHopFigure = _tipTimestamp == null
                ? "\u2014"
                : RouteCardText.LastHopFigure(_tipLastHop, _tipLastLoss),
            RiotFigure = _tipTimestamp == null
                ? "\u2014"
                : RouteCardText.RiotFigure(_tipRiot, _tipRiotLoss),
            ContextLine = _tipTimestamp == null
                ? "The first test fills this in."
                : RouteCardText.Context(_league, _tipAdapter, _tipWifi, _tipGateway, _tipVpn, TooltipText.Ago(_tipTimestamp.Value, now)),
            ChangeLine = _pathChange,
            ChangedHops = _changedHops.ToArray(),
            TestTimestamp = _tipTimestamp == null ? "" : CsvFormat.FormatTimestamp(_tipTimestamp.Value),
            TestUpstream = _tipUpstream,
            TestHandoffNetwork = _tipNetwork,
            TestHandoffCity = _tipCity,
            TestLastHopAvg = _tipLastHop,
            TestLastHopLoss = _tipLastLoss,
            TestRiotAvg = _tipRiot,
            TestRiotLoss = _tipRiotLoss,
            TestLeague = _tipLeague,
            TestAdapter = _tipAdapter,
            TestWifi = _tipWifi,
            TestGateway = _tipGateway,
            TestVpn = _tipVpn,
        };
    }

    private void Post(Action action)
    {
        var ui = _ui;
        if (ui == null)
        {
            try
            {
                action();
            }
            catch
            {
                // UI updates are optional.
            }

            return;
        }

        ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch
            {
                // A drawing failure must not kill the runner.
            }
        }, null);
    }

    /// <summary>
    /// Like Progress&lt;T&gt;, but uses a captured UI SynchronizationContext even when
    /// constructed on a thread-pool thread (where SynchronizationContext.Current is null).
    /// </summary>
    private sealed class ContextProgress<T> : IProgress<T>
    {
        private readonly SynchronizationContext? _context;
        private readonly Action<T> _handler;

        public ContextProgress(SynchronizationContext? context, Action<T> handler)
        {
            _context = context;
            _handler = handler;
        }

        public void Report(T value)
        {
            if (_context == null)
            {
                _handler(value);
                return;
            }

            _context.Post(_ =>
            {
                try
                {
                    _handler(value);
                }
                catch
                {
                    // Progress must not fail the probe.
                }
            }, null);
        }
    }
}
