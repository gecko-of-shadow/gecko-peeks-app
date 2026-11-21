using System.Diagnostics;

namespace GeckoPeeks.App;

public static class LeagueProcess
{
    public static bool IsRunning()
    {
        try
        {
            // "League of Legends.exe" is reported without the extension.
            var found = Process.GetProcessesByName("League of Legends");
            try
            {
                return found.Length > 0;
            }
            finally
            {
                foreach (var process in found)
                    process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// When the running game process started (UTC), else null. Only the start time is read; the
    /// game process is not opened for anything else.
    /// </summary>
    public static DateTime? StartedUtc()
    {
        try
        {
            var found = Process.GetProcessesByName("League of Legends");
            try
            {
                DateTime? newest = null;
                foreach (var process in found)
                {
                    try
                    {
                        var start = process.StartTime.ToUniversalTime();
                        if (newest == null || start > newest)
                            newest = start;
                    }
                    catch
                    {
                        // Access denied or exited: no time from this one.
                    }
                }

                return newest;
            }
            finally
            {
                foreach (var process in found)
                    process.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }
}
