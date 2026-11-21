using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using GeckoPeeks.App;
using GeckoPeeks.Logging;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// Tests added to prove bugs found in the 2026-10-21 review. They assert the
/// behaviour that should hold; a failure here is evidence, not a flaky suite.
/// </summary>
public class UpdateCheckErrorTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_throws_on_http_error_instead_of_reporting_latest(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler(new HttpResponseMessage(status)));
        using var client = new UpdateClient(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CheckAsync(new Version(1, 0, 15)));

        Assert.Contains(((int)status).ToString(), error.Message);
    }

    [Fact]
    public void PickOffer_rejects_a_foreign_url_and_a_zero_size()
    {
        var feed = new Uri("https://gecko-peeks-api.gecko-apps.workers.dev/");
        var hash = new string('c', 64);
        var foreign = "{\"version\":\"9.9.9\",\"sha256\":\"" + hash + "\",\"size\":5000000,\"url\":\"http://127.0.0.1/not-from-the-feed.exe\"}";
        var zero = "{\"version\":\"9.9.9\",\"sha256\":\"" + hash + "\",\"size\":0,\"url\":\"https://gecko-peeks-api.gecko-apps.workers.dev/v1/update/download/stable/9.9.9\"}";

        Assert.Throws<InvalidDataException>(() => AppUpdate.PickOffer(foreign, new Version(1, 0, 15), feed));
        Assert.Throws<InvalidDataException>(() => AppUpdate.PickOffer(zero, new Version(1, 0, 15), feed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    public async Task DownloadAsync_without_a_valid_sha256_downloads_nothing(string? sha256)
    {
        var folder = TempFolder();
        try
        {
            var exe = Path.Combine(folder, "GeckoPeeks.exe");
            var handler = new CountingStub(FakeMz());
            using var http = new HttpClient(handler);
            using var client = new UpdateClient(http);
            var offer = new UpdateOffer(new Version(9, 9, 9), "https://feed.test/fake.exe", 0, sha256!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(offer, exe));

            Assert.Equal(0, handler.Calls);
            Assert.False(File.Exists(exe + ".new"));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public async Task DownloadAsync_installs_an_mz_file_whose_sha256_matches()
    {
        var fake = FakeMz();
        var folder = TempFolder();
        try
        {
            var exe = Path.Combine(folder, "GeckoPeeks.exe");
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });
            using var http = new HttpClient(new CountingStub(fake));
            using var client = new UpdateClient(http);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fake)).ToLowerInvariant();
            var offer = new UpdateOffer(new Version(9, 9, 9), "https://feed.test/fake.exe", fake.Length, hash);

            var downloaded = await client.DownloadAsync(offer, exe);

            Assert.Equal(exe + ".new", downloaded);
            Assert.Equal(fake.Length, new FileInfo(downloaded).Length);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static byte[] FakeMz()
    {
        var fake = new byte[1_000_001];
        fake[0] = (byte)'M';
        fake[1] = (byte)'Z';
        return fake;
    }

    private sealed class CountingStub : HttpMessageHandler
    {
        private readonly byte[] _body;

        public CountingStub(byte[] body) => _body = body;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Temp only.
        }
    }
}

public class UpdateInstallerEncodingTests
{
    [Fact]
    public void Swap_script_keeps_a_non_ascii_path_and_restarts_the_old_exe_if_the_move_fails()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "Søren", "GeckoPeeks.exe");
            var script = Path.Combine(folder, "swap.cmd");
            UpdateInstaller.WriteSwapScript(script, 42, exe, exe + ".new", "--tray");

            // UTF-8 without a BOM, and the code page switch comes before the first path.
            var bytes = File.ReadAllBytes(script);
            Assert.Equal((byte)'@', bytes[0]);

            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains(exe, text);
            Assert.DoesNotContain("S?ren", text);
            Assert.True(
                text.IndexOf("chcp 65001", StringComparison.Ordinal) < text.IndexOf("set \"EXE=", StringComparison.Ordinal),
                "chcp 65001 must come before the paths");

            var failure = text[text.IndexOf("if errorlevel 1 (", StringComparison.Ordinal)..];
            var branch = failure[..failure.IndexOf("exit /b 1", StringComparison.Ordinal)];
            Assert.Contains("start \"\" \"%EXE%\" %ARGS%", branch, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    /// <summary>
    /// Runs the real script header in cmd.exe. The v1.0.16 UTF-16 script never ran: cmd read
    /// its BOM as a command. The tail is cut before the wait loop, so nothing is moved or started.
    /// </summary>
    [Fact]
    public void Cmd_runs_the_swap_script_and_reads_a_non_ascii_path()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"), "Søren");
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "GeckoPeeks.exe");
            File.WriteAllText(exe + ".new", "new");
            var script = Path.Combine(folder, "swap.cmd");
            var result = Path.Combine(folder, "result.txt");
            UpdateInstaller.WriteSwapScript(script, 42, exe, exe + ".new", "");

            var text = File.ReadAllText(script, UpdateInstaller.ScriptEncoding);
            text = text[..text.IndexOf(":wait", StringComparison.Ordinal)]
                + "if exist \"%NEW%\" (echo found>\"" + result + "\")\r\n";
            File.WriteAllText(script, text, UpdateInstaller.ScriptEncoding);

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/c \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(15000));

            Assert.True(File.Exists(result), "cmd.exe did not run the script or could not read the Søren path");
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    [Fact]
    public void Quote_leaves_cmd_metacharacters_unquoted()
    {
        var quote = typeof(UpdateInstaller).GetMethod("Quote", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(quote);

        var ampersand = (string)quote!.Invoke(null, new object[] { "payload&calc" })!;
        var pipe = (string)quote.Invoke(null, new object[] { "a|b" })!;

        Assert.Equal("payload&calc", ampersand);
        Assert.Equal("a|b", pipe);
        Assert.DoesNotContain('"', ampersand);
    }
}

