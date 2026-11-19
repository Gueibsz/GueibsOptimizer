using System.Reflection;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class TweakInfoTests
{
    private static IReadOnlyList<ITweak> RealTweaks()
    {
        using var key = new TestRegistryKey();
        return TweakRegistry.CreateDefault(key.CreateContext(dryRun: true)).All;
    }

    [Fact]
    public void EveryRegisteredTweakHasCompleteDescription()
    {
        foreach (var t in RealTweaks())
        {
            var info = t.Info;
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Id}: Description vazia");
            Assert.False(string.IsNullOrWhiteSpace(info.LongDescription), $"{t.Id}: LongDescription vazia");
            Assert.False(string.IsNullOrWhiteSpace(info.WhatItChanges), $"{t.Id}: WhatItChanges vazio");
            Assert.False(string.IsNullOrWhiteSpace(info.Benefit), $"{t.Id}: Benefit vazio");
            Assert.False(string.IsNullOrWhiteSpace(info.RiskDetails), $"{t.Id}: RiskDetails vazio");
            Assert.False(string.IsNullOrWhiteSpace(info.HowToRevert), $"{t.Id}: HowToRevert vazio");
            Assert.True(info.Sections.Count >= 5, $"{t.Id}: seções insuficientes");
        }
    }

    [Fact]
    public void CatalogHasNoOrphanEntries()
    {
        var ids = RealTweaks().Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(TweakInfoCatalog.KnownIds, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void RegistryTweaksListTheExactValuesTheyWrite()
    {
        var gameMode = RealTweaks().First(t => t.Id == "gaming.game-mode");
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games\Priority = 6", gameMode.Info.WhatItChanges);
        Assert.Contains(@"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 1", gameMode.Info.WhatItChanges);
    }

    [Fact]
    public void EmptyNotesAreNotShownAsSection()
    {
        var info = new TweakInfo("a", "b", "c", "d", "e", "");
        Assert.Equal(5, info.Sections.Count);
        Assert.DoesNotContain(info.Sections, s => s.Header == "Observações");
    }

    [Fact]
    public void UnknownTweakGetsEmptyInfoWithoutThrowing() =>
        Assert.Empty(new FakeTweak("desconhecido", 1).As<ITweak>().Info.Sections);

    [Fact]
    public void AllScreenHelpTextsAreFilled()
    {
        var fields = typeof(UiHelp).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToList();
        Assert.True(fields.Count >= 20);
        Assert.All(fields, f => Assert.True(((string)f.GetRawConstantValue()!).Length > 40, $"{f.Name} curto demais"));
    }
}

internal static class CastExtensions
{
    public static T As<T>(this object o) => (T)o;
}
