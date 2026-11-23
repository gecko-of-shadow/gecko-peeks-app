using System.Net;
using System.Net.Http;
using GeckoPeeks.App;
using Xunit;

namespace GeckoPeeks.Tests;

public class DevIsolationTests
{
    [Fact]
    public void This_compilation_is_the_production_profile()
    {
        Assert.Equal(RunProfile.Production, AppIsolation.Profile);
        Assert.Equal("GeckoPeeks", AppIsolation.ProcessName(RunProfile.Production));
        Assert.Equal("GeckoPeeksDev", AppIsolation.ProcessName(RunProfile.Dev));
        Assert.Equal(@"Local\GeckoPeeks.SingleInstance", AppIsolation.MutexName(RunProfile.Production));
        Assert.Equal(@"Local\GeckoPeeks.Dev.SingleInstance", AppIsolation.MutexName(RunProfile.Dev));
        Assert.Equal(@"Local\GeckoPeeks.ShowWindow", AppIsolation.ShowEventName(RunProfile.Production));
        Assert.Equal(@"Local\GeckoPeeks.Dev.ShowWindow", AppIsolation.ShowEventName(RunProfile.Dev));
        Assert.Equal("GeckoPeeks", StartupRegistration.ValueName);
        Assert.NotEqual(AppIsolation.MutexName(RunProfile.Production), AppIsolation.MutexName(RunProfile.Dev));
        Assert.NotEqual(AppIsolation.ShowEventName(RunProfile.Production), AppIsolation.ShowEventName(RunProfile.Dev));
    }

    [Theory]
    [InlineData(RunProfile.Production, true)]
    [InlineData(RunProfile.Dev, false)]
    public void Background_work_is_production_only(RunProfile profile, bool allowed)
    {
        Assert.Equal(allowed, AppIsolation.AllowsProbes(profile));
        Assert.Equal(allowed, AppIsolation.AllowsUpdateChecks(profile));
        Assert.Equal(allowed, AppIsolation.AllowsStartupRegistry(profile));
        Assert.Equal(allowed, AppIsolation.AllowsLogPrepare(profile));
        Assert.Equal(allowed, AppIsolation.AllowsExeSiblingCleanup(profile));
    }

    [Fact]
    public void Production_launch_paths_ignore_dev_scratch_and_keep_fallback()
    {
        var documents = Path.Combine("synthetic", "Documents");
        var local = Path.Combine("synthetic", "Local");
        var paths = AppIsolation.LaunchPaths(
            RunProfile.Production,
            () => LogPaths.For(documents, local),
            devScratchRoot: Path.Combine("dev-scratch"),
            exeDirectory: Path.Combine("installed"));

        Assert.Equal(Path.Combine(documents, "RiotRouteLog"), paths.Folder);
        Assert.Equal(Path.Combine(local, "GeckoPeeks", "RiotRouteLog"), paths.FallbackFolder);
    }

