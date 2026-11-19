using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase84Tests
{
    private sealed class StubTweak(string id, TweakCategory category, TweakRisk risk, bool inBoost = true) : ITweak
    {
        public string Id => id;
        public string Name => id;
        public string Description => id;
        public TweakCategory Category => category;
        public TweakRisk Risk => risk;
        public int Points => 1;
        public string? Notice => null;
        public bool InBoost => inBoost;
        public bool IsApplicable() => true;
        public bool IsApplied() => false;
        public Task ApplyAsync() => Task.CompletedTask;
        public Task RevertAsync() => Task.CompletedTask;
    }

    private static readonly StubTweak Power = new("power", TweakCategory.Power, TweakRisk.Low);
    private static readonly StubTweak Gpu = new("gpu", TweakCategory.Gpu, TweakRisk.Low);
    private static readonly StubTweak Privacy = new("privacy", TweakCategory.Privacy, TweakRisk.Low);
    private static readonly StubTweak Service = new("service", TweakCategory.Windows, TweakRisk.Medium);
    private static readonly StubTweak Danger = new("danger", TweakCategory.Windows, TweakRisk.High);
    private static readonly StubTweak Pref = new("pref", TweakCategory.Windows, TweakRisk.Low, inBoost: false);
    private static readonly ITweak[] All = [Power, Gpu, Privacy, Service, Danger, Pref];

    [Fact]
    public void LevelsMapToDocumentedTweaks()
    {
        Assert.Equal(["power"], Ids(PerformanceLevel.Baixo));
        Assert.Equal(["power", "gpu"], Ids(PerformanceLevel.Medio));
        Assert.Equal(["power", "gpu", "privacy"], Ids(PerformanceLevel.Alto));
        Assert.Equal(["power", "gpu", "privacy", "service"], Ids(PerformanceLevel.Maximo));
        Assert.True(PerformanceLevels.RequiresConfirmation(PerformanceLevel.Maximo));
        Assert.False(PerformanceLevels.RequiresConfirmation(PerformanceLevel.Alto));
    }

    private static string[] Ids(PerformanceLevel level) => PerformanceLevels.TweaksFor(level, All).Select(t => t.Id).ToArray();

    private static TweakState S(ITweak t, bool applied, bool applicable = true) => new(t, applicable, applied);

    [Fact]
    public void CurrentLevelIsHighestCompleteLevel()
    {
        Assert.Null(PerformanceLevels.Current([S(Power, false), S(Gpu, true)]));
        Assert.Equal(PerformanceLevel.Baixo, PerformanceLevels.Current([S(Power, true), S(Gpu, false), S(Privacy, true)]));
        Assert.Equal(PerformanceLevel.Alto, PerformanceLevels.Current([S(Power, true), S(Gpu, true), S(Privacy, true), S(Service, false)]));
        // Ajustes de risco Alto e de preferência não contam; não aplicáveis são ignorados.
        Assert.Equal(PerformanceLevel.Maximo, PerformanceLevels.Current(
            [S(Power, true), S(Gpu, true), S(Privacy, false, applicable: false), S(Service, true), S(Danger, false), S(Pref, false)]));
    }

    [Fact]
    public void RealRegistryLevelsAreNestedAndMaximoEqualsBoost()
    {
        using var key = new TestRegistryKey();
        var tweaks = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true)).All;
        PerformanceLevel? previous = null;
        foreach (var level in PerformanceLevels.All)
        {
            var set = PerformanceLevels.TweaksFor(level, tweaks);
            Assert.NotEmpty(set);
            Assert.DoesNotContain(set, t => t.Risk == TweakRisk.High);
            if (previous is { } p) Assert.Superset(PerformanceLevels.TweaksFor(p, tweaks).Select(t => t.Id).ToHashSet(), set.Select(t => t.Id).ToHashSet());
            previous = level;
        }
        Assert.Equal(tweaks.Where(TweakText.IsRecommended).Select(t => t.Id).Order(),
            PerformanceLevels.TweaksFor(PerformanceLevel.Maximo, tweaks).Select(t => t.Id).Order());
    }

    [Fact]
    public void ComparisonShowsOnlyMeasuredNumbers()
    {
        var before = new SystemSnapshot(DateTime.Now, 150, 110, 6UL << 30, 12, TimeSpan.FromMilliseconds(23456));
        var after = before with { Processes = 140, RunningServices = 110, StartupItems = 9, RamUsedBytes = 5UL << 30 };
        var rows = SnapshotComparison.Rows(before, after);
        Assert.Equal("-10", rows[0].Change);
        Assert.Equal("igual", rows[1].Change);
        Assert.Equal("110", rows[1].AfterText);
        Assert.Equal("140 (-10)", rows[0].AfterText);
        Assert.Equal(string.Empty, rows[4].Change);
        Assert.StartsWith("-", rows[2].Change);
        Assert.Equal("-3", rows[3].Change);
        Assert.Equal("23,5 s".Replace(",", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator), rows[4].Before);
        var noBoot = SnapshotComparison.Rows(before with { LastBootDuration = null }, after with { LastBootDuration = null });
        Assert.Equal("não disponível", noBoot[4].Before);
    }

    [Fact]
    public void ParsesBootTimeFromDiagnosticsEvent()
    {
        const string xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData>" +
                           "<Data Name='BootStartTime'>2026-11-18T10:00:00Z</Data><Data Name='BootTime'>31250</Data></EventData></Event>";
        Assert.Equal(TimeSpan.FromMilliseconds(31250), SnapshotComparison.ParseBootEvent(xml));
        Assert.Null(SnapshotComparison.ParseBootEvent("<Event><EventData><Data Name='BootTime'>0</Data></EventData></Event>"));
        Assert.Null(SnapshotComparison.ParseBootEvent("lixo"));
    }

    [Fact]
    public async Task BoostRunsOnlySelectedLevel()
    {
        var a = new FakeTweak("a", 50);
        var b = new FakeTweak("b", 50, TweakRisk.Medium);
        var registry = new TweakRegistry([a, b]);
        var logger = new GueibsOptimizer.Core.Logger(null);
        var boost = new BoostService(registry, new ScoreService(logger), new FakeRestorePoints(true), logger, dryRun: true);
        var summary = await boost.RunAsync(_ => Task.FromResult(true), selector: t => t.Risk == TweakRisk.Low);
        Assert.Equal(1, a.ApplyCalls);
        Assert.Equal(0, b.ApplyCalls);
        Assert.Equal(50, summary.ScoreAfter);
    }
}
