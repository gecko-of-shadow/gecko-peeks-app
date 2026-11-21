using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using GeckoPeeks.App;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// Drives <see cref="UpdateService"/> with a fake update feed, a fake clock and temp folders.
/// No test touches %LOCALAPPDATA%, starts a swap script, or waits on a real timer.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string FeedBase = "https://feed.test/";
    private const string ApiUrl = "https://feed.test/v1/update/latest?channel=stable";
    private const string DevApiUrl = "https://feed.test/v1/update/latest?channel=devtest";
    private const string ExeUrl = "https://feed.test/v1/update/download/stable/2.0.0";
    private const string DevExeUrl = "https://feed.test/v1/update/download/devtest/2.0.0";

    private static readonly Version Current = new(1, 0, 16);
    private static readonly byte[] Exe = FakeExe();
    private static readonly string ExeHash = Convert.ToHexString(SHA256.HashData(Exe)).ToLowerInvariant();

    private readonly string _folder;
    private readonly FakeFeed _feed = new();
    private readonly FakeTimers _timers = new();
    private readonly List<UpdateStatus> _changes = new();
    private readonly List<(string Exe, string Next, string Args)> _replaces = new();
    private int _exitRequests;
    private bool _canAutoInstall = true;

    public UpdateServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    private string ExePath => Path.Combine(_folder, "GeckoPeeks.exe");
    private string SkipPath => Path.Combine(_folder, "skipped-update.txt");
    private string SettingsPath => Path.Combine(_folder, "update-settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    [Fact]
    public async Task A_new_release_goes_Idle_Checking_Downloading_Ready()
    {
        _feed.Release(Manifest("2.0.0", notes: "New radar UI."));
        using var service = Create();
        Assert.Equal(UpdateState.Idle, service.Status.State);

        await service.CheckNowAsync(userInitiated: false);

        var states = Distinct(_changes.Select(c => c.State));
        Assert.Equal(new[] { UpdateState.Checking, UpdateState.Downloading, UpdateState.Ready }, states);
        var status = service.Status;
        Assert.Equal(new Version(2, 0, 0), status.Available);
        Assert.Equal("New radar UI.", status.Notes);
        Assert.Null(status.Progress);
        Assert.NotNull(status.LastCheckUtc);
        Assert.Equal("https://github.com/gecko-of-shadow/gecko-peeks-app/releases", status.ReleaseUrl);
        Assert.True(File.Exists(ExePath + ".new"));
    }

    [Fact]
    public async Task Download_reports_progress_from_0_to_1()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();

        await service.CheckNowAsync(false);

        var progress = _changes
            .Where(c => c.State == UpdateState.Downloading && c.Progress != null)
            .Select(c => c.Progress!.Value)
            .ToList();
        Assert.True(progress.Count >= 3, "Expected several progress steps, got " + progress.Count);
        Assert.Equal(0, progress[0]);
        Assert.Equal(1.0, progress[^1]);
        Assert.All(progress.Zip(progress.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
    }

    [Fact]
    public async Task A_digest_mismatch_deletes_the_file_and_sets_Error()
    {
        _feed.Release(Manifest("2.0.0", sha256: new string('a', 64)));
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Contains("SHA-256", service.Status.Error);
        Assert.False(File.Exists(ExePath + ".new"));
        Assert.False(File.Exists(ExePath + ".new.partial"));
    }

    [Fact]
    public async Task A_manifest_without_sha256_is_Error_and_downloads_nothing()
    {
        _feed.Release(Manifest("2.0.0", sha256: ""));
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Contains("SHA-256", service.Status.Error);
        Assert.Equal(0, _feed.Calls(ExeUrl));
        Assert.False(File.Exists(ExePath + ".new"));
    }

    [Fact]
    public async Task A_size_mismatch_is_rejected()
    {
        _feed.Release(Manifest("2.0.0", size: Exe.Length + 1));
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Contains("size", service.Status.Error);
        Assert.False(File.Exists(ExePath + ".new"));
        Assert.False(File.Exists(ExePath + ".new.partial"));
    }

    [Fact]
    public async Task A_download_link_on_another_host_is_rejected()
    {
        _feed.Release(Manifest("2.0.0", url: "https://evil.test/GeckoPeeks.exe"));
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Equal(1, _feed.TotalCalls);
    }

    [Fact]
    public async Task No_release_on_the_channel_is_UpToDate()
    {
        _feed.NoRelease = true;
        using var service = Create();

        await service.CheckNowAsync(true);

        Assert.Equal(UpdateState.UpToDate, service.Status.State);
        Assert.Equal("", service.Status.Error);
    }

    [Fact]
    public async Task Another_404_is_Error()
    {
        _feed.NotFound = true;
        using var service = Create();

        await service.CheckNowAsync(true);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Contains("404", service.Status.Error);
    }

    [Fact]
    public async Task The_same_version_is_UpToDate()
    {
        _feed.Release(Manifest("1.0.16"));
        using var service = Create();

        await service.CheckNowAsync(true);

        Assert.Equal(UpdateState.UpToDate, service.Status.State);
        Assert.Null(service.Status.Available);
        Assert.Equal(0, _feed.Calls(ExeUrl));
    }

    [Fact]
    public async Task A_network_failure_sets_Error()
    {
        _feed.FailApi = true;
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.False(string.IsNullOrWhiteSpace(service.Status.Error));
        Assert.NotNull(service.Status.LastCheckUtc);
    }

    [Fact]
    public async Task An_auto_check_honours_a_skipped_version()
    {
        _feed.Release(Manifest("2.0.0"));
        UpdateSkip.Skip(new Version(2, 0, 0), RunProfile.Production, SkipPath);
        using var service = Create();

        await service.CheckNowAsync(userInitiated: false);

        Assert.Equal(UpdateState.UpToDate, service.Status.State);
        Assert.Equal(0, _feed.Calls(ExeUrl));
        Assert.True(File.Exists(SkipPath));
    }

    [Fact]
    public async Task A_user_check_clears_the_skipped_version()
    {
        _feed.Release(Manifest("2.0.0"));
        UpdateSkip.Skip(new Version(2, 0, 0), RunProfile.Production, SkipPath);
        using var service = Create();

        await service.CheckNowAsync(userInitiated: true);

        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.False(File.Exists(SkipPath));
    }

    [Fact]
    public async Task Skip_writes_the_version_and_drops_the_download()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();
        await service.CheckNowAsync(false);

        service.Skip();

        Assert.Equal(UpdateState.Idle, service.Status.State);
        Assert.True(UpdateSkip.IsSkipped(new Version(2, 0, 0), RunProfile.Production, SkipPath));
        Assert.False(File.Exists(ExePath + ".new"));
    }

    [Fact]
    public async Task A_recheck_while_Ready_keeps_Ready_and_does_not_download_again()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();
        await service.CheckNowAsync(false);
        _changes.Clear();

        await service.CheckNowAsync(false);
        _feed.FailApi = true;
        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.DoesNotContain(_changes, c => c.State != UpdateState.Ready);
        Assert.Equal(1, _feed.Calls(ExeUrl));
    }

    [Fact]
    public async Task Start_checks_after_12_seconds_then_every_3_hours()
    {
        _feed.Release(Manifest("1.0.16"));
        using var service = Create();
        service.Start();
        service.Start();

        await _timers.AdvanceAsync(TimeSpan.FromSeconds(11));
        Assert.Equal(0, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromHours(3));
        Assert.Equal(2, _feed.Calls(ApiUrl));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(3, 80)]
    [InlineData(4, 80)]
    [InlineData(50, 80)]
    public void RetryDelay_doubles_from_10_to_80_minutes(int failedBefore, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), UpdateService.RetryDelay(failedBefore));

    [Fact]
    public async Task A_failed_background_check_retries_after_10_then_20_minutes()
    {
        _feed.FailApi = true;
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(1, _feed.Calls(ApiUrl));
        Assert.Equal(UpdateState.Error, service.Status.State);

        await _timers.AdvanceAsync(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Calls(ApiUrl));

        await _timers.AdvanceAsync(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, _feed.Calls(ApiUrl));
    }

    [Fact]
    public async Task A_retry_that_works_downloads_and_stops_retrying()
    {
        _feed.Release(Manifest("2.0.0"));
        _feed.FailApi = true;
        using var service = Create();
        service.AutoInstall = false;
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(UpdateState.Error, service.Status.State);

        _feed.FailApi = false;
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.Equal(2, _feed.Calls(ApiUrl));

        // No more retries: only the 3 h check remains.
        await _timers.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(2, _feed.Calls(ApiUrl));
    }

    [Fact]
    public async Task A_check_without_Start_schedules_no_retry()
    {
        _feed.FailApi = true;
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Equal(0, _timers.Scheduled);
    }

    [Fact]
    public async Task A_blocked_folder_says_so_and_downloads_nothing()
    {
        _feed.Release(Manifest("2.0.0"));
        // A folder where the temp file must go makes Windows refuse the write, as Controlled Folder Access does.
        Directory.CreateDirectory(ExePath + ".new.partial");
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Contains("Controlled Folder Access", service.Status.Error);
        Assert.Contains(@"%LOCALAPPDATA%\Programs\GeckoPeeks", service.Status.Error);
        Assert.Equal(0, _feed.Calls(ExeUrl));
    }

    [Fact]
    public async Task A_failed_download_retries_and_a_good_retry_resets_the_backoff()
    {
        _feed.Release(Manifest("2.0.0"));
        _feed.FailExe = true;
        using var service = Create();
        service.AutoInstall = false;
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Equal(1, _feed.Calls(ExeUrl));

        await _timers.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(2, _feed.Calls(ExeUrl));

        _feed.FailExe = false;
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(20));
        Assert.Equal(3, _feed.Calls(ExeUrl));
        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.True(File.Exists(ExePath + ".new"));
    }

    [Fact]
    public async Task A_blocked_folder_gets_no_quick_retry_but_the_3_hour_check_still_runs()
    {
        _feed.Release(Manifest("2.0.0"));
        Directory.CreateDirectory(ExePath + ".new.partial");
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        Assert.Contains("Controlled Folder Access", service.Status.Error);
        Assert.Equal(1, _feed.Calls(ApiUrl));

        await _timers.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(1, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(2, _feed.Calls(ApiUrl));
        Assert.Equal(0, _feed.Calls(ExeUrl));
    }

    [Fact]
    public async Task A_good_check_resets_the_backoff_to_10_minutes()
    {
        _feed.Release(Manifest("1.0.16"));
        _feed.FailApi = true;
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));          // fail 1 -> retry in 10 min
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(10));          // fail 2 -> retry in 20 min
        Assert.Equal(2, _feed.Calls(ApiUrl));

        _feed.FailApi = false;
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(20));          // good -> reset
        Assert.Equal(3, _feed.Calls(ApiUrl));
        Assert.Equal(UpdateState.UpToDate, service.Status.State);

        _feed.FailApi = true;
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(150));         // the 3 h check at 3:00:12 fails
        Assert.Equal(4, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(4, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(1));           // 10 min again, not 40
        Assert.Equal(5, _feed.Calls(ApiUrl));
    }

    [Fact]
    public async Task A_download_that_fails_its_check_gets_no_quick_retry()
    {
        _feed.Release(Manifest("2.0.0", sha256: new string('a', 64)));
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Equal(1, _feed.Calls(ExeUrl));

        await _timers.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(1, _feed.Calls(ExeUrl));
        await _timers.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(2, _feed.Calls(ExeUrl));
    }

    [Theory]
    [InlineData("http", true)]
    [InlineData("timeout", true)]
    [InlineData("canceled", true)]
    [InlineData("io", true)]
    [InlineData("blocked", false)]
    [InlineData("checksum", false)]
    [InlineData("manifest", false)]
    public void IsTransient_retries_only_network_type_failures(string kind, bool expected)
    {
        Exception ex = kind switch
        {
            "http" => new HttpRequestException("502"),
            "timeout" => new TimeoutException(),
            "canceled" => new TaskCanceledException(),
            "io" => new IOException("reset"),
            "blocked" => new UpdateFolderBlockedException(new UnauthorizedAccessException()),
            "checksum" => new InvalidOperationException("The download did not match its SHA-256 checksum."),
            _ => new InvalidDataException("bad manifest"),
        };
        Assert.Equal(expected, UpdateService.IsTransient(ex));
    }

    [Fact]
    public void FitBalloon_never_splits_an_emoji()
    {
        var text = new string('x', 253) + "\U0001F98E" + new string('y', 10);
        var fit = GeckoPeeks.Text.TrayMenu.FitBalloon(text);
        Assert.True(fit.Length <= 255);
        Assert.False(char.IsHighSurrogate(fit[^2]));
    }

    [Fact]
    public async Task Other_download_errors_do_not_name_Controlled_Folder_Access()
    {
        _feed.Release(Manifest("2.0.0"));
        _feed.FailExe = true;
        using var service = Create();

        await service.CheckNowAsync(false);

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.DoesNotContain("Controlled Folder Access", service.Status.Error);
    }

    [Fact]
    public void The_blocked_folder_error_fits_a_tray_balloon_whole()
    {
        var text = "Could not download the update: " + UpdateService.BlockedFolderMessage;
        Assert.True(text.Length <= GeckoPeeks.Text.TrayMenu.MaxBalloonChars, "Length " + text.Length);
        Assert.Equal(text, GeckoPeeks.Text.TrayMenu.FitBalloon(text));
    }

    [Fact]
    public void FitBalloon_cuts_long_text_at_a_word_to_255_chars()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        var fit = GeckoPeeks.Text.TrayMenu.FitBalloon(text);
        Assert.True(fit.Length <= 255, "Length " + fit.Length);
        Assert.EndsWith("word…", fit);
        Assert.Equal("", GeckoPeeks.Text.TrayMenu.FitBalloon(null));
        Assert.Equal(new string('x', 254) + "…", GeckoPeeks.Text.TrayMenu.FitBalloon(new string('x', 400)));
    }

    [Fact]
    public async Task CheckAfterResume_checks_30_seconds_later()
    {
        _feed.Release(Manifest("1.0.16"));
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));

        service.CheckAfterResume();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.Equal(1, _feed.Calls(ApiUrl));
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _feed.Calls(ApiUrl));
    }

    [Fact]
    public async Task Auto_install_waits_for_10_idle_minutes_then_restarts_in_the_tray()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create(restartArguments: "");
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(UpdateState.Ready, service.Status.State);

        // The first gate sample at 30 s starts the 10 minutes.
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(600 - 12));
        Assert.Empty(_replaces);
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(60));

        var replace = Assert.Single(_replaces);
        Assert.Equal(ExePath, replace.Exe);
        Assert.Equal(ExePath + ".new", replace.Next);
        Assert.Equal("--tray", replace.Args);
        Assert.Equal(1, _exitRequests);
        Assert.Equal(UpdateState.Installing, service.Status.State);
    }

    [Fact]
    public async Task One_busy_sample_restarts_the_10_minutes()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));

        await _timers.AdvanceAsync(TimeSpan.FromMinutes(8));
        _canAutoInstall = false;
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(30));
        _canAutoInstall = true;
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(9));
        Assert.Empty(_replaces);

        await _timers.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Single(_replaces);
    }

    [Fact]
    public async Task Sleep_time_does_not_count_as_idle()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();
        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(12));
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(2));

        // The PC sleeps for an hour. No tick fires while it sleeps.
        _timers.Sleep(TimeSpan.FromHours(1));
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(_replaces);

        await _timers.AdvanceAsync(TimeSpan.FromMinutes(9));
        Assert.Empty(_replaces);
        await _timers.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Single(_replaces);
    }

    [Fact]
    public async Task Auto_install_off_never_installs()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();
        service.AutoInstall = false;
        service.Start();

        await _timers.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.Empty(_replaces);
        Assert.Equal(0, _exitRequests);
    }

    [Fact]
    public async Task InstallNow_drops_tray_so_the_window_shows_and_requests_exit()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create(restartArguments: "--tray --foo");
        service.InstallNow();
        Assert.Empty(_replaces);

        await service.CheckNowAsync(false);
        service.InstallNow();

        var replace = Assert.Single(_replaces);
        Assert.Equal("--foo", replace.Args);
        Assert.Equal(1, _exitRequests);
    }

    [Fact]
    public async Task A_download_changed_on_disk_is_not_installed()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create();
        await service.CheckNowAsync(false);
        Assert.Equal(UpdateState.Ready, service.Status.State);

        // Same size, other bytes: a swap between the check and the install.
        var next = ExePath + ".new";
        var bytes = File.ReadAllBytes(next);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(next, bytes);

        service.InstallNow();

        Assert.Empty(_replaces);
        Assert.Equal(0, _exitRequests);
        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.False(File.Exists(next));
    }

    [Fact]
    public async Task A_failed_swap_sets_Error_and_does_not_exit()
    {
        _feed.Release(Manifest("2.0.0"));
        using var service = Create(failReplace: true);
        await service.CheckNowAsync(false);

        service.InstallNow();

        Assert.Equal(UpdateState.Error, service.Status.State);
        Assert.Equal(0, _exitRequests);
    }

    [Fact]
    public void AutoInstall_defaults_on_and_persists_to_the_given_path()
    {
        using (var first = Create())
        {
            Assert.True(first.AutoInstall);
            first.AutoInstall = false;
        }

        Assert.Contains("false", File.ReadAllText(SettingsPath));
        using var second = Create();
        Assert.False(second.AutoInstall);
        Assert.False(second.Status.AutoInstall);
    }

    [Fact]
    public void A_broken_settings_file_falls_back_to_on()
    {
        File.WriteAllText(SettingsPath, "{ nope");
        using var service = Create();
        Assert.True(service.AutoInstall);
    }

    [Fact]
    public async Task Changed_is_posted_to_the_ui_context()
    {
        _feed.Release(Manifest("1.0.16"));
        var ui = new QueueContext();
        using var service = Create(ui: ui);

        await service.CheckNowAsync(false);

        Assert.Empty(_changes);
        ui.Drain();
        Assert.Equal(new[] { UpdateState.Checking, UpdateState.UpToDate }, _changes.Select(c => c.State));
    }

    [Fact]
    public async Task No_Changed_after_Dispose()
    {
        _feed.Release(Manifest("2.0.0"));
        var ui = new QueueContext();
        var service = Create(ui: ui);
        await service.CheckNowAsync(false);

        service.Dispose();
        ui.Drain();

        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Dev_profile_does_nothing()
    {
        _feed.Release(Manifest("2.0.0"));
        var http = new HttpClient(_feed);
        using var service = new UpdateService(
            new UpdateClient(http),
            () => true,
            () => _exitRequests++,
            null,
            RunProfile.Dev,
            new UpdateServiceOptions
            {
                Current = Current,
                ExePath = ExePath,
                SkipPath = SkipPath,
                SettingsPath = SettingsPath,
                Timers = _timers,
                BeginReplace = (e, n, a) => _replaces.Add((e, n, a)),
                RestartArguments = () => "",
            });
        service.Changed += _changes.Add;

        service.Start();
        await service.CheckNowAsync(true);
        await _timers.AdvanceAsync(TimeSpan.FromHours(4));
        service.AutoInstall = false;
        service.InstallNow();
        service.Skip();

        Assert.Equal(0, _feed.TotalCalls);
        Assert.Equal(0, _timers.Scheduled);
        Assert.Empty(_replaces);
        Assert.Equal(0, _exitRequests);
        Assert.False(File.Exists(SettingsPath));
        Assert.False(File.Exists(ExePath + ".new"));
        Assert.Equal(UpdateState.Idle, service.Status.State);
    }

    [Fact]
    public async Task Dev_public_constructor_makes_no_calls()
    {
        var feed = new FakeFeed();
        using var service = new UpdateService(new UpdateClient(new HttpClient(feed)), () => true, () => { }, null, RunProfile.Dev);

        service.Start();
        await service.CheckNowAsync(true);

        Assert.Equal(0, feed.TotalCalls);
        Assert.Equal(UpdateState.Idle, service.Status.State);
    }

    [Fact]
    public async Task Dev_with_updates_on_uses_devtest_swaps_and_never_writes_settings()
    {
        _feed.Release(Manifest("2.0.0", url: DevExeUrl), channel: "devtest");
        var feed = new UpdateFeed(RunProfile.Dev, true, new Uri(FeedBase), UpdateFeed.DevChannel);
        using var service = new UpdateService(
            new UpdateClient(new HttpClient(_feed), feed),
            () => true,
            () => _exitRequests++,
            null,
            RunProfile.Dev,
            new UpdateServiceOptions
            {
                Current = Current,
                ExePath = ExePath,
                SkipPath = SkipPath,
                SettingsPath = SettingsPath,
                Timers = _timers,
                BeginReplace = (e, n, a) => _replaces.Add((e, n, a)),
                RestartArguments = () => "",
                FirstCheckDelay = TimeSpan.FromSeconds(3),
            });

        service.Start();
        await _timers.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, _feed.Calls(DevApiUrl));
        Assert.Equal(0, _feed.Calls(ApiUrl));
        Assert.Equal(UpdateState.Ready, service.Status.State);

        service.AutoInstall = false;
        service.InstallNow();

        Assert.Single(_replaces);
        Assert.Equal(1, _exitRequests);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task A_dev_client_does_not_serve_the_production_profile()
    {
        _feed.Release(Manifest("2.0.0"));
        var feed = new UpdateFeed(RunProfile.Dev, true, new Uri(FeedBase), UpdateFeed.DevChannel);
        using var client = new UpdateClient(new HttpClient(_feed), feed);

        Assert.Null(await client.CheckAsync(Current, RunProfile.Production));
        Assert.Equal(0, _feed.TotalCalls);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("601", null)]
    [InlineData("abc", null)]
    [InlineData("5", 5)]
    [InlineData(" 600 ", 600)]
    public void Dev_first_check_delay_reads_1_to_600_seconds(string? text, int? seconds) =>
        Assert.Equal(seconds == null ? null : TimeSpan.FromSeconds(seconds.Value), UpdateServiceOptions.DevFirstCheckDelay(text));

    [Theory]
    [InlineData("", "--tray")]
    [InlineData("--tray", "--tray")]
    [InlineData("--TRAY", "--TRAY")]
    [InlineData("--foo", "--tray --foo")]
    public void WithTrayArgument_adds_tray_once(string input, string expected) =>
        Assert.Equal(expected, UpdateService.WithTrayArgument(input));

    [Theory]
    [InlineData("", "")]
    [InlineData("--tray", "")]
    [InlineData("--TRAY --foo", "--foo")]
    [InlineData("--foo --tray", "--foo")]
    public void WithoutTrayArgument_removes_tray(string input, string expected) =>
        Assert.Equal(expected, UpdateService.WithoutTrayArgument(input));

    private UpdateService Create(
        SynchronizationContext? ui = null,
        string restartArguments = "",
        bool failReplace = false)
    {
        var service = new UpdateService(
            new UpdateClient(new HttpClient(_feed), StableFeed),
            () => _canAutoInstall,
            () => _exitRequests++,
            ui,
            RunProfile.Production,
            new UpdateServiceOptions
            {
                Current = Current,
                ExePath = ExePath,
                SkipPath = SkipPath,
                SettingsPath = SettingsPath,
                Timers = _timers,
                BeginReplace = (exe, next, args) =>
                {
                    if (failReplace)
                        throw new FileNotFoundException("The downloaded update is missing.");
                    _replaces.Add((exe, next, args));
                },
                RestartArguments = () => restartArguments,
            });
        service.Changed += _changes.Add;
        return service;
    }

    private static readonly UpdateFeed StableFeed = new(RunProfile.Production, true, new Uri(FeedBase), UpdateFeed.StableChannel);

    internal static string Manifest(string version, string? sha256 = null, string? notes = null, long? size = null, string? url = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version,
            sha256 = sha256 ?? ExeHash,
            size = size ?? Exe.Length,
            notes = notes ?? "",
            publishedAt = "2026-11-20T12:00:00.000Z",
            url = url ?? ExeUrl,
        });

    private static byte[] FakeExe()
    {
        var bytes = new byte[1_200_000];
        new Random(42).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static List<T> Distinct<T>(IEnumerable<T> items)
    {
        var list = new List<T>();
        foreach (var item in items)
        {
            if (list.Count == 0 || !EqualityComparer<T>.Default.Equals(list[^1], item))
                list.Add(item);
        }

        return list;
    }

    private sealed class FakeFeed : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _calls = new();
        private readonly Dictionary<string, string> _releases = new();

        public bool FailApi { get; set; }

        public bool NoRelease { get; set; }

        public bool NotFound { get; set; }

        public bool FailExe { get; set; }

        public int TotalCalls { get; private set; }

        public void Release(string json, string channel = "stable") => _releases[channel] = json;

        public int Calls(string url)
        {
            lock (_calls)
                return _calls.TryGetValue(url, out var count) ? count : 0;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_calls)
            {
                TotalCalls++;
                _calls[url] = (_calls.TryGetValue(url, out var count) ? count : 0) + 1;
            }

            if (url == ApiUrl || url == DevApiUrl)
            {
                if (FailApi)
                    throw new HttpRequestException("No such host is known.");
                if (NoRelease)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no_release\"}") });
                if (NotFound)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>gone</html>") });
                var channel = url == ApiUrl ? "stable" : "devtest";
                return Task.FromResult(Ok(new StringContent(_releases.TryGetValue(channel, out var json) ? json : "{}")));
            }

            if (url == ExeUrl || url == DevExeUrl)
            {
                if (FailExe)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
                return Task.FromResult(Ok(new ByteArrayContent(Exe)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeTimers : IUpdateTimers
    {
        private readonly List<Entry> _entries = new();

        public DateTime UtcNow { get; private set; } = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        public int Scheduled => _entries.Count;

        public IDisposable Schedule(TimeSpan dueTime, TimeSpan period, Func<Task> tick)
        {
            var entry = new Entry(UtcNow + dueTime, period, tick);
            _entries.Add(entry);
            return entry;
        }

        /// <summary>Moves the clock without firing ticks. Each timer fires once when time moves next.</summary>
        public void Sleep(TimeSpan by)
        {
            UtcNow += by;
            foreach (var entry in _entries)
            {
                if (entry.Next < UtcNow)
                    entry.Next = UtcNow;
            }
        }

        /// <summary>Moves time forward and runs every due tick in order, awaiting each one.</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            var end = UtcNow + by;
            while (true)
            {
                var next = _entries
                    .Where(e => !e.Stopped && e.Next <= end)
                    .OrderBy(e => e.Next)
                    .FirstOrDefault();
                if (next == null)
                    break;

                UtcNow = next.Next;
                if (next.Period == Timeout.InfiniteTimeSpan)
                    next.Stopped = true;
                else
                    next.Next += next.Period;
                await next.Tick();
            }

            UtcNow = end;
        }

        private sealed class Entry : IDisposable
        {
            public Entry(DateTime next, TimeSpan period, Func<Task> tick)
            {
                Next = next;
                Period = period;
                Tick = tick;
            }

            public DateTime Next { get; set; }
            public TimeSpan Period { get; }
            public Func<Task> Tick { get; }
            public bool Stopped { get; set; }

            public void Dispose() => Stopped = true;
        }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
                _queue.Enqueue((d, state));
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                        return;
                    item = _queue.Dequeue();
                }

                item.Callback(item.State);
            }
        }
    }
}
