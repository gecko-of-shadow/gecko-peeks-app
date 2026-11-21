using System.Diagnostics;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Text;
using GeckoPeeks.Ui.State;
using GeckoPeeks.Ui.WebHost;

namespace GeckoPeeks.Ui;

/// <summary>
/// The v2 window: a plain WinForms frame that hosts the web UI in WebView2. The frame keeps
/// the v1 behaviour: X hides to the tray, minimize stays on the taskbar, only Exit quits, and
/// the title shows the version. The WebView2 is made on the first show and disposed after the
/// window was hidden for two minutes.
/// </summary>
internal sealed class MainForm : Form
{
    /// <summary>Hidden this long, the WebView2 and its browser processes are released.</summary>
    public static readonly TimeSpan ReleaseAfterHidden = TimeSpan.FromMinutes(2);

    private static readonly string[] StartPages = { "overview", "route", "history", "internet", "settings" };

    private readonly ProbeScheduler _scheduler;
    private readonly CoreServices _core;
    private readonly CloudPoller _cloud;
    private readonly HostSettings _settings;
    private readonly StartupToggle _startup;
    private readonly bool _demo;
    private readonly WebSurface _surface;
    private readonly StateBridge _bridge;
    private readonly System.Windows.Forms.Timer _releaseTimer;
    private bool _exitRequested;

