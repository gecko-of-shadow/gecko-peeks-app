namespace GeckoPeeks.App;

public enum RunProfile
{
    Production = 0,
    Dev = 1,
}

/// <summary>
/// Production behavior is the default. A dev build sets GECKO_PEEKS_DEV and must
/// supply a scratch root outside the executable directory. Dev launches do not
/// probe, update, repair startup, or fall back to the production log folders.
/// </summary>
public static class AppIsolation
{
    public const string DevScratchVariable = "GECKO_PEEKS_DEV_ROOT";
    public const string ProductionProcessName = "GeckoPeeks";
    public const string DevProcessName = "GeckoPeeksDev";
    public const string ProductionMutexName = @"Local\GeckoPeeks.SingleInstance";
    public const string DevMutexName = @"Local\GeckoPeeks.Dev.SingleInstance";
    public const string ProductionShowEventName = @"Local\GeckoPeeks.ShowWindow";
    public const string DevShowEventName = @"Local\GeckoPeeks.Dev.ShowWindow";
    public const string DevLogFolderName = "RiotRouteLogDev";

    public static RunProfile Profile
    {
        get
        {
#if GECKO_PEEKS_DEV
            return RunProfile.Dev;
#else
            return RunProfile.Production;
#endif
        }
    }

    public static string ProcessName(RunProfile profile) => Pick(profile, ProductionProcessName, DevProcessName);

    public static string MutexName(RunProfile profile) => Pick(profile, ProductionMutexName, DevMutexName);

    public static string ShowEventName(RunProfile profile) => Pick(profile, ProductionShowEventName, DevShowEventName);

    public static bool AllowsProbes(RunProfile profile) => IsProduction(profile);

    /// <summary>Dev-only opt-in for one real probe. The CSV and settings stay in the dev root.</summary>
    public const string DevProbeVariable = "GECKO_PEEKS_DEV_PROBE";

    /// <summary>
    /// True only for a Dev build with GECKO_PEEKS_DEV_PROBE=1 and demo off. Demo always wins.
    /// Production ignores the variable: it probes anyway.
    /// </summary>
    public static bool DevProbeOn(RunProfile profile, string? value, bool demo) =>
        profile == RunProfile.Dev && !demo && string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    public static bool AllowsUpdateChecks(RunProfile profile) => IsProduction(profile);

    public static bool AllowsStartupRegistry(RunProfile profile) => IsProduction(profile);

    public static bool AllowsLogPrepare(RunProfile profile) => IsProduction(profile);

    public static bool AllowsExeSiblingCleanup(RunProfile profile) => IsProduction(profile);

    public static string? ReadDevScratchRoot()
    {
        if (Profile != RunProfile.Dev)
            return null;

        var value = Environment.GetEnvironmentVariable(DevScratchVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static LogPaths LaunchPaths(
        RunProfile profile,
        Func<LogPaths> productionPaths,
        string? devScratchRoot,
        string? exeDirectory)
    {
        switch (profile)
        {
            case RunProfile.Production:
                return productionPaths();
            case RunProfile.Dev:
                if (!TryDevLogDirectory(devScratchRoot, exeDirectory, out var directory))
                    throw new InvalidOperationException(
                        "Dev scratch root is required and must sit outside the executable directory.");
                return new LogPaths(directory, fallbackFolder: null);
            default:
                throw Unexpected(profile);
        }
    }

    public static string? CrashFolderOrNull(string? configured, Func<string> productionFolder, RunProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        if (!AllowsLogPrepare(profile))
            return null;
        return productionFolder();
    }

    public static bool TryDevLogDirectory(string? scratchRoot, string? exeDirectory, out string directory)
    {
        directory = "";
        if (string.IsNullOrWhiteSpace(scratchRoot))
            return false;

        var log = Path.Combine(Path.GetFullPath(scratchRoot), DevLogFolderName);
        if (!string.IsNullOrWhiteSpace(exeDirectory) && IsInside(log, exeDirectory))
            return false;

        directory = log;
        return true;
    }

    private static bool IsProduction(RunProfile profile)
    {
        switch (profile)
        {
            case RunProfile.Production:
                return true;
            case RunProfile.Dev:
                return false;
            default:
                throw Unexpected(profile);
        }
    }

    private static string Pick(RunProfile profile, string production, string dev)
    {
        switch (profile)
        {
            case RunProfile.Production:
                return production;
            case RunProfile.Dev:
                return dev;
            default:
                throw Unexpected(profile);
        }
    }

    private static Exception Unexpected(RunProfile profile)
    {
        var unexpected = profile;
        return new ArgumentOutOfRangeException(nameof(profile), unexpected, "Unexpected run profile.");
    }

    private static bool IsInside(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        if (fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
