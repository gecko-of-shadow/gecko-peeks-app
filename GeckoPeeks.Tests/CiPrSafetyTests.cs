using Xunit;

namespace GeckoPeeks.Tests;

public class CiPrSafetyTests
{
    [Fact]
    public void Pull_request_does_not_run_the_production_smoke()
    {
        var ci = ReadRepoFile(".github", "workflows", "ci.yml");
        var smoke = Between(ci, "\n  smoke:\n", "\n  smoke-dev:\n");

        Assert.Contains("needs: test", smoke, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name != 'pull_request'", smoke, StringComparison.Ordinal);
        Assert.Contains("smoke-exe.ps1", smoke, StringComparison.Ordinal);
        Assert.Contains("dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("GeckoPeeksDev", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_request_smoke_publishes_and_names_only_the_dev_exe()
    {
        var ci = ReadRepoFile(".github", "workflows", "ci.yml");
        var dev = FromMarker(ci, "\n  smoke-dev:\n");

        Assert.Contains("needs: test", dev, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name == 'pull_request'", dev, StringComparison.Ordinal);
        Assert.Contains("-p:GeckoPeeksDev=true", dev, StringComparison.Ordinal);
        Assert.Contains("GeckoPeeksDev.exe", dev, StringComparison.Ordinal);
        Assert.Contains("smoke-dev.ps1", dev, StringComparison.Ordinal);
        Assert.DoesNotContain("smoke-exe.ps1", dev, StringComparison.Ordinal);
        Assert.Contains("runs-on: windows-latest", dev, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_job_still_runs_for_every_ci_event()
    {
        var ci = ReadRepoFile(".github", "workflows", "ci.yml");
        var test = Between(ci, "\n  test:\n", "\n  smoke:\n");

        Assert.DoesNotContain("if:", test, StringComparison.Ordinal);
        Assert.Contains("run: dotnet test -c Release", test, StringComparison.Ordinal);
        Assert.Contains("pull_request:", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Dev_smoke_script_cannot_rewrite_live_identity()
    {
        var script = ReadRepoFile(".github", "scripts", "smoke-dev.ps1");

        Assert.Contains("GeckoPeeksDev.exe", script, StringComparison.Ordinal);
        Assert.Contains("GECKO_PEEKS_DEV_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("GITHUB_ACTIONS", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-ItemProperty", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-ItemProperty", script, StringComparison.Ordinal);
        Assert.DoesNotContain("New-ItemProperty", script, StringComparison.Ordinal);
        Assert.DoesNotContain("icacls", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Process -Name", script, StringComparison.Ordinal);
        Assert.DoesNotContain("smoke-exe.ps1", script, StringComparison.Ordinal);
        Assert.Contains("ProcessName -eq \"GeckoPeeksDev\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_workflow_is_not_a_pull_request_path()
    {
        var release = ReadRepoFile(".github", "workflows", "release.yml");

        Assert.Contains("tags:", release, StringComparison.Ordinal);
        Assert.Contains("- \"v*\"", release, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", release, StringComparison.Ordinal);
        Assert.DoesNotContain("GeckoPeeksDev", release, StringComparison.Ordinal);
        Assert.DoesNotContain("smoke-dev.ps1", release, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_workflow_publishes_only_the_stable_channel_with_the_secret()
    {
        var release = ReadRepoFile(".github", "workflows", "release.yml");
        var step = FromMarker(release, "      - name: Publish to the stable update channel\n");

        Assert.Contains("GECKO_PUBLISH_KEY: ${{ secrets.GECKO_PUBLISH_KEY }}", step, StringComparison.Ordinal);
        Assert.Contains("./cloud/publish-release.ps1", step, StringComparison.Ordinal);
        Assert.Contains("-Channel stable", step, StringComparison.Ordinal);
        // A manual dispatch builds an unreviewed branch, so only a tag push may promote to stable.
        Assert.Contains("if: github.ref_type == 'tag'", step, StringComparison.Ordinal);
        Assert.Contains("-BaseUrl \"https://gecko-peeks-api.gecko-apps.workers.dev\"", step, StringComparison.Ordinal);
        Assert.DoesNotContain("devtest", release, StringComparison.Ordinal);
        Assert.True(
            release.IndexOf("- name: Smoke-run GeckoPeeks.exe", StringComparison.Ordinal)
                < release.IndexOf("- name: Publish to the stable update channel", StringComparison.Ordinal),
            "The update channel must be published after the smoke run.");
    }

    [Fact]
    public void Publish_script_never_prints_the_key()
    {
        var script = ReadRepoFile("cloud", "publish-release.ps1");

        Assert.Contains("GECKO_PUBLISH_KEY", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-SecureString", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $key", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write-Output $key", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileVersion", script, StringComparison.Ordinal);
        // The app's dev feed override must never receive the publish key.
        Assert.DoesNotContain("$env:GECKO_PEEKS_UPDATE_URL", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_markers_match_when_checkout_uses_crlf()
    {
        var crlf = string.Join(
            "\r\n",
            "jobs:",
            "  test:",
            "    run: dotnet test -c Release",
            "  smoke:",
            "    if: github.event_name != 'pull_request'",
            "  smoke-dev:",
            "    if: github.event_name == 'pull_request'",
            "");
        var text = NormalizeNewlines(crlf);
        var test = Between(text, "\n  test:\n", "\n  smoke:\n");
        var smoke = Between(text, "\n  smoke:\n", "\n  smoke-dev:\n");
        var dev = FromMarker(text, "\n  smoke-dev:\n");

        Assert.Contains("run: dotnet test -c Release", test, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name != 'pull_request'", smoke, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name == 'pull_request'", dev, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0, "Missing start marker " + start);
        Assert.True(to > from, "Missing end marker " + end);
        return text.Substring(from, to - from);
    }

    private static string FromMarker(string text, string start)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Missing start marker " + start);
        return text.Substring(from);
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = dir.FullName;
            foreach (var part in parts)
                candidate = Path.Combine(candidate, part);
            if (File.Exists(candidate))
                return NormalizeNewlines(File.ReadAllText(candidate));
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join("/", parts));
    }
}
