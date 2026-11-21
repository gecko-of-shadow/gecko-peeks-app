using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GeckoPeeks.Ui.WebHost;

/// <summary>
/// Owns the WebView2 control inside the main window. It is made on the first show and thrown
/// away after the window was hidden for a while, so the tray copy keeps no browser processes.
/// The page comes only from the embedded files at https://gecko.peeks.invalid/. Every other
/// navigation and every new window is refused.
/// </summary>
internal sealed class WebSurface : IDisposable
{
    public static readonly Color Background = Color.FromArgb(0x0f, 0x0f, 0x10);

    /// <summary>This many browser crashes inside <see cref="CrashWindow"/> show the fallback panel.</summary>
    private const int MaxCrashes = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(2);

    /// <summary>The fallback panel is not retried more often than this (Show raises two calls).</summary>
    private static readonly TimeSpan FallbackRetryGap = TimeSpan.FromSeconds(2);

    private readonly Control _host;
    private readonly string _userDataFolder;
    private readonly bool _devMode;
    private readonly string _startUrl;
    private WebView2? _view;
    private Control? _fallback;
    private DateTime _fallbackShownUtc;
    private readonly Queue<DateTime> _crashes = new();
    private int _generation;
    private bool _pageReady;
    private bool _hidden;
    private bool _disposed;

    public WebSurface(Control host, string userDataFolder, bool devMode, string startUrl)
    {
        _host = host;
        _userDataFolder = userDataFolder;
        _devMode = devMode;
        _startUrl = startUrl;
    }

    /// <summary>Raw JSON the page posted (WebMessageAsJson).</summary>
    public event Action<string>? MessageReceived;

    /// <summary>True between the page's "ready" and the next navigation or release.</summary>
    public bool PageReady => _pageReady && _view?.CoreWebView2 != null;

    public bool IsCreated => _view != null;

    /// <summary>True from SuspendAsync until Resume. While hidden, the host posts nothing.</summary>
    public bool IsHidden => _hidden;

    /// <summary>
    /// The window went to the tray: hide the view and ask WebView2 to suspend its page, so a
    /// hidden window costs no script time. Any failure only leaves the view awake.
    /// </summary>
    public async Task SuspendAsync()
    {
        var view = _view;
        if (_disposed || view == null || view.IsDisposed)
            return;
        _hidden = true;
        var generation = _generation;
        try
        {
            view.Visible = false;
            var core = view.CoreWebView2;
            if (core == null || !_pageReady || core.IsSuspended)
                return;
            await core.TrySuspendAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Not ready, already gone or not allowed now: stay awake, nothing breaks.
            return;
        }

        // A show that came while the suspend ran: wake the page again at once. A release in
        // the meantime changed the generation; then the old view is not touched.
        if (generation != _generation || !ReferenceEquals(view, _view) || view.IsDisposed)
            return;
        if (!_hidden)
            ResumeCore(view);
    }

    /// <summary>The window is shown again: make the view visible and wake a suspended page.</summary>
    public void Resume()
    {
        _hidden = false;
        var view = _view;
        if (_disposed || view == null || view.IsDisposed)
            return;
        try
        {
            view.Visible = true;
        }
        catch (Exception)
        {
            // Being disposed.
        }

        ResumeCore(view);
    }

    private static void ResumeCore(WebView2 view)
    {
        try
        {
            var core = view.CoreWebView2;
            if (core != null && core.IsSuspended)
                core.Resume();
        }
        catch (Exception)
        {
            // Gone: the next show makes a new view.
        }
    }

    /// <summary>Called by the host when the page sent {cmd:"ready"}.</summary>
    public void MarkPageReady() => _pageReady = true;

    /// <summary>
    /// Makes the WebView2 when there is none. Safe to call on every show. When the fallback
    /// panel shows, each new show removes it and tries again (the runtime may be installed now).
    /// </summary>
    public async void EnsureCreated()
    {
        if (_disposed || _view != null)
            return;
        if (_fallback != null)
        {
            if (DateTime.UtcNow - _fallbackShownUtc < FallbackRetryGap)
                return;
            RemoveFallback();
        }

        string? version;
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            version = null;
        }
        catch (Exception)
        {
            version = null;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            ShowFallback("The Microsoft Edge WebView2 Runtime is not installed on this PC.");
            return;
        }

