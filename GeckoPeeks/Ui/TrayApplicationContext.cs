using System.Drawing.Drawing2D;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Text;
using GeckoPeeks.Ui.State;
using Microsoft.Win32;

namespace GeckoPeeks.Ui;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly MainForm _form;
    private readonly ProbeScheduler _scheduler;
    private readonly CoreServices _core;
    private readonly CloudPoller _cloud;
    private readonly UpdateService _updates;
    private readonly StartupToggle _startup = new(StartupRegistration.IsEnabled, StartupRegistration.SetEnabled);
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly System.Windows.Forms.Timer _clock;
    private Version? _balloonedReady;
    private bool _userCheckPending;
    private bool _exiting;
    private bool _closedHintShown;

    public TrayApplicationContext(SingleInstance instance, bool showWindow)
    {
        var profile = AppIsolation.Profile;
        var devRoot = AppIsolation.ReadDevScratchRoot();
        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var paths = AppIsolation.LaunchPaths(profile, LogPaths.Resolve, devRoot, exeDirectory);
        AppUpdate.CleanupLeftovers(Environment.ProcessPath);

        var demo = DemoData.IsOn(profile, Environment.GetEnvironmentVariable(DemoData.Variable));
        var settings = new HostSettings(HostSettings.PathFor(profile, devRoot));
        // Demo shows fixed cloud data. Its poller stays off, so no live call can feed the
        // region pick or the page. The demo edge gives the frozen automatic pick (na1, country).
        _core = CoreServices.Create(profile, devRoot, !demo && settings.CloudEnabled);
        if (demo)
            _core.Region.OnEdgeJson(DemoData.EdgeJson);
        _scheduler = new ProbeScheduler(paths, profile, _core)
        {
            // Dev only, never with demo: one real test, logged in the dev root.
            DevSingleProbe = AppIsolation.DevProbeOn(
                profile, Environment.GetEnvironmentVariable(AppIsolation.DevProbeVariable), demo),
        };
        _cloud = _core.Cloud;
        _form = new MainForm(_scheduler, _core, settings, _startup, demo);
        _ = _form.Handle;

        // The form handle exists, so this is the WinForms context: Changed and the automatic
        // install run on the UI thread.
        _updates = new UpdateService(
            new UpdateClient(),
            CanAutoInstall,
            () => Exit(),
            SynchronizationContext.Current,
            profile);
        _form.AttachUpdates(_updates);

        _icon = new NotifyIcon
        {
            Icon = GeckoMark.CreateIcon(SystemInformation.SmallIconSize),
            Visible = true,
            Text = "Gecko Peeks — no route yet",
        };
        // Windows 11 tucks new tray icons under the ^ overflow, where a double-click is easy to
        // miss. A single left click opens the window too.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _form.ShowFromTray();
        };
        _icon.DoubleClick += (_, _) => _form.ShowFromTray();
        _icon.BalloonTipClicked += (_, _) => _form.ShowFromTray();
        _form.HiddenToTray += ShowClosedHint;

        _pauseItem = CommandItem(TrayCommand.TogglePause);
        _startupItem = CommandItem(TrayCommand.StartWithWindows);
        _updateItem = CommandItem(TrayCommand.CheckForUpdates);
        _icon.ContextMenuStrip = BuildMenu();

        _scheduler.Updated += snapshot =>
        {
            _icon.Text = Fit(snapshot.Tooltip);
            _pauseItem.Text = TrayMenu.Label(TrayCommand.TogglePause, snapshot.Paused);
        };
        _scheduler.RouteChanged += message =>
            _icon.ShowBalloonTip(6000, "Gecko Peeks", TrayMenu.FitBalloon(message), ToolTipIcon.Info);
        _updates.Changed += OnUpdateChanged;

        instance.Listen(() => _form.BeginInvoke(new Action(_form.ShowFromTray)));
        _scheduler.Start();
        if (!demo)
            _cloud.Start();
        _updates.Start();
        StartupRegistration.RepairIfStale();
        _startupItem.Checked = _startup.Checked;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _clock = new System.Windows.Forms.Timer { Interval = 15000 };
        _clock.Tick += (_, _) => _icon.Text = Fit(_scheduler.Capture().Tooltip);
        _clock.Start();

        if (showWindow)
            _form.ShowFromTray();
    }

    /// <summary>The automatic install waits for a hidden window, no test and no League.</summary>
    private bool CanAutoInstall()
    {
        if (_exiting || _form.IsDisposed || _form.Visible)
            return false;
        var snapshot = _scheduler.Capture();
        return !snapshot.TestRunning && !snapshot.LeagueRunning;
    }

    private ContextMenuStrip BuildMenu()
    {
        // ShowCheckMargin is required for ToolStripMenuItem.Checked glyphs.
        // ShowImageMargin=false alone hides the check column entirely.
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = true,
            ShowCheckMargin = true,
        };
        menu.Opening += (_, _) => _startupItem.Checked = _startup.Checked;

        foreach (var entry in TrayMenu.Layout)
        {
            if (entry is not { } command)
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            var item = command switch
            {
                TrayCommand.TogglePause => _pauseItem,
                TrayCommand.StartWithWindows => _startupItem,
                TrayCommand.CheckForUpdates => _updateItem,
                _ => CommandItem(command),
            };
            if (command == TrayMenu.DefaultCommand)
                item.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(item);
        }

        return menu;
    }

    private ToolStripMenuItem CommandItem(TrayCommand command)
    {
        var item = new ToolStripMenuItem(TrayMenu.Label(command)) { ForeColor = Theme.Text };
        item.Click += (_, _) => Run(command);
        return item;
    }

    private void Run(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenWindow:
                _form.ShowFromTray();
                break;
            case TrayCommand.RunTestNow:
                _scheduler.RunNow();
                break;
            case TrayCommand.CopyDiagnosis:
                _form.CopyDiagnosis();
                break;
            case TrayCommand.OpenLog:
                _form.OpenLog();
                break;
            case TrayCommand.OpenLogFolder:
                _form.OpenLogFolder();
                break;
            case TrayCommand.TogglePause:
                _scheduler.TogglePause();
                break;
            case TrayCommand.StartWithWindows:
                ToggleStartup();
                break;
            case TrayCommand.CheckForUpdates:
                OnUpdateCommand();
                break;
            case TrayCommand.Exit:
                Exit();
                break;
        }
    }

    /// <summary>Install update when one is ready, otherwise a check whose result is a balloon.</summary>
    private void OnUpdateCommand()
    {
        if (_exiting)
            return;
        if (!UpdateFeed.Current.Enabled)
        {
            Balloon("Update checks are off in the Dev build.");
            return;
        }

        var status = _updates.Status;
        if (status.State == UpdateState.Ready)
        {
            _updates.InstallNow();
            return;
        }

        if (status.State is UpdateState.Checking or UpdateState.Downloading)
        {
            Balloon(status.State == UpdateState.Downloading && status.Available != null
                ? "Downloading v" + AppUpdate.FormatVersion(status.Available) + "…"
                : "Still checking for an update.");
            return;
        }

        _userCheckPending = true;
        _ = _updates.CheckNowAsync(userInitiated: true);
    }

    private void OnUpdateChanged(UpdateStatus status)
    {
        if (_exiting)
            return;

        var ready = status.State == UpdateState.Ready && status.Available != null
            ? AppUpdate.FormatVersion(status.Available)
            : null;
        _updateItem.Text = TrayMenu.Label(TrayCommand.CheckForUpdates, updateVersion: ready);

        if (ready != null && status.Available != _balloonedReady)
        {
            _balloonedReady = status.Available;
            _userCheckPending = false;
            Balloon("v" + ready + " is ready. Click Restart to update in the window, or right-click the gecko > Install update.");
            return;
        }

        if (!_userCheckPending)
            return;

        switch (status.State)
        {
            case UpdateState.UpToDate:
                _userCheckPending = false;
                Balloon("You're on v" + AppUpdate.FormatVersion(status.Current) + " (latest).");
                break;
            case UpdateState.Downloading when status.Available != null && status.Progress is null or 0:
                Balloon("Downloading v" + AppUpdate.FormatVersion(status.Available) + "…");
                break;
            case UpdateState.Error:
                _userCheckPending = false;
                // status.Error already starts with "Could not check / download / install ...".
                Balloon(string.IsNullOrWhiteSpace(status.Error) ? "Could not check for updates." : status.Error);
                break;
        }
    }

    private void Balloon(string text) =>
        _icon.ShowBalloonTip(8000, "Gecko Peeks", TrayMenu.FitBalloon(text), ToolTipIcon.Info);

    private void ToggleStartup()
    {
        if (!_startup.Toggle())
            _form.ShowFromTray();

        // Reflect the real HKCU Run value, not the intended toggle.
        _startupItem.Checked = _startup.Checked;
    }

    private void ShowClosedHint()
    {
        if (_closedHintShown || _exiting)
            return;

        _closedHintShown = true;
        _icon.ShowBalloonTip(6000, TrayMenu.ClosedHintTitle, TrayMenu.ClosedHintText, ToolTipIcon.Info);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _exiting)
            return;
        _updates.CheckAfterResume();
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        _clock.Stop();
        try
        {
            // Logoff/shutdown will kill the process; finish the runner synchronously.
            _scheduler.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown.
        }

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            _updates.Dispose();
            // The scheduler first: it cancels a running test before its resolver and clients go.
            _scheduler.Dispose();
            // CoreServices flushes the ASN cache and disposes the poller and the clients.
            _core.Dispose();
        }
        catch
        {
            // Process is ending.
        }
    }

    private async void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;

        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _clock.Stop();
        try
        {
            await _scheduler.StopAsync().ConfigureAwait(true);
        }
        catch
        {
            // Still leave the tray.
        }

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            // Dispose also disposes the UpdateClient it was given.
            _updates.Dispose();
            // The scheduler first, then the core (see OnSessionEnding).
            _scheduler.Dispose();
            _core.Dispose();
        }
        catch (Exception ex)
        {
            // Still leave. A process with no tray icon must not keep the mutex.
            CrashLog.Write(ex);
        }
        finally
        {
            try
            {
                _form.ExitApplication();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }

            ExitThread();
        }
    }

    private static string Fit(string text) => text.Length <= 127 ? text : text[..127];

    /// <summary>A light tick in an orange-edged box, readable on the idle and the hover row.</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new MenuColors())
        {
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var box = Rectangle.Inflate(e.ImageRectangle, 1, 1);
            using (var fill = new SolidBrush(Theme.SurfaceRaised))
                g.FillRectangle(fill, box);
            using (var border = new Pen(Theme.Accent))
                g.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);

            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var tick = new Pen(Theme.Text, Math.Max(2f, box.Height / 7f)))
            {
                tick.StartCap = LineCap.Round;
                tick.EndCap = LineCap.Round;
                tick.LineJoin = LineJoin.Round;
                g.DrawLines(tick, new[] { At(box, 0.24f, 0.52f), At(box, 0.43f, 0.70f), At(box, 0.77f, 0.31f) });
            }

            g.SmoothingMode = smoothing;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextMuted;
            base.OnRenderItemText(e);
        }

        private static PointF At(Rectangle box, float x, float y) =>
            new(box.X + box.Width * x, box.Y + box.Height * y);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuItemSelected => Theme.SurfaceRaised;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceRaised;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceRaised;
        public override Color MenuItemPressedGradientBegin => Theme.Ink;
        public override Color MenuItemPressedGradientEnd => Theme.Ink;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color CheckBackground => Theme.SurfaceRaised;
        public override Color CheckSelectedBackground => Theme.SurfaceRaised;
        public override Color CheckPressedBackground => Theme.Ink;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