public class SettingsStoreRaceTests
{
    [Fact]
    public async Task Concurrent_saves_leave_one_complete_settings_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "gecko-peeks-settings.json");
        try
        {
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            {
                try
                {
                    SettingsStore.Save(path, new AppSettings
                    {
                        Paused = i % 2 == 0,
                        Last = new SavedResult { Upstream = "Zayo" + i, Timestamp = "2026-10-11 12:00:00" },
                    });
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            var loaded = SettingsStore.Load(path);
            Assert.NotNull(loaded.Last);
            Assert.StartsWith("Zayo", loaded.Last!.Upstream);
            var n = int.Parse(loaded.Last.Upstream["Zayo".Length..]);
            Assert.Equal(n % 2 == 0, loaded.Paused);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    [Fact]
    public void Stale_snapshot_save_does_not_clear_a_newer_pause()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "gecko-peeks-settings.json");
        try
        {
            // A finishing test copied Paused=false (revision 1). The user then paused
            // (revision 2). The stale copy is written last and must not un-pause.
            SettingsStore.Save(path, new AppSettings
            {
                Paused = false,
                Last = new SavedResult { Upstream = "Zayo", Timestamp = "2026-10-11 12:00:00" },
            }, revision: 1);
            SettingsStore.Save(path, new AppSettings
            {
                Paused = true,
                Last = new SavedResult { Upstream = "Cogent", Timestamp = "2026-10-11 12:01:00" },
            }, revision: 2);
            SettingsStore.Save(path, new AppSettings
            {
                Paused = false,
                Last = new SavedResult { Upstream = "Zayo", Timestamp = "2026-10-11 12:00:00" },
            }, revision: 1);

            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.Paused);
            Assert.Equal("Cogent", loaded.Last?.Upstream);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }
}

public class CsvShareTests
{
    [Fact]
    public void Append_keeps_writing_main_while_a_shared_reader_is_open()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));

            // Same share mode ReadAll uses (FileShare.ReadWrite). The window holds
            // this handle for the whole CSV parse when BindHistory runs.
            using var reader = new FileStream(log.MainPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var result = log.Append(Row("Cogent", "12:01:00"));

            Assert.Equal(AppendResult.Main, result);
            Assert.Equal(LogHealth.Saved, log.Health);
            Assert.False(File.Exists(log.PendingPath));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    private static RouteRow Row(string upstream, string time) => new()
    {
        Timestamp = "2026-10-11 " + time,
        LeagueRunning = "N",
        Upstream = upstream,
        RiotHopAvg = "50",
        FullPath = "1.1.1.1 > 192.207.0.5",
    };
}

public class MergeDuplicateTests
{
    [Fact]
    public void Merge_does_not_rewrite_rows_already_in_main_when_a_later_pending_row_is_combined()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));
            Assert.Equal(AppendResult.Main, log.Append(Row("Cogent", "12:01:00")));

            // Crash after those rows were merged into main, before the merging file was deleted.
            File.WriteAllText(log.MergingPath, CsvFormat.FormatLine(Row("Cogent", "12:01:00")));

            // Excel (or anything else) then locks the main CSV, so the next row lands in pending.
            using (var locked = new FileStream(log.MainPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(AppendResult.Pending, log.Append(Row("TELUS", "12:02:00")));
            }

            Assert.Equal(AppendResult.Main, log.Append(Row("HE", "12:03:00")));

            var main = File.ReadAllText(log.MainPath);
            Assert.Equal(1, CountOf(main, "Cogent"));
            Assert.Equal(1, CountOf(main, "TELUS"));
            Assert.Equal(1, CountOf(main, "HE"));
            Assert.Equal(new[] { "Zayo", "Cogent", "TELUS", "HE" }, log.ReadAll().Select(r => r.Upstream).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static RouteRow Row(string upstream, string time) => new()
    {
        Timestamp = "2026-10-11 " + time,
        LeagueRunning = "N",
        Upstream = upstream,
        RiotHopAvg = "50",
        FullPath = "1.1.1.1 > 192.207.0.5",
    };
}

public class PendingReadAllTests
{
    [Fact]
    public void ReadAll_does_not_repeat_a_merging_row_already_in_main()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));
            Assert.Equal(AppendResult.Main, log.Append(Row("Cogent", "12:01:00")));

            // Crash after the merge write succeeded but before MergingPath was deleted.
            File.WriteAllText(log.MergingPath, CsvFormat.FormatLine(Row("Cogent", "12:01:00")));

            var rows = log.ReadAll();
            Assert.Equal(new[] { "Zayo", "Cogent" }, rows.Select(r => r.Upstream).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    [Fact]
    public void ReadAll_shows_older_merging_rows_before_newer_pending_rows()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));
            File.WriteAllText(log.MergingPath, CsvFormat.FormatLine(Row("TELUS", "12:01:00")));
            File.WriteAllText(log.PendingPath, CsvFormat.FormatLine(Row("Cogent", "12:02:00")));

            var rows = log.ReadAll();
            Assert.Equal(new[] { "Zayo", "TELUS", "Cogent" }, rows.Select(r => r.Upstream).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    private static RouteRow Row(string upstream, string time) => new()
    {
        Timestamp = "2026-10-11 " + time,
        LeagueRunning = "N",
        Upstream = upstream,
        RiotHopAvg = "50",
        FullPath = "1.1.1.1 > 192.207.0.5",
    };
}
