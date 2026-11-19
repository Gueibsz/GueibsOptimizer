using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase92Tests
{
    private static TweakRegistry Registry(TestRegistryKey key) => TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));

    [Theory]
    [InlineData("privacy.activity-history-off", true, PerformanceLevel.Baixo)]
    [InlineData("privacy.consumer-features-off", true, PerformanceLevel.Baixo)]
    [InlineData("privacy.powershell-telemetry-off", true, PerformanceLevel.Baixo)]
    [InlineData("privacy.location-off", false, PerformanceLevel.Alto)]
    [InlineData("privacy.background-apps-off", false, PerformanceLevel.Alto)]
    [InlineData("power.hibernate-off", false, PerformanceLevel.Alto)]
    [InlineData("windows.storage-sense-off", false, PerformanceLevel.Maximo)]
    [InlineData("edge.sidebar-off", false, PerformanceLevel.Medio)]
    public void NewTweaksHaveDocumentedBoostAndLevel(string id, bool inBoost, PerformanceLevel level)
    {
        using var key = new TestRegistryKey();
        var t = Assert.IsType<DeclarativeTweak>(Registry(key).Find(id));
        Assert.Equal(inBoost, t.InBoost);
        Assert.Equal(level, t.Level);
        // Ajuste opcional de risco Médio/Alto nunca entra no Boost.
        if (t.Risk != TweakRisk.Low) Assert.False(t.InBoost);
    }

    [Fact]
    public void EdgeIsOnlyTouchedByPolicies()
    {
        using var key = new TestRegistryKey();
        var edge = Registry(key).All.OfType<DeclarativeTweak>().Where(t => t.Id.StartsWith("edge.")).ToList();
        Assert.Equal(5, edge.Count);
        Assert.All(edge, t =>
        {
            Assert.Empty(t.Definition.Commands);
            Assert.All(t.Definition.Registry, r => Assert.StartsWith(@"SOFTWARE\Policies\Microsoft\Edge", r.Path));
        });
    }

    /// <summary>Lê o estado real da hibernação (powercfg): VMs como o runner do CI não têm hibernação.</summary>
    [Fact]
    [Trait("Category", "RequiresHardware")]
    public void HibernationTweaksAreOpposites()
    {
        using var key = new TestRegistryKey();
        var reg = Registry(key);
        var off = (DeclarativeTweak)reg.Find("power.hibernate-off")!;
        var on = (DeclarativeTweak)reg.Find("power.hibernate-on")!;
        Assert.NotEqual(off.IsApplied(), on.IsApplied());
        Assert.Equal(["/hibernate", "on"], CommandAllowlist.All["hibernate-off"].RevertArgs);
    }

    [Fact]
    public void TaskbarEndTaskIsWindows11Only()
    {
        using var key = new TestRegistryKey();
        var t = (DeclarativeTweak)Registry(key).Find("windows.taskbar-end-task")!;
        Assert.Equal([11], t.Definition.WindowsVersions);
        if (!GueibsOptimizer.Core.OsInfo.IsWindows11) Assert.False(t.IsApplicable());
    }

    [Fact]
    public void ServicesAllowlistRejectsProtectedAndDuplicates()
    {
        Assert.Throws<DeclarativeValidationException>(() => ServicesManual.Parse(
            "{ \"services\": [ { \"name\": \"WinDefend\", \"default\": \"Automatic\", \"reason\": \"x\" } ] }"));
        Assert.Throws<DeclarativeValidationException>(() => ServicesManual.Parse(
            "{ \"services\": [ { \"name\": \"TrkWks\", \"default\": \"Automatic\", \"reason\": \"x\" }, { \"name\": \"trkwks\", \"default\": \"Automatic\", \"reason\": \"y\" } ] }"));
        Assert.Single(ServicesManual.Parse("{ \"services\": [ { \"name\": \"TrkWks\", \"default\": \"Automatic\", \"reason\": \"x\" } ] }"));
    }

    [Fact]
    public void ServiceWithAutomaticDependentsIsSkipped()
    {
        (string, string[], int)[] graph = [("SvcB", ["SvcA"], 2), ("SvcC", ["SvcD"], 3)];
        Assert.Equal(["SvcB"], ServicesManual.AutomaticDependents("SvcA", graph));
        Assert.Empty(ServicesManual.AutomaticDependents("SvcD", graph)); // dependente manual não bloqueia
        var def = ServicesManual.BuildDefinition(
            [new("SvcA", "Automatic", "a"), new("SvcD", "Automatic", "d")], n => ServicesManual.AutomaticDependents(n, graph));
        Assert.Equal(["SvcD"], def.Services.Select(s => s.Name));
        Assert.Contains("SvcA", def.Info!.Notes);
        Assert.False(def.InBoost);
        Assert.Equal("Medium", def.Risk);
    }

    [Fact]
    public void EmbeddedServicesListIsSafe()
    {
        using var key = new TestRegistryKey();
        if (Registry(key).Find(ServicesManual.TweakId) is DeclarativeTweak t)
            Assert.All(t.Definition.Services, s => Assert.DoesNotContain(s.Name, DeclarativeTweakLoader.ProtectedServices));
    }

    [Fact]
    public void PointsAreNormalizedTo100()
    {
        var n = TweakRegistry.Normalize([("a", 50), ("b", 50), ("c", 3), ("d", 0)]);
        Assert.Equal(100, n.Values.Sum());
        Assert.False(n.ContainsKey("d"));
        using var key = new TestRegistryKey();
        var reg = Registry(key);
        Assert.Equal(100, reg.All.Sum(reg.PointsOf));
    }

    [Fact]
    public void DiskCleanupUsesFixedProfile()
    {
        Assert.Equal(["/sageset:64"], GueibsOptimizer.ViewModels.WindowsViewModel.CleanMgrSetArgs);
        Assert.Equal(["/sagerun:64"], GueibsOptimizer.ViewModels.WindowsViewModel.CleanMgrRunArgs);
    }
}
