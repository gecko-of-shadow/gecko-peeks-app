namespace GeckoPeeks.Ui;

/// <summary>
/// Native colours. The v2 window is drawn by the web UI; these colours are for the tray menu,
/// the WebView2 fallback panel and the gecko mark, and match the web palette.
/// </summary>
internal static class Theme
{
    // v2 palette (same values as GeckoPeeks/Web/app.css).
    public static readonly Color Ink = Color.FromArgb(0x0f, 0x0f, 0x10);
    public static readonly Color Surface = Color.FromArgb(0x16, 0x16, 0x18);
    public static readonly Color SurfaceRaised = Color.FromArgb(0x1c, 0x1c, 0x1f);
    public static readonly Color Border = Color.FromArgb(0x2a, 0x2a, 0x2e);
    public static readonly Color Accent = Color.FromArgb(0xf6, 0x82, 0x1f);
    public static readonly Color Text = Color.FromArgb(0xf4, 0xf4, 0xf5);
    public static readonly Color TextMuted = Color.FromArgb(0xa1, 0xa1, 0xaa);

    // The gecko mark keeps its original cream body and amber eye.
    public static readonly Color Cream = Color.FromArgb(246, 242, 232);
    public static readonly Color Amber = Color.FromArgb(240, 200, 110);
}