        var generation = ++_generation;
        var view = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Background,
            BackColor = Background,
        };
        _view = view;
        _host.Controls.Add(view);

        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var options = new CoreWebView2EnvironmentOptions
            {
                AllowSingleSignOnUsingOSPrimaryAccount = false,
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options).ConfigureAwait(true);
            if (generation != _generation || view.IsDisposed)
                return;

            await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (generation != _generation || view.IsDisposed || view.CoreWebView2 == null)
                return;

            Configure(view.CoreWebView2, environment);
            view.CoreWebView2.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            Release();
            ShowFallback("The window could not start its web view: " + ex.Message);
        }
    }

    /// <summary>Sends a JSON message to the page when it is listening.</summary>
    public bool Post(string json)
    {
        var core = _view?.CoreWebView2;
        if (!_pageReady || core == null)
            return false;
        try
        {
            core.PostWebMessageAsJson(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Disposes the WebView2. Its browser processes exit soon after.</summary>
    public void Release()
    {
        _generation++;
        _pageReady = false;
        _hidden = false;
        var view = _view;
        _view = null;
        if (view == null)
            return;

        try
        {
            _host.Controls.Remove(view);
            view.Dispose();
        }
        catch
        {
            // Already gone.
        }

        WebAssets.ClearCache();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Release();
        _fallback?.Dispose();
        _fallback = null;
    }

    private void Configure(CoreWebView2 core, CoreWebView2Environment environment)
    {
        var settings = core.Settings;
        settings.AreDevToolsEnabled = _devMode;
        settings.AreDefaultContextMenusEnabled = _devMode;
        settings.AreBrowserAcceleratorKeysEnabled = _devMode;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsWebMessageEnabled = true;
        settings.IsScriptEnabled = true;

        core.AddWebResourceRequestedFilter(WebAssets.Filter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Serve(environment, e);
        core.NavigationStarting += (_, e) =>
        {
            if (!WebAssets.IsAppUrl(e.Uri))
                e.Cancel = true;
        };
        // A new document (first load or reload) sends "ready" again. Hash changes from the
        // sidebar are same-document navigations and do not raise ContentLoading.
        core.ContentLoading += (_, _) => _pageReady = false;
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!WebAssets.IsAppUrl(e.Uri))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (_, e) =>
        {
            if (!WebAssets.IsAppUrl(e.Source))
                return;
            string json;
            try
            {
                json = e.WebMessageAsJson;
            }
            catch
            {
                return;
            }

            MessageReceived?.Invoke(json);
        };
        core.ProcessFailed += (_, e) =>
        {
            // GPU and utility process exits recover by themselves. Only a lost browser or
            // page process needs a new WebView2.
            if (_host.IsDisposed || !NeedsRestart(e.ProcessFailedKind))
                return;
            _host.BeginInvoke(new Action(() =>
            {
                Release();
                if (TooManyCrashes())
                {
                    ShowFallback("The window's web view stopped several times in a row.");
                    return;
                }

                if (_host.Visible)
                    EnsureCreated();
            }));
        };
    }

    private static bool NeedsRestart(CoreWebView2ProcessFailedKind kind) => kind
        is CoreWebView2ProcessFailedKind.BrowserProcessExited
        or CoreWebView2ProcessFailedKind.RenderProcessExited
        or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;

    /// <summary>Records one crash and says whether the recent ones reached the limit.</summary>
    private bool TooManyCrashes()
    {
        var now = DateTime.UtcNow;
        _crashes.Enqueue(now);
        while (_crashes.Count > 0 && now - _crashes.Peek() > CrashWindow)
            _crashes.Dequeue();
        if (_crashes.Count < MaxCrashes)
            return false;
        _crashes.Clear();
        return true;
    }

    private void RemoveFallback()
    {
        var panel = _fallback;
        _fallback = null;
        if (panel == null)
            return;
        try
        {
            _host.Controls.Remove(panel);
            panel.Dispose();
        }
        catch
        {
            // Already gone.
        }
    }

    private static void Serve(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (!string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                || !WebAssets.TryGet(e.Request.Uri, out var bytes, out var type))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(bytes, writable: false),
                200,
                "OK",
                "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        }
        catch
        {
            // Leave the request unanswered; WebView2 shows nothing for it.
        }
    }

    private void ShowFallback(string reason)
    {
        if (_fallback != null || _host.IsDisposed)
            return;

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(48) };
        var text = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(0xf4, 0xf4, 0xf5),
            BackColor = Background,
            Font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = reason + "\n\nGecko Peeks keeps testing and logging in the tray. "
                + "Install the Microsoft Edge WebView2 Runtime, then open this window again. "
                + "Right-click the gecko icon for Open log and Copy diagnosis.",
        };
        panel.Controls.Add(text);
        _fallback = panel;
        _fallbackShownUtc = DateTime.UtcNow;
        _host.Controls.Add(panel);
        panel.BringToFront();
    }
}
