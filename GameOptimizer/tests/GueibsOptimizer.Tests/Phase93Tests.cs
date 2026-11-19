using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using GueibsOptimizer.ViewModels;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase93Tests
{
    [Fact]
    public void PreferencesExistAndStayOutOfBoostAndScore()
    {
        using var key = new TestRegistryKey();
        var reg = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var prefs = reg.All.Where(PreferencesViewModel.IsPreference).ToList();
        string[] expected = ["pref.show-file-extensions", "pref.show-hidden-files", "pref.numlock-on", "pref.dark-theme", "pref.detailed-bsod",
            "pref.verbose-logon", "pref.taskbar-left", "pref.taskbar-search-hidden", "pref.taskbar-taskview-hidden", "pref.snap-off",
            "pref.snap-assist-off", "pref.snap-layouts-off"];
        Assert.Equal(expected.Order(), prefs.Select(p => p.Id).Order());
        Assert.All(prefs, p =>
        {
            Assert.False(p.InBoost);
            Assert.Equal(0, reg.PointsOf(p));
            Assert.False(PerformanceLevels.Includes(PerformanceLevel.Maximo, p));
            Assert.False(CategoryTweaksViewModel.IsWindows(p)); // não aparecem duplicados na página Windows
        });
    }

    [Fact]
    public void Windows11OnlyPreferencesAreMarked()
    {
        using var key = new TestRegistryKey();
        var reg = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        foreach (var id in new[] { "pref.taskbar-left", "pref.snap-layouts-off" })
            Assert.Equal([11], ((DeclarativeTweak)reg.Find(id)!).Definition.WindowsVersions);
    }

    [Fact]
    public void ExplorerRestartOnlyKillsExplorer() =>
        Assert.Equal(["/f", "/im", "explorer.exe"], PreferencesViewModel.KillExplorerArgs);
}
