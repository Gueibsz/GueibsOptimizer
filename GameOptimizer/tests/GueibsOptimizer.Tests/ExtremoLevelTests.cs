using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;
using Xunit;

namespace GueibsOptimizer.Tests;

/// <summary>Versão 2.4: nível Extremo (só desempenho, risco Alto, fora do Boost, confirmação dupla).</summary>
public class ExtremoLevelTests
{
    private sealed class ExtremeFake(string id) : ITweak, ILeveledTweak
    {
        public string Id => id;
        public string Name => id;
        public string Description => id;
        public TweakCategory Category => TweakCategory.Windows;
        public TweakRisk Risk => TweakRisk.High;
        public int Points => 0;
        public string? Notice => "aviso";
        public bool InBoost => false;
        public PerformanceLevel Level => PerformanceLevel.Extremo;
        public int ApplyCalls { get; private set; }
        public bool IsApplicable() => true;
        public bool IsApplied() => ApplyCalls > 0;
        public Task ApplyAsync() { ApplyCalls++; return Task.CompletedTask; }
        public Task RevertAsync() => Task.CompletedTask;
    }

    private static BoostService Boost(IEnumerable<ITweak> tweaks)
    {
        var logger = new Logger(null);
        return new BoostService(new TweakRegistry(tweaks), new ScoreService(logger), new FakeRestorePoints(true), logger, dryRun: false);
    }

    [Fact]
    public async Task OneClickBoostNeverAppliesExtremeTweaks()
    {
        var extreme = new ExtremeFake("x");
        var low = new FakeTweak("low", 100);
        await Boost([low, extreme]).RunAsync(_ => Task.FromResult(true));
        Assert.Equal(1, low.ApplyCalls);
        Assert.Equal(0, extreme.ApplyCalls);
    }

    [Fact]
    public async Task ExtremeLevelAppliesExtremeTweaksOnlyWhenChosen()
    {
        var extreme = new ExtremeFake("x");
        var low = new FakeTweak("low", 100);
        var high = new FakeTweak("high", 0, TweakRisk.High);
        await Boost([low, extreme, high]).RunAsync(_ => Task.FromResult(true), selector: t => PerformanceLevels.Includes(PerformanceLevel.Extremo, t));
        Assert.Equal(1, low.ApplyCalls);
        Assert.Equal(1, extreme.ApplyCalls);
        Assert.Equal(0, high.ApplyCalls); // risco Alto comum nunca entra em nível

        var extreme2 = new ExtremeFake("y");
        await Boost([extreme2]).RunAsync(_ => Task.FromResult(true), selector: t => PerformanceLevels.Includes(PerformanceLevel.Maximo, t));
        Assert.Equal(0, extreme2.ApplyCalls);
    }

    [Fact]
    public void ExtremeIsCurrentOnlyWhenItsOwnTweaksAreApplied()
    {
        var low = new FakeTweak("low", 100, applied: true);
        var extreme = new ExtremeFake("x");
        Assert.Equal(PerformanceLevel.Maximo, PerformanceLevels.Current([new TweakState(low, true, true), new TweakState(extreme, true, false)]));
        Assert.Equal(PerformanceLevel.Extremo, PerformanceLevels.Current([new TweakState(low, true, true), new TweakState(extreme, true, true)]));
        Assert.True(PerformanceLevels.RequiresConfirmation(PerformanceLevel.Extremo));
    }

    private static string Json(string risk = "High", string inBoost = "false", int points = 0, string info = "\"info\": { \"risk\": \"Alto: perde a função X.\" }, ") =>
        "{ \"version\": 1, \"tweaks\": [ { \"id\": \"extreme.test\", \"category\": \"Windows\", \"risk\": \"" + risk + "\", \"points\": " + points +
        ", \"level\": \"Extremo\", \"inBoost\": " + inBoost + ", \"name\": \"Extremo: teste\", \"description\": \"Descrição.\", \"notice\": \"Aviso.\", " + info +
        "\"registry\": [ { \"hive\": \"HKCU\", \"path\": \"Software\\\\X\", \"name\": \"V\", \"type\": \"DWord\", \"value\": 1, \"default\": null } ] } ] }";

    [Fact]
    public void ValidationEnforcesExtremeRules()
    {
        Assert.Single(DeclarativeTweakLoader.Parse(Json()));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(Json(risk: "Medium")));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(Json(inBoost: "true")));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(Json(points: 3)));
        Assert.Throws<DeclarativeValidationException>(() => DeclarativeTweakLoader.Parse(Json(info: string.Empty)));
    }

    public static readonly string[] ExpectedIds =
    [
        "extreme.search-indexing-off", "extreme.sysmain-off", "extreme.delivery-optimization-p2p-off",
        "extreme.power-throttling-off", "extreme.mmcss-games", "extreme.error-reporting-off"
    ];

    [Fact]
    public void RealCatalogHasOnlyPerformanceExtremeTweaks()
    {
        using var key = new TestRegistryKey();
        var all = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true)).All;
        var extremes = all.Where(PerformanceLevels.IsExtreme).ToList();
        Assert.Equal(ExpectedIds.Order(), extremes.Select(t => t.Id).Order());
        foreach (var t in extremes)
        {
            Assert.Equal(TweakRisk.High, t.Risk);
            Assert.False(t.InBoost);
            Assert.Equal(0, t.Points);
            Assert.False(string.IsNullOrWhiteSpace(t.Notice));
            Assert.False(TweakText.IsRecommended(t));
        }
        // Nada que reduza a segurança: nenhum item do Extremo mexe em Defender, Firewall, UAC, Windows Update ou mitigações.
        var json = DeclarativeTweakLoader.Parse(ReadCatalog()).Where(d => ExpectedIds.Contains(d.Id)).ToList();
        var forbidden = new[] { "WinDefend", "MpsSvc", "wuauserv", "EnableLUA", "FeatureSettingsOverride", "DisableAntiSpyware", "EnableVirtualizationBasedSecurity", "UsoSvc", "WaaSMedicSvc", "SecurityHealthService" };
        var text = string.Join("\n", json.Select(d => System.Text.Json.JsonSerializer.Serialize(d)));
        foreach (var f in forbidden) Assert.DoesNotContain(f, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtremeTweaksApplyAndRevertInDryRunWithoutTouchingTheSystem()
    {
        using var key = new TestRegistryKey();
        var all = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true)).All;
        foreach (var t in all.Where(PerformanceLevels.IsExtreme))
        {
            await t.ApplyAsync();
            await t.RevertAsync();
        }
    }

    private static string ReadCatalog()
    {
        var asm = typeof(DeclarativeTweakLoader).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("Data.tweaks.json", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new System.IO.StreamReader(s);
        return r.ReadToEnd();
    }
}
