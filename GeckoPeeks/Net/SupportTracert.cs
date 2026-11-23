using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Net;

/// <summary>What the export trace gave.</summary>
/// <param name="Outcome">finished (tracert ended by itself), timedOut or failed.</param>
/// <param name="Reached">True when a hop line names the traced address: it answered.</param>
public sealed record TracertResult(string Outcome, string Output, string Note, double Seconds, bool Reached = false);

/// <summary>
/// The one trace Riot support asks for: Windows tracert to the address Riot's tracert page lists,
/// as in Riot's own "Tracert.txt" steps, with a 1-second wait per probe. It runs only when the user
/// exports diagnostics, off the UI thread, with a time limit. It never throws.
/// </summary>
public static class SupportTracertRunner
{
    /// <summary>The whole trace stops after this; the zip then holds the part it got.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(180);

    /// <summary>Riot's command is "tracert &lt;ip&gt;". A 1-second wait per probe keeps it inside the limit.</summary>
    public static string Arguments(string ip) => "-w 1000 -h 30 " + ip;

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>tracert writes in the system's OEM code page (65001 when Windows uses UTF-8).</summary>
    private static Encoding OutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp = (int)GetOEMCP();
            return cp == 65001 ? new UTF8Encoding(false) : Encoding.GetEncoding(cp);
        }
        catch
        {
            return Encoding.GetEncoding(437);
        }
    }

    /// <summary>True when a hop line ("  7    20 ms    21 ms    20 ms  192.0.2.1") ends with the address.</summary>
    public static bool ReachedTarget(string output, string ip)
    {
        var pattern = new Regex(@"^\s*\d+\s.*(\s|\[)" + Regex.Escape(ip) + @"\]?\s*$", RegexOptions.CultureInvariant);
        return output.Split('\n').Any(line => pattern.IsMatch(line.TrimEnd('\r')));
    }

    public static async Task<TracertResult> RunAsync(SupportTracert address, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        if (!IPAddress.TryParse(address.Ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return new TracertResult("failed", "", "The address is not an IPv4 address.", 0);

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tracert.exe");
        if (!File.Exists(exe))
            return new TracertResult("failed", "", "Windows tracert.exe was not found.", 0);

        var ip = parsed.ToString();
        var output = new StringBuilder();
        void Add(string? line)
        {
            if (line == null)
                return;
            lock (output)
                output.Append(line).Append("\r\n");
        }

        try
        {
            var encoding = OutputEncoding();
            var start = new ProcessStartInfo(exe, Arguments(ip))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            };
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => Add(e.Data);
            process.ErrorDataReceived += (_, e) => Add(e.Data);
            process.Start();
            // A cancel (the app exits) kills tracert at once, inside Cancel(), not later on a pool thread.
            using var killOnCancel = token.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It ended by itself.
                }
            });
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(Limit);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It ended by itself.
                }

                var why = token.IsCancellationRequested
                    ? "The trace was cancelled (Gecko Peeks closed)."
                    : $"The trace was stopped after {Limit.TotalSeconds:0} seconds. The file has the hops it got by then.";
                var partial = Text(output);
                return new TracertResult("timedOut", partial, why, clock.Elapsed.TotalSeconds, ReachedTarget(partial, ip));
            }

            // Let the last lines arrive.
            process.WaitForExit();
            var text = Text(output);
            // tracert also ends with code 0 when the address never answered: "Reached" says which.
            return process.ExitCode == 0 && text.Length > 0
                ? new TracertResult("finished", text, "", clock.Elapsed.TotalSeconds, ReachedTarget(text, ip))
                : new TracertResult("failed", text, "tracert ended with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".",
                    clock.Elapsed.TotalSeconds, ReachedTarget(text, ip));
        }
        catch (Exception ex)
        {
            var text = Text(output);
            return new TracertResult("failed", text, "The trace could not run: " + ex.Message, clock.Elapsed.TotalSeconds, ReachedTarget(text, ip));
        }
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
            return output.ToString();
    }
}
