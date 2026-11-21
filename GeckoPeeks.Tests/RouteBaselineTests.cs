using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// A region change while a test is running: the running test still traces the old target.
/// Its row must not become the baseline of the new target. Neutral demo-style paths.
/// </summary>
public class RouteBaselineTests
{
    private const string OldTarget = "192.207.0.5";
    private const string NewTarget = "203.0.113.50";
    private const string OldPath = "192.168.1.1 > * > 184.105.64.1 > 184.105.80.1 > 104.160.131.1 > 192.207.0.5";
    private const string NewPath = "192.168.1.1 > * > 154.54.1.1 > 154.54.2.2 > 203.0.113.40 > 203.0.113.50";

    private static RouteBaseline SeededOnTheOldTarget()
    {
        var baseline = new RouteBaseline();
        baseline.SetTarget(OldTarget);
        baseline.Seed(OldPath, "HE", "chi01", "Riot Direct");
        return baseline;
    }

    [Fact]
    public void A_row_for_the_old_target_after_a_region_change_is_not_the_baseline()
    {
        var baseline = SeededOnTheOldTarget();

        // The region changes while a test of the old target is running.
        Assert.True(baseline.SetTarget(NewTarget));
        Assert.Equal("", baseline.PreviousPath);

        // The running test finishes: logged, but stale.
        var inFlight = baseline.Observe(OldTarget, OldPath, "HE", "chi01", "Riot Direct");
        Assert.True(inFlight.Stale);
        Assert.Equal("", inFlight.PathChange);
        Assert.Empty(inFlight.ChangedHops);
        Assert.Null(inFlight.RouteMessage);
        Assert.Equal("", baseline.PreviousPath);

        // The first test of the new target starts the baseline: no hop diff.
        var first = baseline.Observe(NewTarget, NewPath, "Cogent", "ams1", "Riot Direct");
        Assert.False(first.Stale);
        Assert.Equal("", first.PathChange);
        Assert.Empty(first.ChangedHops);
        Assert.Null(first.RouteMessage);

        // Later tests of the same route: no false "route changed" balloon.
        for (var i = 0; i < 3; i++)
        {
            var next = baseline.Observe(NewTarget, NewPath, "Cogent", "ams1", "Riot Direct");
            Assert.Null(next.RouteMessage);
            Assert.Empty(next.ChangedHops);
        }
    }

    [Fact]
    public void Without_the_stale_check_the_old_row_would_seed_a_false_change()
    {
        // The same sequence with the in-flight row taken as the baseline (the v2.1 bug).
        var baseline = new RouteBaseline();
        baseline.Seed(OldPath, "HE", "chi01", "Riot Direct");
        var diff = baseline.Observe("", NewPath, "Cogent", "ams1", "Riot Direct");
        Assert.NotEmpty(diff.ChangedHops);
        Assert.NotNull(baseline.Observe("", NewPath, "Cogent", "ams1", "Riot Direct").RouteMessage);
    }

    [Fact]
    public void Same_target_rows_still_report_changed_hops_and_route_changes()
    {
        var baseline = SeededOnTheOldTarget();
        Assert.False(baseline.SetTarget(OldTarget));

        var moved = OldPath.Replace("184.105.80.1", "184.105.80.2");
        var first = baseline.Observe(OldTarget, moved, "Cogent", "chi01", "Riot Direct");
        Assert.False(first.Stale);
        Assert.Equal(new[] { 4 }, first.ChangedHops);
        Assert.Null(first.RouteMessage);
        Assert.NotNull(baseline.Observe(OldTarget, moved, "Cogent", "chi01", "Riot Direct").RouteMessage);
    }

    [Theory]
    [InlineData("", OldTarget, true)]
    [InlineData(OldTarget, "", true)]
    [InlineData(OldTarget, OldTarget, true)]
    [InlineData(OldTarget, NewTarget, false)]
    public void A_blank_target_on_either_side_matches(string baselineTarget, string rowTarget, bool expected)
    {
        var baseline = new RouteBaseline();
        baseline.SetTarget(baselineTarget);
        Assert.Equal(expected, baseline.Matches(rowTarget));
    }
}

public class TestTargetTextTests
{
    [Fact]
    public void Diagnosis_names_the_tested_target_and_the_next_one_after_a_region_change()
    {
        var data = RefData.BuiltIn;
        var na = data.FindRegion("na1")!;
        var other = data.Regions.First(r => r.TargetIp != na.TargetIp);

        Assert.Equal(na.TargetLabel + " " + na.TargetIp, TestTargetText.Diagnosis(na.TargetIp, na, data));
        Assert.Equal(na.TargetLabel + " " + na.TargetIp, TestTargetText.Diagnosis("", na, data));

        var text = TestTargetText.Diagnosis(na.TargetIp, other, data);
        Assert.StartsWith(na.TargetLabel + " " + na.TargetIp, text);
        Assert.EndsWith("(the next test traces " + other.TargetIp + ")", text);
    }

    [Fact]
    public void Labels_follow_the_region_that_uses_the_address()
    {
        var data = RefData.BuiltIn;
        var na = data.FindRegion("na1")!;
        Assert.Equal(RefData.EdgeLabel, TestTargetText.LabelFor("192.207.0.77", na, data));
        Assert.Equal(RefData.PlatformLabel, TestTargetText.LabelFor("203.0.113.9", na, data));
    }
}
