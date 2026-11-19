using GueibsOptimizer.Core;
using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase811Tests
{
    [Theory]
    [InlineData(19045, 5131, "22H2", "Windows 10 22H2 · build 19045.5131")]
    [InlineData(22631, 4460, "23H2", "Windows 11 23H2 · build 22631.4460")]
    [InlineData(26100, 0, "", "Windows 11 · build 26100")]
    [InlineData(17763, 1, "", "Windows 10 · build 17763.1")]
    public void DescribesWindowsVersion(int build, int ubr, string display, string expected) =>
        Assert.Equal(expected, OsInfo.Describe(build, ubr, display));

    [Fact]
    public void Windows11OnlyTweaksAreNotApplicableOnWindows10()
    {
        const int win10 = 19045;
        Assert.False(OsInfo.Supports("windows.widgets-off", win10));
        Assert.False(OsInfo.Supports("windows.classic-context-menu", win10));
        Assert.False(OsInfo.Supports("privacy.copilot-off", win10));
        Assert.False(OsInfo.Supports("privacy.recall-policy", win10));
        Assert.True(OsInfo.Supports("gpu.hags", win10));
        Assert.True(OsInfo.Supports("gaming.game-mode", win10)); // sem requisito
        Assert.Equal("Só existe no Windows 11", OsInfo.RequirementText("windows.widgets-off", win10));
        Assert.Null(OsInfo.RequirementText("windows.widgets-off", 22631));
    }

    [Fact]
    public void VersionGatesAreOrdered()
    {
        Assert.True(OsInfo.Supports("privacy.copilot-off", 22621));
        Assert.False(OsInfo.Supports("privacy.copilot-off", 22000));
        Assert.False(OsInfo.Supports("privacy.recall-feature", 22631));
        Assert.True(OsInfo.Supports("privacy.recall-feature", 26100));
        Assert.False(OsInfo.Supports("gpu.hags", 18363));
    }

    [Fact]
    public void EveryRequirementPointsToARealTweak()
    {
        using var key = new TestRegistryKey();
        var ids = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true)).All.Select(t => t.Id).ToHashSet();
        Assert.All(OsInfo.Requirements.Keys, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void TweaksFollowTheCurrentWindows()
    {
        // Os ajustes com requisito usam a mesma regra central: no Windows atual, aplicável == Supports(id).
        using var key = new TestRegistryKey();
        foreach (var t in TweakRegistry.CreateDefault(key.CreateContext(dryRun: true)).All.Where(t => OsInfo.Requirements.ContainsKey(t.Id)))
            if (!OsInfo.Supports(t.Id)) Assert.False(t.IsApplicable(), t.Id);
    }
}
