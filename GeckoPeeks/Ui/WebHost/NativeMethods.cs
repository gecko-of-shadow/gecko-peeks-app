using System.Runtime.InteropServices;

namespace GeckoPeeks.Ui.WebHost;

internal static class NativeMethods
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    /// <summary>COLORREF is 0x00BBGGRR. #0f0f10 is R=0x0f, G=0x0f, B=0x10.</summary>
    private const int CaptionColor = 0x00100F0F;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    /// <summary>
    /// After the web view is gone: collect what it left on the managed heap and hand the unused
    /// pages back to Windows, so the tray copy shows a small footprint in Task Manager.
    /// </summary>
    public static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Trimming is only a nicety.
        }
    }

    /// <summary>Dark title bar with the page background colour. Older Windows ignores it.</summary>
    public static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            var on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            var color = CaptionColor;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref color, sizeof(int));
        }
        catch
        {
            // dwmapi missing or the attribute is unknown: keep the default frame.
        }
    }
}
