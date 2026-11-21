using System.Reflection;

namespace GeckoPeeks.Ui.WebHost;

/// <summary>
/// The web UI files compiled into the exe (csproj: EmbeddedResource, logical name
/// "GeckoPeeks.Web/&lt;file&gt;"). Only these flat file names are served.
/// </summary>
internal static class WebAssets
{
    public const string Origin = "https://gecko.peeks.invalid";
    public const string Filter = Origin + "/*";
    public const string StartFile = "index.html";
    private const string Prefix = "GeckoPeeks.Web/";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json; charset=utf-8",
        [".woff2"] = "font/woff2",
    };

    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>True when the URL is on the app origin (any path).</summary>
    public static bool IsAppUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.Equals(uri.Host, "gecko.peeks.invalid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Looks up the file for an app URL. Query and fragment are ignored.</summary>
    public static bool TryGet(string url, out byte[] bytes, out string contentType)
    {
        bytes = Array.Empty<byte>();
        contentType = "";
        if (!IsAppUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var name = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (name.Length == 0)
            name = StartFile;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
            return false;
        if (!Types.TryGetValue(Path.GetExtension(name), out var type))
            return false;

        lock (Lock)
        {
            if (!Cache.TryGetValue(name, out var cached))
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name);
                if (stream == null)
                    return false;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                cached = copy.ToArray();
                Cache[name] = cached;
            }

            bytes = cached;
        }

        contentType = type;
        return true;
    }

    /// <summary>Drops the cached bytes so a hidden window holds no copy of the UI.</summary>
    public static void ClearCache()
    {
        lock (Lock)
            Cache.Clear();
    }
}
