namespace GeckoPeeks.Logging;

/// <summary>
/// Sibling of the CSV. activity.log keeps the newest lines; older lines move to monthly
/// archives (activity-YYYY-MM.log, same folder) and are never deleted.
/// </summary>
public sealed class ActivityLog
{
    public const int MaxLines = 400;

    /// <summary>Held while the files of a folder are trimmed or read, so a reader never sees lines in neither file.</summary>
    private static readonly object FileGate = new();
    private readonly object _gate = new();
    private readonly List<string> _recent = new();

    public ActivityLog(string path)
    {
        Path = path;
        try
        {
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadAllLines(path).TakeLast(12))
                _recent.Add(line);
        }
        catch
        {
            // The activity file is optional. A bad read must not stop the monitor.
        }
    }

    public string Path { get; private set; }

    /// <summary>Later lines go to <paramref name="path"/>; the recent lines stay on screen.</summary>
    public void MoveTo(string path)
    {
        lock (_gate)
            Path = path;
    }

    public IReadOnlyList<string> Recent()
    {
        lock (_gate)
            return _recent.ToArray();
    }

    public string Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (_gate)
        {
            _recent.Add(line);
            if (_recent.Count > 12)
                _recent.RemoveAt(0);

            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                lock (FileGate)
                {
                    File.AppendAllText(Path, line + Environment.NewLine);
                    TrimIfNeeded();
                }
            }
            catch
            {
                // Activity history is best-effort. The CSV write is separate.
            }
        }

        return line;
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < 96 * 1024)
            return;

        var lines = File.ReadAllLines(Path);
        if (lines.Length <= MaxLines)
            return;

        // Archive first. If the archive cannot be written, activity.log is left as it is.
        var folder = System.IO.Path.GetDirectoryName(Path) ?? "";
        foreach (var group in lines[..^MaxLines].GroupBy(line => ArchiveMonth(line, DateTime.Now)))
        {
            var archive = System.IO.Path.Combine(folder, ArchiveName(group.Key));
            var batch = group.ToList();
            // A trim that archived these lines and then failed to rewrite activity.log must not
            // archive them again: skip up to the archive's last line when it is in this batch.
            var skip = AlreadyArchived(archive, batch);
            if (skip < batch.Count)
                File.AppendAllLines(archive, batch.Skip(skip));
        }

        // A temp file and a move, so a crash mid-write never leaves activity.log cut short.
        var temp = Path + ".tmp";
        File.WriteAllLines(temp, lines[^MaxLines..]);
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>
    /// How many lines at the start of <paramref name="batch"/> the archive already ends with: the
    /// longest n where the archive's last n lines equal the batch's first n lines.
    /// </summary>
    public static int AlreadyArchived(string archive, List<string> batch)
    {
        if (!File.Exists(archive) || batch.Count == 0)
            return 0;
        // Only the archive's last batch.Count lines can overlap.
        var tail = new Queue<string>(batch.Count);
        foreach (var line in SharedText.ReadLines(archive))
        {
            if (line.Length == 0)
                continue;
            if (tail.Count == batch.Count)
                tail.Dequeue();
            tail.Enqueue(line);
        }

        var lines = tail.ToArray();
        for (var n = Math.Min(lines.Length, batch.Count); n > 0; n--)
        {
            var match = true;
            for (var i = 0; i < n && match; i++)
                match = lines[lines.Length - n + i] == batch[i];
            if (match)
                return n;
        }

        return 0;
    }

    public const string ArchivePattern = "activity-*.log";

    public static string ArchiveName(string month) => $"activity-{month}.log";

    /// <summary>"yyyy-MM" from the line's own time stamp, else from <paramref name="fallback"/>.</summary>
    public static string ArchiveMonth(string line, DateTime fallback) =>
        LineTime(line) is { } at ? at.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)
            : fallback.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The local time a line starts with ("yyyy-MM-dd HH:mm:ss"), or null.</summary>
    public static DateTime? LineTime(string line) =>
        line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
            ? at
            : null;

    /// <summary>All saved lines in a folder, oldest first: the monthly archives, then activity.log.</summary>
    public static IReadOnlyList<string> ReadAll(string folder)
    {
        // Read under the gate: a trim between the archives and activity.log would hide lines.
        lock (FileGate)
            return ReadAllUnlocked(folder).ToList();
    }

    private static IEnumerable<string> ReadAllUnlocked(string folder)
    {
        string[] archives;
        try
        {
            archives = Directory.Exists(folder) ? Directory.GetFiles(folder, ArchivePattern) : Array.Empty<string>();
        }
        catch
        {
            archives = Array.Empty<string>();
        }

        Array.Sort(archives, StringComparer.OrdinalIgnoreCase);
        foreach (var file in archives)
        {
            foreach (var line in SharedText.ReadLines(file))
                yield return line;
        }

        foreach (var line in SharedText.ReadLines(System.IO.Path.Combine(folder, "activity.log")))
            yield return line;
    }
}
