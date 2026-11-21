using System.Text.Json;

namespace GeckoPeeks.App;

public enum UpdateState { Idle, Checking, Downloading, Ready, Installing, UpToDate, Error }

public sealed record UpdateStatus(UpdateState State, Version Current, Version? Available, double? Progress,
    string Notes, string ReleaseUrl, string Error, DateTime? LastCheckUtc, bool AutoInstall);

/// <summary>
/// Discord-style updater. It checks the update feed in the background, downloads and verifies the new exe,
/// and then waits in <see cref="UpdateState.Ready"/>. The user can restart to update, or the
/// service installs by itself after the app was idle for 10 minutes. Dev builds do nothing,
/// unless GECKO_PEEKS_DEV_UPDATES=1 opts them in to the devtest channel (see <see cref="UpdateFeed"/>).
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);
    public static readonly TimeSpan ResumeCheckDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GateInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AutoInstallIdle = TimeSpan.FromMinutes(10);

    /// <summary>After a failed check or download, retry after 10, 20, 40, then 80 min. The 3 h check still runs.</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(80);

    private readonly object _lock = new();
    private readonly UpdateClient _client;
    private readonly Func<bool> _canAutoInstall;
    private readonly Action _requestExit;
    private readonly SynchronizationContext? _ui;
    private readonly RunProfile _profile;
    private readonly Version _current;
    private readonly bool _enabled;
    private readonly TimeSpan _firstCheckDelay;
    private readonly string? _exePath;
    private readonly string? _skipPath;
    private readonly string? _settingsPath;
    private readonly IUpdateTimers _timers;
    private readonly Action<string, string, string> _beginReplace;
    private readonly Func<string> _restartArguments;
    private readonly CancellationTokenSource _cancel = new();

    private UpdateState _state = UpdateState.Idle;
    private Version? _available;
    private double? _progress;
    private string _notes = "";
    private string _error = "";
    private DateTime? _lastCheckUtc;
    private bool _autoInstall = true;
    private UpdateOffer? _readyOffer;
    private string? _readyPath;
    private DateTime? _gateSinceUtc;
    private DateTime? _lastGateSampleUtc;
    private Task? _inflight;
    private bool _started;
    private bool _disposed;
    private IDisposable? _checkTimer;
    private IDisposable? _gateTimer;
    private IDisposable? _resumeTimer;
    private IDisposable? _retryTimer;
    private int _failedChecks;
    private bool _transientFailure;

    public UpdateService(UpdateClient client, Func<bool> canAutoInstall, Action requestExit, SynchronizationContext? ui, RunProfile profile)
        : this(client, canAutoInstall, requestExit, ui, profile, UpdateServiceOptions.ForProfile(profile))
    {
    }

    /// <summary>Test seam. Every path, the clock, the timers and the swap can be replaced.</summary>
    internal UpdateService(
        UpdateClient client,
        Func<bool> canAutoInstall,
        Action requestExit,
        SynchronizationContext? ui,
        RunProfile profile,
        UpdateServiceOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _canAutoInstall = canAutoInstall ?? throw new ArgumentNullException(nameof(canAutoInstall));
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        _ui = ui;
        _profile = profile;
        _enabled = client.Serves(profile);
        _firstCheckDelay = options.FirstCheckDelay ?? FirstCheckDelay;
        _current = options.Current ?? AppUpdate.CurrentVersion();
        _exePath = options.ExePath;
        _skipPath = options.SkipPath;
        _timers = options.Timers ?? new SystemUpdateTimers();
        _beginReplace = options.BeginReplace
            ?? ((exe, next, args) => UpdateInstaller.BeginReplace(exe, next, args, profile));
        _restartArguments = options.RestartArguments ?? UpdateInstaller.RestartArguments;

        // Dev builds never read or write the settings file, even with dev updates on.
        _settingsPath = _enabled && profile == RunProfile.Production ? options.SettingsPath : null;
        _autoInstall = LoadAutoInstall(_settingsPath);
    }

    /// <summary>Raised on the UI context (or on the calling thread when no context was given).</summary>
    public event Action<UpdateStatus>? Changed;

    public UpdateStatus Status
    {
        get
        {
            lock (_lock)
                return Snapshot();
        }
    }

    public bool AutoInstall
    {
        get
        {
            lock (_lock)
                return _autoInstall;
        }
        set
        {
            lock (_lock)
            {
                if (_autoInstall == value)
                    return;
                _autoInstall = value;
                _gateSinceUtc = null;
            }

            SaveAutoInstall(_settingsPath, value);
            Raise();
        }
    }

    /// <summary>First check after 12 s, then every 3 h. Safe to call twice. No-op in Dev.</summary>
    public void Start()
    {
        if (!_enabled)
            return;

        lock (_lock)
        {
            if (_started || _disposed)
                return;
            _started = true;
            _checkTimer = _timers.Schedule(_firstCheckDelay, CheckInterval, () => CheckNowAsync(false));
            _gateTimer = _timers.Schedule(GateInterval, GateInterval, GateTickAsync);
        }
    }

    /// <summary>
    /// The host calls this after the PC wakes up. It restarts the idle gate and checks again
    /// 30 s later, when the network is back.
    /// </summary>
    public void CheckAfterResume()
    {
        if (!_enabled)
            return;

        lock (_lock)
        {
            if (!_started || _disposed)
                return;
            _gateSinceUtc = null;
            _resumeTimer?.Dispose();
            _resumeTimer = _timers.Schedule(ResumeCheckDelay, Timeout.InfiniteTimeSpan, () => CheckNowAsync(false));
        }
    }

    /// <summary>
    /// Checks the feed and downloads a newer release. Only one check runs at a time; a second
    /// call gets the running task. A user check clears a skipped version first.
    /// </summary>
    public Task CheckNowAsync(bool userInitiated)
    {
        if (!_enabled)
            return Task.CompletedTask;

        Task<Task> run;
        Task check;
        lock (_lock)
        {
            if (_disposed)
                return Task.CompletedTask;
            if (_inflight != null && !_inflight.IsCompleted)
                return _inflight;
            if (_state == UpdateState.Installing)
                return Task.CompletedTask;

            run = new Task<Task>(() => RunCheckAsync(userInitiated));
            check = run.Unwrap();
            _inflight = check;
        }

        // Runs on this thread up to the first await, so the Checking state is visible at once.
        run.RunSynchronously(TaskScheduler.Default);
        return check;
    }

    /// <summary>When Ready: starts the swap script and asks the host to exit.</summary>
    public void InstallNow() => Install(automatic: false);

    /// <summary>Remembers the offered version as skipped and hides it until a user check.</summary>
    public void Skip()
    {
        if (!_enabled)
            return;

        Version? version;
        string? downloaded;
        lock (_lock)
        {
            if (_disposed || _available == null)
                return;
            if (_state != UpdateState.Ready && _state != UpdateState.Error)
                return;

            version = _available;
            downloaded = _readyPath;
            _state = UpdateState.Idle;
            _available = null;
            _progress = null;
            _notes = "";
            _error = "";
            _readyOffer = null;
            _readyPath = null;
            _gateSinceUtc = null;
        }

        if (_skipPath != null)
            UpdateSkip.Skip(version, _profile, _skipPath);
        TryDelete(downloaded);
        Raise();
    }

    public void Dispose()
    {
        IDisposable?[] timers;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            timers = new[] { _checkTimer, _gateTimer, _resumeTimer, _retryTimer };
            _checkTimer = _gateTimer = _resumeTimer = _retryTimer = null;
        }

        foreach (var timer in timers)
            timer?.Dispose();

        try
        {
            _cancel.Cancel();
        }
        catch
        {
            // A callback threw; the service is going away anyway.
        }

        _client.Dispose();
    }

    private async Task RunCheckAsync(bool userInitiated)
    {
        lock (_lock)
            _transientFailure = false;
        await RunCheckCoreAsync(userInitiated).ConfigureAwait(false);
        ScheduleRetryIfFailed();
    }

    /// <summary>
    /// An Error after a check schedules one retry, so a dropped download does not wait 3 h.
    /// The wait doubles after each failure. Any check that ends well resets it.
    /// Only a network-type failure (<see cref="IsTransient"/>) gets a quick retry. A blocked folder
    /// (only moving the app fixes it) or a file that fails its size or SHA-256 check (retrying would
    /// download 72 MB again and again) waits for the 3 h check or a user check.
    /// </summary>
    private void ScheduleRetryIfFailed()
    {
        IDisposable? old;
        lock (_lock)
        {
            if (_disposed || !_started)
                return;

            old = _retryTimer;
            _retryTimer = null;
            if (_state != UpdateState.Error)
            {
                _failedChecks = 0;
            }
            else if (_transientFailure)
            {
                var delay = RetryDelay(_failedChecks);
                _failedChecks++;
                _retryTimer = _timers.Schedule(delay, Timeout.InfiniteTimeSpan, () => CheckNowAsync(false));
            }
        }

        old?.Dispose();
    }

    /// <summary>A failure that can pass by itself: no network, a timeout, a server error, a dropped download.</summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        UpdateFolderBlockedException => false,
        HttpRequestException or TimeoutException or OperationCanceledException or IOException => true,
        _ => false,
    };

    /// <summary>10 min after the first failure, then doubled, at most <see cref="MaxRetryDelay"/>.</summary>
    internal static TimeSpan RetryDelay(int failedBefore)
    {
        var delay = FirstRetryDelay;
        for (var i = 0; i < failedBefore && delay < MaxRetryDelay; i++)
            delay += delay;
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    private async Task RunCheckCoreAsync(bool userInitiated)
    {
        var token = _cancel.Token;
        try
        {
            if (userInitiated && _skipPath != null)
                UpdateSkip.Clear(_profile, _skipPath);

            // A check while Ready stays quiet. The Restart button must not flicker away.
            bool wasReady;
            lock (_lock)
            {
                wasReady = _state == UpdateState.Ready && _readyOffer != null;
                if (!wasReady)
                {
                    _state = UpdateState.Checking;
                    _progress = null;
                    _error = "";
                }
            }

            if (!wasReady)
                Raise();

            UpdateOffer? offer;
            try
            {
                offer = await _client.CheckAsync(_current, _profile, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _lastCheckUtc = _timers.UtcNow;
                    if (!wasReady)
                    {
                        _state = UpdateState.Error;
                        _error = Describe(ex, "Could not check for updates");
                        _transientFailure = IsTransient(ex);
                    }
                }

                Raise();
                return;
            }

            UpdateOffer? ready;
            lock (_lock)
            {
                _lastCheckUtc = _timers.UtcNow;
                ready = _readyOffer;
            }

            if (wasReady && (offer == null || AppUpdate.Compare(offer.Version, ready!.Version) <= 0))
            {
                Raise();
                return;
            }

            if (offer == null || (!userInitiated && _skipPath != null && UpdateSkip.IsSkipped(offer.Version, _profile, _skipPath)))
            {
                if (wasReady)
                {
                    Raise();
                    return;
                }

                SetUpToDate();
                return;
            }

            await DownloadAsync(offer, wasReady, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last guard. A background check must never crash the app.
            lock (_lock)
            {
                if (_state == UpdateState.Checking || _state == UpdateState.Downloading)
                {
                    _state = UpdateState.Error;
                    _progress = null;
                    _error = Describe(ex, "Could not update");
                }
            }

            Raise();
        }
    }

    private async Task DownloadAsync(UpdateOffer offer, bool wasReady, CancellationToken token)
    {
        var exe = _exePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            if (!wasReady)
                SetError(offer, "Could not find this exe's path, so the update cannot be installed.");
            return;
        }

        UpdateOffer? previous;
        string? previousPath;
        lock (_lock)
        {
            previous = _readyOffer;
            previousPath = _readyPath;
            _state = UpdateState.Downloading;
            _available = offer.Version;
            _progress = 0;
            _notes = offer.Notes;
            _error = "";
            _readyOffer = null;
            _readyPath = null;
            _gateSinceUtc = null;
        }

        Raise();

        string path;
        try
        {
            path = await _client.DownloadAsync(offer, exe, _profile, new SyncProgress(OnProgress), token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (wasReady && previous != null && previousPath != null && File.Exists(previousPath))
            {
                // The older download is still on disk. Keep offering it.
                lock (_lock)
                {
                    _state = UpdateState.Ready;
                    _available = previous.Version;
                    _progress = null;
                    _notes = previous.Notes;
                    _readyOffer = previous;
                    _readyPath = previousPath;
                }

                Raise();
                return;
            }

            lock (_lock)
                _transientFailure = IsTransient(ex);

            SetError(offer, Describe(ex, "Could not download the update"));
            return;
        }

        lock (_lock)
        {
            if (_disposed)
                return;
            _state = UpdateState.Ready;
            _progress = null;
            _readyOffer = offer;
            _readyPath = path;
            _gateSinceUtc = null;
        }

        Raise();
    }

    private void OnProgress(double value)
    {
        lock (_lock)
        {
            if (_state != UpdateState.Downloading)
                return;
            var clamped = Math.Clamp(value, 0, 1);
            if (_progress is double last && clamped < 1 && clamped - last < 0.01)
                return;
            _progress = clamped;
        }

        Raise();
    }

    private Task GateTickAsync()
    {
        OnUi(EvaluateGate);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs on the UI context. Installs once <c>canAutoInstall()</c> held for 10 minutes in a row
    /// while Ready with AutoInstall on. Any false sample starts the 10 minutes again.
    /// </summary>
    private void EvaluateGate()
    {
        lock (_lock)
        {
            if (_disposed || _state != UpdateState.Ready || !_autoInstall)
            {
                _gateSinceUtc = null;
                return;
            }
        }

        bool allowed;
        try
        {
            allowed = _canAutoInstall();
        }
        catch
        {
            allowed = false;
        }

        lock (_lock)
        {
            if (!allowed || _disposed || _state != UpdateState.Ready || !_autoInstall)
            {
                _gateSinceUtc = null;
                return;
            }

            // A long gap between samples means the PC slept. Sleep is not idle time.
            var now = _timers.UtcNow;
            var last = _lastGateSampleUtc;
            _lastGateSampleUtc = now;
            if (last is DateTime previous && now - previous > GateInterval * 3)
                _gateSinceUtc = null;

            if (_gateSinceUtc is not DateTime since)
            {
                _gateSinceUtc = now;
                return;
            }

            if (now - since < AutoInstallIdle)
                return;
        }

        Install(automatic: true);
    }

    private void Install(bool automatic)
    {
        if (!_enabled)
            return;

        string exe;
        string next;
        UpdateOffer? offer;
        lock (_lock)
        {
            if (_disposed || _state != UpdateState.Ready || _readyPath == null || _readyOffer == null || string.IsNullOrWhiteSpace(_exePath))
                return;
            exe = _exePath;
            next = _readyPath;
            offer = _readyOffer;
            _state = UpdateState.Installing;
            _gateSinceUtc = null;
        }

        Raise();

        // The file may have waited next to the exe for hours. Check it again right before the swap.
        if (!StillMatches(next, offer))
        {
            lock (_lock)
            {
                _state = UpdateState.Error;
                _error = "The downloaded update changed on disk, so it was not installed. The next check downloads it again.";
                _readyOffer = null;
                _readyPath = null;
            }

            TryDelete(next);
            Raise();
            return;
        }

        try
        {
            var arguments = _restartArguments();
            // An automatic install restarts hidden. A manual install restarts with the window shown.
            arguments = automatic ? WithTrayArgument(arguments) : WithoutTrayArgument(arguments);
            _beginReplace(exe, next, arguments);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _state = UpdateState.Error;
                _error = Describe(ex, "Could not install the update");
                _readyOffer = null;
                _readyPath = null;
            }

            Raise();
            return;
        }

        _requestExit();
    }

    /// <summary>Adds <c>--tray</c> once, so an automatic restart stays hidden in the tray.</summary>
    internal static string WithTrayArgument(string? arguments)
    {
        var text = (arguments ?? "").Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => string.Equals(p, StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase)))
            return text;
        return text.Length == 0 ? StartupRegistration.TrayArgument : StartupRegistration.TrayArgument + " " + text;
    }

    /// <summary>Removes every <c>--tray</c>, so a manual restart shows its window.</summary>
    internal static string WithoutTrayArgument(string? arguments)
    {
        var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Where(p =>
            !string.Equals(p, StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase)));
    }

    private void SetUpToDate()
    {
        lock (_lock)
        {
            _state = UpdateState.UpToDate;
            _available = null;
            _progress = null;
            _notes = "";
            _error = "";
        }

        Raise();
    }

    private void SetError(UpdateOffer offer, string message)
    {
        lock (_lock)
        {
            _state = UpdateState.Error;
            _available = offer.Version;
            _progress = null;
            _error = message;
        }

        Raise();
    }

    private UpdateStatus Snapshot() => new(
        _state, _current, _available, _progress, _notes, AppUpdate.ReleasesPageUrl, _error, _lastCheckUtc, _autoInstall);

    private void Raise()
    {
        UpdateStatus status;
        lock (_lock)
        {
            if (_disposed)
                return;
            status = Snapshot();
        }

        OnUi(() =>
        {
            bool disposed;
            lock (_lock)
                disposed = _disposed;
            if (!disposed)
                Changed?.Invoke(status);
        });
    }

    private void OnUi(Action action)
    {
        if (_ui == null)
        {
            action();
            return;
        }

        _ui.Post(_ => action(), null);
    }

    /// <summary>
    /// Shown only when Windows refuses to create the download file next to the exe
    /// (<see cref="UpdateFolderBlockedException"/>). Kept short so the tray balloon shows all of it.
    /// </summary>
    internal const string BlockedFolderMessage =
        "Windows blocked saving the update next to GeckoPeeks.exe (Controlled Folder Access or folder permissions). "
        + @"Move the app to %LOCALAPPDATA%\Programs\GeckoPeeks, then check again.";

    private static string Describe(Exception ex, string prefix)
    {
        var message = ex switch
        {
            TaskCanceledException => "the request timed out.",
            UpdateFolderBlockedException => BlockedFolderMessage,
            HttpRequestException => "the update server could not be reached (" + ex.Message.TrimEnd('.') + ").",
            _ => ex.Message,
        };
        return prefix + ": " + message;
    }

    /// <summary>True when the file at <paramref name="path"/> still has the offer's size and SHA-256.</summary>
    internal static bool StillMatches(string path, UpdateOffer offer)
    {
        try
        {
            var expected = AppUpdate.NormalizeSha256(offer.Sha256);
            if (expected == null)
                return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (offer.SizeBytes > 0 && stream.Length != offer.SizeBytes)
                return false;
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(expected, actual, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The next download overwrites it.
        }
    }

    internal static bool LoadAutoInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        try
        {
            if (!File.Exists(path))
                return true;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("autoInstall", out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
                return value.GetBoolean();
        }
        catch
        {
            // A broken file falls back to the default.
        }

        return true;
    }

    private static void SaveAutoInstall(string? path, bool value)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, bool> { ["autoInstall"] = value }));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Best-effort. The value still holds for this run.
        }
    }

    /// <summary>Reports on the calling thread. <see cref="Progress{T}"/> posts later, which is racy.</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public SyncProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(value);
    }
}

