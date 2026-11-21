using System.Text.Json;
using GeckoPeeks.App;

namespace GeckoPeeks.Ui.State;

/// <summary>
/// Window settings that are not stored elsewhere. Today that is only "cloud data on/off".
/// Production keeps them in %LOCALAPPDATA%\GeckoPeeks\ui-settings.json, Dev in the dev root.
/// Start with Windows stays in the registry and auto-install stays with the updater.
/// </summary>
public sealed class HostSettings
{
    public const string FileName = "ui-settings.json";

    private readonly string? _path;
    private readonly object _lock = new();
    private bool _cloudEnabled = true;

    /// <param name="path">Null keeps the settings in memory only.</param>
    public HostSettings(string? path)
    {
        _path = path;
        _cloudEnabled = Load(path);
    }

    public string? Path => _path;

    public bool CloudEnabled
    {
        get
        {
            lock (_lock)
                return _cloudEnabled;
        }
        set
        {
            lock (_lock)
            {
                if (_cloudEnabled == value)
                    return;
                _cloudEnabled = value;
            }

            Save(_path, value);
        }
    }

    public static string? PathFor(RunProfile profile, string? devRoot)
    {
        if (profile == RunProfile.Production)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local) ? null : System.IO.Path.Combine(local, "GeckoPeeks", FileName);
        }

        return string.IsNullOrWhiteSpace(devRoot) ? null : System.IO.Path.Combine(devRoot, FileName);
    }

    /// <summary>A missing or broken file means cloud data is on.</summary>
    internal static bool Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        try
        {
            if (!File.Exists(path))
                return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("cloudEnabled", out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }
        catch
        {
            // Unreadable settings fall back to the default.
        }

        return true;
    }

    private static void Save(string? path, bool cloudEnabled)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { cloudEnabled }));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // The choice still holds for this run.
        }
    }
}
