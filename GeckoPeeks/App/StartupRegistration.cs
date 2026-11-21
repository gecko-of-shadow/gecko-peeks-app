using Microsoft.Win32;

namespace GeckoPeeks.App;

public static class StartupRegistration
{
    public const string ValueName = "GeckoPeeks";

    /// <summary>Run-key launches start hidden in the tray; a manual launch shows the window.</summary>
    public const string TrayArgument = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        if (!AppIsolation.AllowsStartupRegistry(AppIsolation.Profile))
            return false;

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled) => SetEnabled(enabled, AppIsolation.Profile);

    public static void SetEnabled(bool enabled, RunProfile profile) =>
        ApplyStartupChange(enabled, profile, WriteRunValue);

    internal static void ApplyStartupChange(bool enabled, RunProfile profile, Action<bool> write)
    {
        if (!AppIsolation.AllowsStartupRegistry(profile))
            return;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Start with Windows is only available on Windows.");

        write(enabled);
    }

    private static void WriteRunValue(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Run key could not be opened.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("The executable path is not available.");

        key.SetValue(ValueName, CommandFor(path), RegistryValueKind.String);
    }

    /// <summary>
    /// Points an existing Run value at this exe with --tray, so an upgrade saved under a new
    /// name (GeckoPeeks (1).exe) is the copy Windows starts. Does nothing when startup is off.
    /// </summary>
    public static void RepairIfStale() => RepairIfStale(AppIsolation.Profile);

    public static void RepairIfStale(RunProfile profile)
    {
        if (!AppIsolation.AllowsStartupRegistry(profile))
            return;

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var path = Environment.ProcessPath;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null || string.IsNullOrWhiteSpace(path))
                return;

            if (NeedsRepair(key.GetValue(ValueName) as string, path))
                key.SetValue(ValueName, CommandFor(path), RegistryValueKind.String);
        }
        catch
        {
            // Keep the old value; the tray toggle can still rewrite it.
        }
    }

    public static string CommandFor(string exePath) => "\"" + exePath + "\" " + TrayArgument;

    public static bool NeedsRepair(string? currentValue, string exePath) =>
        !string.IsNullOrWhiteSpace(currentValue)
        && !string.Equals(currentValue.Trim(), CommandFor(exePath), StringComparison.OrdinalIgnoreCase);
}