/// <summary>Clock and timers, so tests can move time without sleeping.</summary>
internal interface IUpdateTimers
{
    DateTime UtcNow { get; }

    /// <summary>Runs <paramref name="tick"/> after <paramref name="dueTime"/>, then every <paramref name="period"/>
    /// (<see cref="Timeout.InfiniteTimeSpan"/> for once). Dispose to stop.</summary>
    IDisposable Schedule(TimeSpan dueTime, TimeSpan period, Func<Task> tick);
}

internal sealed class SystemUpdateTimers : IUpdateTimers
{
    public DateTime UtcNow => DateTime.UtcNow;

    public IDisposable Schedule(TimeSpan dueTime, TimeSpan period, Func<Task> tick) =>
        new System.Threading.Timer(_ =>
        {
            try
            {
                tick().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch
            {
                // A timer callback must never take the process down.
            }
        }, null, dueTime, period);
}

/// <summary>What the public constructor fills in. Tests set every path to a temp folder.</summary>
internal sealed class UpdateServiceOptions
{
    public Version? Current { get; init; }
    public string? ExePath { get; init; }
    public string? SkipPath { get; init; }
    public string? SettingsPath { get; init; }
    public IUpdateTimers? Timers { get; init; }
    public Action<string, string, string>? BeginReplace { get; init; }
    public Func<string>? RestartArguments { get; init; }
    public TimeSpan? FirstCheckDelay { get; init; }

