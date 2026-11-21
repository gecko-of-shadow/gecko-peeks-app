using GeckoPeeks.App;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>GECKO_PEEKS_DEV_PROBE is a Dev-only opt-in for one real test. Demo always wins.</summary>
public sealed class DevProbeTests
{
    [Theory]
    [InlineData(RunProfile.Dev, "1", false, true)]
    [InlineData(RunProfile.Dev, " 1 ", false, true)]
    [InlineData(RunProfile.Dev, "1", true, false)]
    [InlineData(RunProfile.Dev, null, false, false)]
    [InlineData(RunProfile.Dev, "", false, false)]
    [InlineData(RunProfile.Dev, "true", false, false)]
    [InlineData(RunProfile.Dev, "0", false, false)]
    [InlineData(RunProfile.Production, "1", false, false)]
    [InlineData(RunProfile.Production, null, false, false)]
    public void Dev_probe_needs_dev_profile_the_exact_value_and_no_demo(RunProfile profile, string? value, bool demo, bool expected)
    {
        Assert.Equal(expected, AppIsolation.DevProbeOn(profile, value, demo));
    }

    [Fact]
    public void Dev_probe_does_not_change_the_default_probe_rule()
    {
        Assert.True(AppIsolation.AllowsProbes(RunProfile.Production));
        Assert.False(AppIsolation.AllowsProbes(RunProfile.Dev));
    }
}
