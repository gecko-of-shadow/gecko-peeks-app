namespace GeckoPeeks.App;

/// <summary>
/// Remembers a version the user skipped ("Not this version") so the auto-check does not nag
/// again. Lives under LocalAppData so Controlled folder access on Documents does not block it.
/// </summary>
public static class UpdateSkip
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeckoPeeks",
        "skipped-update.txt");

    public static bool IsSkipped(Version version) => IsSkipped(version, AppIsolation.Profile, FilePath);

    public static bool IsSkipped(Version version, RunProfile profile, string path)
    {
        if (!UpdateFeed.IsEnabled(profile))
            return false;

        try
        {
            if (!File.Exists(path))
                return false;
            var text = File.ReadAllText(path).Trim();
            var skipped = AppUpdate.ParseTag(text);
            return skipped != null && AppUpdate.Compare(skipped, version) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Skip(Version version) => Skip(version, AppIsolation.Profile, FilePath);

    public static void Skip(Version version, RunProfile profile, string path)
    {
        if (!UpdateFeed.IsEnabled(profile))
            return;

        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(path, AppUpdate.FormatVersion(version));
        }
        catch
        {
            // Skipping is best-effort; the tray Install item still works.
        }
    }

    public static void Clear() => Clear(AppIsolation.Profile, FilePath);

    public static void Clear(RunProfile profile, string path)
    {
        if (!UpdateFeed.IsEnabled(profile))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }
}