    public static string ProductionSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeckoPeeks",
        "update-settings.json");

    public static UpdateServiceOptions ForProfile(RunProfile profile)
    {
        if (!UpdateFeed.IsEnabled(profile))
            return new UpdateServiceOptions();

        if (profile == RunProfile.Production)
        {
            return new UpdateServiceOptions
            {
                ExePath = Environment.ProcessPath,
                SkipPath = UpdateSkip.FilePath,
                SettingsPath = ProductionSettingsPath,
            };
        }

        // Dev with GECKO_PEEKS_DEV_UPDATES=1: the skip file lives under the dev root, the
        // settings file is never written, and nothing touches %LOCALAPPDATA%\GeckoPeeks.
        var devRoot = AppIsolation.ReadDevScratchRoot();
        return new UpdateServiceOptions
        {
            ExePath = Environment.ProcessPath,
            SkipPath = devRoot == null ? null : Path.Combine(Path.GetFullPath(devRoot), "skipped-update.txt"),
            SettingsPath = null,
            FirstCheckDelay = DevFirstCheckDelay(Environment.GetEnvironmentVariable(UpdateFeed.DelayVariable)),
        };
    }

    /// <summary>Dev only: GECKO_PEEKS_UPDATE_DELAY_S from 1 to 600 seconds, else the default.</summary>
    internal static TimeSpan? DevFirstCheckDelay(string? text) =>
        int.TryParse(text?.Trim(), out var seconds) && seconds is >= 1 and <= 600
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
