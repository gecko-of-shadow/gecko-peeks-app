using System.Text.Json;

namespace GeckoPeeks.Ui.State;

public enum BridgeCommand
{
    Ready,
    RunNow,
    TogglePause,
    CopyDiagnosis,
    OpenLog,
    OpenFolder,
    CopyPath,
    CheckUpdate,
    InstallUpdate,
    RefreshCloud,
    OpenUrl,
    SetSetting,
    SetRegion,
}

public enum BridgeSetting
{
    StartWithWindows,
    AutoInstallUpdates,
    CloudEnabled,
}

/// <summary>One web-to-host message from Contract 2.</summary>
/// <param name="RegionId">For setRegion: "auto" or a lowercase region id.</param>
public sealed record BridgeMessage(BridgeCommand Command, string? Url = null, BridgeSetting? Setting = null, bool Value = false, string? RegionId = null)
{
    /// <summary>The longest region id setRegion accepts ("auto" or for example "euw1").</summary>
    public const int MaxRegionIdLength = 8;

    /// <summary>Messages longer than this are dropped. The longest real one is an openUrl.</summary>
    public const int MaxLength = 8192;

    /// <summary>
    /// Reads the JSON the page posted. Unknown commands, wrong types and broken JSON give null,
    /// so the host ignores them.
    /// </summary>
    public static BridgeMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxLength)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cmd", out var cmd)
                || cmd.ValueKind != JsonValueKind.String)
                return null;

            switch (cmd.GetString())
            {
                case "ready": return new BridgeMessage(BridgeCommand.Ready);
                case "runNow": return new BridgeMessage(BridgeCommand.RunNow);
                case "togglePause": return new BridgeMessage(BridgeCommand.TogglePause);
                case "copyDiagnosis": return new BridgeMessage(BridgeCommand.CopyDiagnosis);
                case "openLog": return new BridgeMessage(BridgeCommand.OpenLog);
                case "openFolder": return new BridgeMessage(BridgeCommand.OpenFolder);
                case "copyPath": return new BridgeMessage(BridgeCommand.CopyPath);
                case "checkUpdate": return new BridgeMessage(BridgeCommand.CheckUpdate);
                case "installUpdate": return new BridgeMessage(BridgeCommand.InstallUpdate);
                case "refreshCloud": return new BridgeMessage(BridgeCommand.RefreshCloud);
                case "openUrl":
                    return root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                        ? new BridgeMessage(BridgeCommand.OpenUrl, Url: url.GetString())
                        : null;
                case "setSetting":
                    return ParseSetting(root);
                case "setRegion":
                    return ParseRegion(root);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>id must be "auto" or match ^[a-z]{2,3}\d?$ after lowercasing, at most 8 chars.</summary>
    private static BridgeMessage? ParseRegion(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            return null;
        var id = idElement.GetString();
        if (id == null || id.Length == 0 || id.Length > MaxRegionIdLength)
            return null;
        id = id.ToLowerInvariant();
        return IsRegionId(id) ? new BridgeMessage(BridgeCommand.SetRegion, RegionId: id) : null;
    }

    private static bool IsRegionId(string id)
    {
        if (id == "auto")
            return true;
        var letters = 0;
        while (letters < id.Length && id[letters] is >= 'a' and <= 'z')
            letters++;
        if (letters is < 2 or > 3)
            return false;
        var rest = id.Length - letters;
        return rest == 0 || (rest == 1 && id[letters] is >= '0' and <= '9');
    }

    private static BridgeMessage? ParseSetting(JsonElement root)
    {
        if (!root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
            return null;
        if (!root.TryGetProperty("value", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        BridgeSetting? setting = key.GetString() switch
        {
            "startWithWindows" => BridgeSetting.StartWithWindows,
            "autoInstallUpdates" => BridgeSetting.AutoInstallUpdates,
            "cloudEnabled" => BridgeSetting.CloudEnabled,
            _ => null,
        };
        return setting == null ? null : new BridgeMessage(BridgeCommand.SetSetting, Setting: setting, Value: value.GetBoolean());
    }
}

/// <summary>
/// The only links the host opens in the default browser: Cloudflare Radar pages and this
/// project's GitHub pages. https only, exact hosts, default port, no user info.
/// </summary>
public static class UrlPolicy
{
    public const string RadarHost = "radar.cloudflare.com";
    public const string GitHubHost = "github.com";
    public const string RepoPath = "/" + GeckoPeeks.App.AppUpdate.Owner + "/" + GeckoPeeks.App.AppUpdate.Repo;

    public static bool TryAllow(string? url, out Uri allowed)
    {
        allowed = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return false;

        var host = uri.IdnHost;
        if (string.Equals(host, RadarHost, StringComparison.OrdinalIgnoreCase))
        {
            allowed = uri;
            return true;
        }

        if (!string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.AbsolutePath;
        var inRepo = path.Equals(RepoPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(RepoPath + "/", StringComparison.OrdinalIgnoreCase);
        if (!inRepo)
            return false;

        allowed = uri;
        return true;
    }
}