    public MainForm(ProbeScheduler scheduler, CoreServices core, HostSettings settings, StartupToggle startup, bool demo)
    {
        _scheduler = scheduler;
        _core = core;
        _cloud = core.Cloud;
        _settings = settings;
        _startup = startup;
        _demo = demo;

        SuspendLayout();
        // The CI smoke test reads this exact title: "Gecko Peeks v2.1.0".
        Text = "Gecko Peeks v" + StateBridge.Version;
        BackColor = WebSurface.Background;
        ForeColor = Color.FromArgb(0xf4, 0xf4, 0xf5);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 820);
        Icon = GeckoMark.CreateIcon();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);

        var profile = AppIsolation.Profile;
        _surface = new WebSurface(this, UserDataFolder(profile), profile == RunProfile.Dev, StartUrl(profile));
        _surface.MessageReceived += OnWebMessage;
        _bridge = new StateBridge(this, scheduler, core, settings, startup, _surface, demo);

        _releaseTimer = new System.Windows.Forms.Timer { Interval = (int)ReleaseAfterHidden.TotalMilliseconds };
        _releaseTimer.Tick += (_, _) =>
        {
            _releaseTimer.Stop();
            if (Visible)
                return;
            _surface.Release();
            _bridge.ReleaseCache();
            // The browser processes need a moment to exit; trim after that.
            BeginInvoke(new Action(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                if (!Visible && !IsDisposed)
                    NativeMethods.TrimMemory();
            }));
        };
    }

    /// <summary>Raised when the window closes to the tray (the app keeps logging).</summary>
    public event Action? HiddenToTray;

    /// <summary>The updater is made after the window (it asks whether the window is hidden).</summary>
    public void AttachUpdates(UpdateService updates) => _bridge.Updates = updates;

    /// <summary>
    /// Tray click / double-click, tray Open window, and a second launch all land here.
    /// ShowInTaskbar stays true: toggling it recreates the window handle, and a hidden
    /// window has no taskbar button anyway.
    /// </summary>
    public void ShowFromTray()
    {
        if (IsDisposed)
            return;

        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        // Activate() can lose to the foreground lock and leave the window behind other
        // apps. A TopMost toggle still raises it to the top of the z-order.
        TopMost = true;
        TopMost = false;
        Activate();
        _surface.EnsureCreated();
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        _releaseTimer.Stop();
        Close();
    }

    public void OpenLog()
    {
        var path = _scheduler.Log.MainPath;
        if (!File.Exists(path))
        {
            OpenLogFolder();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            OpenLogFolder();
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_scheduler.Paths.Folder);
            Process.Start(new ProcessStartInfo(_scheduler.Paths.Folder) { UseShellExecute = true });
        }
        catch
        {
            ShowFromTray();
            _bridge.Toast("Could not open the log folder.", "bad");
        }
    }

    public void CopyLogPath()
    {
        try
        {
            Clipboard.SetText(_scheduler.Paths.Folder);
            _bridge.Toast("Copied the folder path.", "good");
        }
        catch
        {
            _bridge.Toast("Could not copy the folder path.", "bad");
        }
    }

    public void CopyDiagnosis()
    {
        var snapshot = _scheduler.Capture();
        var hops = new List<DiagnosisHop>(snapshot.Hops.Count);
        foreach (var hop in snapshot.Hops)
            hops.Add(new DiagnosisHop(hop.Number, hop.Responded ? hop.Ip : "*", hop.Hostname));

        var text = DiagnosisText.Format(new DiagnosisInput(
            StateBridge.Version,
            snapshot.HasRoute,
            snapshot.TestTimestamp,
            snapshot.TestUpstream,
            snapshot.TestHandoffNetwork,
            snapshot.TestHandoffCity,
            snapshot.TestLastHopAvg,
            snapshot.TestLastHopLoss,
            snapshot.TestRiotAvg,
            snapshot.TestRiotLoss,
            snapshot.TestLeague,
            snapshot.TestAdapter,
            snapshot.TestWifi,
            snapshot.TestGateway,
            snapshot.TestVpn,
            snapshot.ChangeLine,
            hops,
            snapshot.RegionText,
            snapshot.TargetText));

        try
        {
            Clipboard.SetText(text);
            _bridge.Toast("Copied the diagnosis.", "good");
        }
        catch
        {
            _bridge.Toast("Could not copy the diagnosis.", "bad");
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToWorkingArea();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _releaseTimer.Stop();
            if (_surface.IsCreated)
            {
                // The view was only suspended: wake it and send the current state once.
                var wasHidden = _surface.IsHidden;
                _surface.Resume();
                if (wasHidden)
                    _bridge.PostNow();
            }
            else
            {
                _surface.EnsureCreated();
            }
        }
        else if (!_exitRequested)
        {
            _releaseTimer.Stop();
            _releaseTimer.Start();
            _ = SuspendViewAsync();
        }
    }

    /// <summary>Hidden to the tray: the page stops running until the next show or the release.</summary>
    private async Task SuspendViewAsync()
    {
        try
        {
            await _surface.SuspendAsync().ConfigureAwait(true);
            // A show that came during the suspend: SuspendAsync already resumed the view.
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>
    /// Close (X) hides to the tray. Minimize stays on the taskbar like any window. A bare
    /// WM_CLOSE from outside the app arrives as TaskManagerClosing; it hides too, because a
    /// disposed form would leave tray Open window with nothing to show.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested
            && e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing or CloseReason.None)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _releaseTimer.Dispose();
            _bridge.Dispose();
            _surface.Dispose();
        }

        base.Dispose(disposing);
    }

    private void FitToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        // A small laptop screen at 125% or 150% is shorter than the scaled minimum height. Let the
        // window shrink to the screen instead of running off the bottom behind the taskbar.
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        if (width == Width && height == Height && area.Contains(Bounds))
            return;

        Bounds = new Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
    }

    private void OnWebMessage(string json)
    {
        var message = BridgeMessage.Parse(json);
        if (message == null)
            return;

        try
        {
            Dispatch(message);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _bridge.Toast("That did not work.", "bad");
        }
    }

    private void Dispatch(BridgeMessage message)
    {
        switch (message.Command)
        {
            case BridgeCommand.Ready:
                _surface.MarkPageReady();
                _bridge.OnPageReady();
                break;
            case BridgeCommand.RunNow:
                if (_demo || !(AppIsolation.AllowsProbes(AppIsolation.Profile) || _scheduler.DevSingleProbe))
                {
                    _bridge.Toast("Tests do not run in the Dev build.", "warn");
                    break;
                }

                if (_scheduler.DevProbeSpent)
                {
                    _bridge.Toast(ProbeScheduler.DevProbeSpentText, "warn");
                    break;
                }

                _scheduler.RunNow();
                break;
            case BridgeCommand.TogglePause:
                _scheduler.TogglePause();
                break;
            case BridgeCommand.CopyDiagnosis:
                CopyDiagnosis();
                break;
            case BridgeCommand.OpenLog:
                OpenLog();
                break;
            case BridgeCommand.OpenFolder:
                OpenLogFolder();
                break;
            case BridgeCommand.CopyPath:
                CopyLogPath();
                break;
            case BridgeCommand.CheckUpdate:
                CheckUpdate();
                break;
            case BridgeCommand.InstallUpdate:
                // Leave the WebView2 callback first. Install exits the app, and that
                // disposes the WebView2 control. A dispose inside its own event can hang.
                BeginInvoke(new Action(() => _bridge.Updates?.InstallNow()));
                break;
            case BridgeCommand.RefreshCloud:
                if (_demo)
                {
                    _bridge.Toast("Demo data does not refresh.", "warn");
                    break;
                }

                _ = _cloud.RefreshAsync();
                break;
            case BridgeCommand.SetRegion when message.RegionId is { } regionId:
                SetRegion(regionId);
                break;
            case BridgeCommand.OpenUrl:
                OpenUrl(message.Url);
                break;
            case BridgeCommand.SetSetting when message.Setting is { } setting:
                ApplySetting(setting, message.Value);
                break;
        }
    }

    private void CheckUpdate()
    {
        var updates = _bridge.Updates;
        if (updates == null || !UpdateFeed.Current.Enabled)
        {
            _bridge.Toast("Update checks are off in the Dev build.", "warn");
            return;
        }

        _ = updates.CheckNowAsync(userInitiated: true);
    }

    private void OpenUrl(string? url)
    {
        if (!UrlPolicy.TryAllow(url, out var allowed))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(allowed.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            _bridge.Toast("Could not open the browser.", "bad");
        }
    }

    private void ApplySetting(BridgeSetting setting, bool value)
    {
        switch (setting)
        {
            case BridgeSetting.StartWithWindows:
                if (!AppIsolation.AllowsStartupRegistry(AppIsolation.Profile))
                {
                    _bridge.Toast("Start with Windows is off in the Dev build.", "warn");
                    _bridge.RefreshStartup();
                    break;
                }

                if (_startup.Checked != value && !_startup.Toggle())
                    _bridge.Toast("Could not change Start with Windows.", "bad");
                _bridge.RefreshStartup();
                break;
            case BridgeSetting.AutoInstallUpdates:
                if (_bridge.Updates is { } updates)
                    updates.AutoInstall = value;
                _bridge.MarkDirty();
                break;
            case BridgeSetting.CloudEnabled:
                _settings.CloudEnabled = value;
                // Demo shows fixed cloud data and makes no live calls.
                if (!_demo)
                    _cloud.SetEnabled(value);
                _bridge.MarkDirty();
                break;
        }
    }

    /// <summary>
    /// "auto" goes back to the automatic pick; any other id is a manual choice. The scheduler
    /// hears Region.Changed and traces the new target (Production only). Dev and demo only
    /// save and show the choice.
    /// </summary>
    private void SetRegion(string id)
    {
        if (id == "auto")
            _core.Region.SetAuto();
        else if (!_core.Region.SetManual(id))
            _bridge.Toast("Unknown region.", "bad");
        _bridge.MarkDirty();
    }

    private static string UserDataFolder(RunProfile profile)
    {
        if (profile == RunProfile.Production)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "GeckoPeeks", "WebView2");
        }

        var root = AppIsolation.ReadDevScratchRoot();
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), "GeckoPeeksDev", "WebView2")
            : Path.Combine(Path.GetFullPath(root), "WebView2");
    }

    /// <summary>Dev builds may open on one page (GECKO_PEEKS_START_PAGE) for screenshots.</summary>
    private static string StartUrl(RunProfile profile)
    {
        var url = WebAssets.Origin + "/" + WebAssets.StartFile;
        if (profile != RunProfile.Dev)
            return url;

        var page = Environment.GetEnvironmentVariable("GECKO_PEEKS_START_PAGE")?.Trim().ToLowerInvariant();
        return page != null && StartPages.Contains(page) ? url + "#/" + page : url;
    }
}
