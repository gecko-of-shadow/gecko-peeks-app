using GeckoPeeks.Routing;

namespace GeckoPeeks.Text;

/// <summary>
/// The route that the next test is compared with: the previous full path (for the hop diff) and
/// the confirmed upstream and city (for the route-change balloon). It belongs to one target.
/// A row traced to another target (a test that was running when the region changed) never
/// becomes the baseline, so the switch itself reports no changed hops and no route change.
/// </summary>
public sealed class RouteBaseline
{
    private readonly RouteChangeConfirmer _confirmer = new();

    /// <summary>The target of the current region. Blank when no region is known (v2.0 callers).</summary>
    public string Target { get; private set; } = "";

    /// <summary>The full path of the last test that belongs to this baseline, else empty.</summary>
    public string PreviousPath { get; private set; } = "";

    /// <summary>True when the target changed. Then the baseline is empty.</summary>
    public bool SetTarget(string? target)
    {
        var next = (target ?? "").Trim();
        if (string.Equals(next, Target, StringComparison.Ordinal))
            return false;
        Target = next;
        Clear();
        return true;
    }

    public void Clear()
    {
        _confirmer.Reset();
        PreviousPath = "";
    }

    /// <summary>A remembered test from disk.</summary>
    public void Seed(string? fullPath, string? upstream, string? city, string? network)
    {
        PreviousPath = fullPath ?? "";
        _confirmer.Seed(upstream, city, network);
    }

    /// <summary>A blank target on either side matches (old rows and callers with no region).</summary>
    public bool Matches(string? rowTarget)
    {
        var row = (rowTarget ?? "").Trim();
        return row.Length == 0 || Target.Length == 0 || string.Equals(row, Target, StringComparison.Ordinal);
    }

    /// <param name="Stale">The row was traced to another target. Nothing changed.</param>
    /// <param name="RouteMessage">The balloon text once two tests agree on a new route, else null.</param>
    public sealed record Result(bool Stale, string PathChange, IReadOnlyList<int> ChangedHops, string? RouteMessage);

    /// <summary>Compares a logged test with the baseline, then makes it the baseline.</summary>
    public Result Observe(string? rowTarget, string? fullPath, string? upstream, string? city, string? network)
    {
        if (!Matches(rowTarget))
            return new Result(true, "", Array.Empty<int>(), null);

        var previous = PreviousPath;
        PreviousPath = fullPath ?? "";
        var change = "";
        IReadOnlyList<int> hops = Array.Empty<int>();
        if (!string.IsNullOrWhiteSpace(previous))
        {
            change = HopDiff.Describe(previous, fullPath);
            hops = HopDiff.ChangedHopNumbers(previous, fullPath);
        }

        return new Result(false, change, hops, _confirmer.Observe(upstream, city, network));
    }
}

/// <summary>Wording for the target of the shown test, which can differ from the region target.</summary>
public static class TestTargetText
{
    /// <summary>The label for an address: the region's own label, else the region that uses it, else by prefix.</summary>
    public static string LabelFor(string? ip, RegionInfo current, RefData data)
    {
        var address = (ip ?? "").Trim();
        if (address.Length == 0 || string.Equals(address, current.TargetIp, StringComparison.Ordinal))
            return current.TargetLabel;
        var region = data.Regions.FirstOrDefault(r => string.Equals(r.TargetIp, address, StringComparison.Ordinal));
        if (region != null)
            return region.TargetLabel;
        return address.StartsWith("192.207.0.", StringComparison.Ordinal) ? RefData.EdgeLabel : RefData.PlatformLabel;
    }

    /// <summary>
    /// The diagnosis Target line: the target the shown test traced. When the region changed since,
    /// it also names the target of the next test.
    /// </summary>
    public static string Diagnosis(string? testTargetIp, RegionInfo current, RefData data)
    {
        var tested = (testTargetIp ?? "").Trim();
        if (tested.Length == 0 || string.Equals(tested, current.TargetIp, StringComparison.Ordinal))
            return current.TargetLabel + " " + current.TargetIp;
        return LabelFor(tested, current, data) + " " + tested + " (the next test traces " + current.TargetIp + ")";
    }
}