    [Fact]
    public void Dev_launch_uses_only_the_scratch_directory()
    {
        var root = ScratchRoot();
        var exe = Path.Combine(ScratchRoot(), "app");
        var called = false;
        var paths = AppIsolation.LaunchPaths(
            RunProfile.Dev,
            () =>
            {
                called = true;
                return LogPaths.For(Path.Combine("synthetic", "Documents"), Path.Combine("synthetic", "Local"));
            },
            root,
            exe);

        Assert.False(called);
        Assert.Null(paths.FallbackFolder);
        Assert.Equal("RiotRouteLogDev", Path.GetFileName(paths.Folder));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "RiotRouteLogDev")), Path.GetFullPath(paths.Folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Dev_without_scratch_does_not_call_production_paths(string? scratch)
    {
        var called = false;
        var error = Assert.Throws<InvalidOperationException>(() => AppIsolation.LaunchPaths(
            RunProfile.Dev,
            () =>
            {
                called = true;
                return LogPaths.For(Path.Combine("synthetic", "Documents"), Path.Combine("synthetic", "Local"));
            },
            scratch,
            exeDirectory: ScratchRoot()));

        Assert.False(called);
        Assert.Contains("scratch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dev_scratch_inside_the_exe_directory_is_rejected()
    {
        var exe = ScratchRoot();
        var scratch = Path.Combine(exe, "inside");
        var called = false;
        Assert.Throws<InvalidOperationException>(() => AppIsolation.LaunchPaths(
            RunProfile.Dev,
            () =>
            {
                called = true;
                return new LogPaths("unused");
            },
            scratch,
            exe));
        Assert.False(called);
    }

    [Fact]
    public void Crash_log_does_not_fall_back_to_production_for_dev()
    {
        var called = false;
        Assert.Null(AppIsolation.CrashFolderOrNull(
            configured: null,
            () =>
            {
                called = true;
                return Path.Combine("synthetic", "Documents", "RiotRouteLog");
            },
            RunProfile.Dev));
        Assert.False(called);

        Assert.Equal(
            Path.Combine("synthetic", "Documents", "RiotRouteLog"),
            AppIsolation.CrashFolderOrNull(null, () => Path.Combine("synthetic", "Documents", "RiotRouteLog"), RunProfile.Production));
        Assert.Equal(
            "dev-folder",
            AppIsolation.CrashFolderOrNull("dev-folder", () => throw new InvalidOperationException("production"), RunProfile.Dev));
    }

    [Fact]
    public void Production_profile_ignores_dev_scratch_variable()
    {
        var name = AppIsolation.DevScratchVariable;
        Assert.Equal("GECKO_PEEKS_DEV_ROOT", name);
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, ScratchRoot());
            Assert.Null(AppIsolation.ReadDevScratchRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void Dev_startup_methods_do_not_require_windows()
    {
        StartupRegistration.SetEnabled(true, RunProfile.Dev);
        StartupRegistration.RepairIfStale(RunProfile.Dev);
    }

    [Fact]
    public void Production_startup_write_still_requires_windows()
    {
        var writes = 0;
        void Record(bool enabled)
        {
            writes++;
            Assert.True(enabled);
        }

        if (OperatingSystem.IsWindows())
        {
            StartupRegistration.ApplyStartupChange(true, RunProfile.Production, Record);
            Assert.Equal(1, writes);
            return;
        }

        var error = Assert.Throws<PlatformNotSupportedException>(() =>
            StartupRegistration.ApplyStartupChange(true, RunProfile.Production, Record));
        Assert.Contains("Windows", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Dev_skip_file_is_not_written_or_read()
    {
        var path = Path.Combine(ScratchRoot(), "skipped-update.txt");
        UpdateSkip.Skip(new Version(9, 9, 9), RunProfile.Dev, path);
        Assert.False(File.Exists(path));
        Assert.False(UpdateSkip.IsSkipped(new Version(9, 9, 9), RunProfile.Dev, path));
        UpdateSkip.Clear(RunProfile.Dev, path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Production_skip_file_still_records_the_version()
    {
        var path = Path.Combine(ScratchRoot(), "skipped-update.txt");
        try
        {
            UpdateSkip.Skip(new Version(9, 9, 9), RunProfile.Production, path);
            Assert.True(UpdateSkip.IsSkipped(new Version(9, 9, 9), RunProfile.Production, path));
            UpdateSkip.Clear(RunProfile.Production, path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Dev_update_check_and_download_do_not_send_or_write()
    {
        var handler = new CountingHandler();
        using var client = new UpdateClient(new HttpClient(handler));
        var exe = Path.Combine(ScratchRoot(), "GeckoPeeks.exe");
        var offer = new UpdateOffer(new Version(9, 9, 9), "http://127.0.0.1/fake.exe", 0, new string('a', 64));

        Assert.Null(await client.CheckAsync(new Version(1, 0, 16), RunProfile.Dev));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(offer, exe, RunProfile.Dev));

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(exe + ".new.partial"));
    }

    [Fact]
    public void Dev_update_variables_do_not_turn_on_dev_updates_in_this_compilation()
    {
        var previous = Environment.GetEnvironmentVariable(UpdateFeed.DevUpdatesVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateFeed.DevUpdatesVariable, "1");
            Assert.False(UpdateFeed.IsEnabled(RunProfile.Dev));
            Assert.Null(UpdateServiceOptions.ForProfile(RunProfile.Dev).ExePath);
            Assert.Null(UpdateServiceOptions.ForProfile(RunProfile.Dev).SettingsPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateFeed.DevUpdatesVariable, previous);
        }
    }

    [Fact]
    public void Dev_replace_does_not_write_a_script_or_start_a_swap()
    {
        var folder = ScratchRoot();
        var exe = Path.Combine(folder, "GeckoPeeks.exe");
        var script = Path.Combine(Path.GetTempPath(), "gecko-peeks-update-" + Environment.ProcessId + ".cmd");
        var scriptBefore = File.Exists(script);

        UpdateInstaller.BeginReplace(exe, exe + ".new", "", RunProfile.Dev);

        Assert.False(File.Exists(exe + ".new"));
        Assert.Equal(scriptBefore, File.Exists(script));
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    [Fact]
    public void Dev_cleanup_leaves_exe_siblings_in_place()
    {
        var folder = ScratchRoot();
        var exe = Path.Combine(folder, "GeckoPeeks.exe");
        var old = exe + ".old";
        File.WriteAllText(old, "keep");

        AppUpdate.CleanupLeftovers(exe, RunProfile.Dev);

        Assert.Equal("keep", File.ReadAllText(old));
    }

    [Fact]
    public void Production_cleanup_still_removes_an_old_sibling()
    {
        var folder = ScratchRoot();
        var exe = Path.Combine(folder, "GeckoPeeks.exe");
        var old = exe + ".old";
        File.WriteAllText(old, "old");

        AppUpdate.CleanupLeftovers(exe, RunProfile.Production);

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void App_project_keeps_production_identity_unless_dev_property_is_set()
    {
        var csproj = File.ReadAllText(AppProjectPath());
        Assert.Contains("<Version>2.2.3</Version>", csproj, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>GeckoPeeks</AssemblyName>", csproj, StringComparison.Ordinal);
        Assert.Contains(
            "<AssemblyName Condition=\"'$(GeckoPeeksDev)' == 'true'\">GeckoPeeksDev</AssemblyName>",
            csproj,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DefineConstants Condition=\"'$(GeckoPeeksDev)' == 'true'\">$(DefineConstants);GECKO_PEEKS_DEV</DefineConstants>",
            csproj,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<AssemblyName>GeckoPeeksDev</AssemblyName>", csproj, StringComparison.Ordinal);
    }

    private static string ScratchRoot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string AppProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "GeckoPeeks", "GeckoPeeks.csproj");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("GeckoPeeks.csproj");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
