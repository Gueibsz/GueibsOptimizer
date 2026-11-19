using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class DebloatCatalogTests
{
    private static IReadOnlyList<DebloatCatalogEntry> Catalog => DebloatService.Catalog;

    [Fact]
    public void EmbeddedCatalogHasAllThreeCategories()
    {
        Assert.Contains(Catalog, e => e.Category == DebloatCategory.Safe);
        Assert.Contains(Catalog, e => e.Category == DebloatCategory.Optional);
        Assert.Contains(Catalog, e => e.Category == DebloatCategory.Protected);
        Assert.Equal(Catalog.Count, Catalog.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Catalog, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason), e.Id));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.GamingServices")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.HEIFImageExtension")]
    [InlineData("Microsoft.VP9VideoExtensions")]
    public void EssentialPackagesAreProtected(string packageName) =>
        Assert.Equal(DebloatCategory.Protected, DebloatRules.Match(Catalog, packageName)?.Category);

    [Theory]
    [InlineData("Clipchamp.Clipchamp", DebloatCategory.Safe)]
    [InlineData("Microsoft.BingWeather", DebloatCategory.Safe)]
    [InlineData("Microsoft.GamingApp", DebloatCategory.Optional)]
    [InlineData("MicrosoftWindows.Client.WebExperience", DebloatCategory.Optional)]
    public void ClassifiesKnownApps(string packageName, DebloatCategory expected) =>
        Assert.Equal(expected, DebloatRules.Match(Catalog, packageName)?.Category);

    [Fact]
    public void ProtectedRuleWinsOverOtherMatches()
    {
        var catalog = new[]
        {
            new DebloatCatalogEntry("Vendor.*", "Tudo", DebloatCategory.Safe, "x", ""),
            new DebloatCatalogEntry("Vendor.Core", "Núcleo", DebloatCategory.Protected, "x", "")
        };
        Assert.Equal(DebloatCategory.Protected, DebloatRules.Match(catalog, "Vendor.Core")!.Category);
        Assert.Equal(DebloatCategory.Safe, DebloatRules.Match(catalog, "Vendor.Game")!.Category);
    }

    [Fact]
    public void StoreIdsAreValidAndProtectedHaveNone()
    {
        Assert.All(Catalog.Where(e => e.StoreId.Length > 0), e => Assert.True(CatalogParser.IsValidWingetId(e.StoreId), e.Id));
        Assert.All(Catalog.Where(e => e.Category == DebloatCategory.Protected), e => Assert.Equal(string.Empty, e.StoreId));
    }
}

public class DebloatPlanTests
{
    private static DebloatItem Item(string name, bool nonRemovable = false, bool framework = false) =>
        DebloatRules.Classify(DebloatService.Catalog, [new InstalledPackage(name, name + "_1.0_x64__8wekyb3d8bbwe", "CN=Microsoft", nonRemovable, framework, 1024)],
            new Dictionary<string, string>()).Single();

    private static readonly DebloatItem Store = Item("Microsoft.WindowsStore");
    private static readonly DebloatItem Calculator = Item("Microsoft.WindowsCalculator");
    private static readonly DebloatItem Weather = Item("Microsoft.BingWeather");
    private static readonly DebloatItem Clipchamp = Item("Clipchamp.Clipchamp");
    private static readonly DebloatItem Maps = Item("Microsoft.WindowsMaps");
    private static readonly DebloatItem Xbox = Item("Microsoft.GamingApp");
    private static readonly DebloatItem Unknown = Item("Fabricante.AppDesconhecido");
    private static readonly DebloatItem SafeButSystem = Item("Microsoft.BingNews", nonRemovable: true);
    private static readonly DebloatItem[] All = [Store, Calculator, Weather, Clipchamp, Maps, Xbox, Unknown, SafeButSystem];

    [Fact]
    public void ProtectedAndUnclassifiedNeverEnterTheRemovalList()
    {
        var plan = DebloatRules.BuildPlan(All, includeProvisioned: true);
        Assert.DoesNotContain(Store, plan.ToRemove);
        Assert.DoesNotContain(Calculator, plan.ToRemove);
        Assert.DoesNotContain(Unknown, plan.ToRemove);
        Assert.DoesNotContain(SafeButSystem, plan.ToRemove); // Windows marca como não removível
        Assert.Contains(Store, plan.Blocked);
        Assert.All(plan.ToRemove, i => Assert.True(i.IsRemovable));
    }

    [Theory]
    [InlineData(DebloatPreset.Leve)]
    [InlineData(DebloatPreset.Equilibrado)]
    [InlineData(DebloatPreset.Agressivo)]
    public void NoPresetEverSelectsProtectedItems(DebloatPreset preset) =>
        Assert.All(DebloatRules.PresetSelection(preset, All), i => Assert.True(i.IsRemovable, i.DisplayName));

    [Fact]
    public void PresetsAreNested()
    {
        var light = DebloatRules.PresetSelection(DebloatPreset.Leve, All);
        var balanced = DebloatRules.PresetSelection(DebloatPreset.Equilibrado, All);
        var aggressive = DebloatRules.PresetSelection(DebloatPreset.Agressivo, All);
        Assert.Equal([Weather, Clipchamp], light.OrderBy(i => i.DisplayName).Reverse().ToList().OrderBy(i => i.DisplayName));
        Assert.Contains(Maps, balanced);
        Assert.DoesNotContain(Maps, light);
        Assert.DoesNotContain(Xbox, balanced);
        Assert.Contains(Xbox, aggressive);
        Assert.True(DebloatRules.BuildPlan(aggressive, false).HasOptional);
        Assert.False(DebloatRules.BuildPlan(balanced, false).HasOptional);
    }

    [Fact]
    public async Task RemoveSkipsProtectedEvenIfPlanIsForced()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var simulated = new List<string>();
        ctx.Logger.EntryLogged += e => { if (e.Level == LogLevel.Simulation) simulated.Add(e.Message); };
        var forced = new DebloatPlan([Store, Weather], [], IncludeProvisioned: false); // plano montado à mão, burlando o BuildPlan

        var results = await new DebloatService(ctx).RemoveAsync(forced);

        Assert.Single(results);
        Assert.Equal(Weather, results[0].Item);
        Assert.DoesNotContain(simulated, s => s.Contains("WindowsStore"));
        Assert.Contains(simulated, s => s.Contains("BingWeather"));
        Assert.Empty(key.Store.All); // simulação não grava backup
    }
}

public class DebloatInventoryTests
{
    [Fact]
    public void ParsesInventoryWithArraysAndProvisioningError()
    {
        const string json = """
            {"packages":[
              {"Name":"Microsoft.BingWeather","PackageFullName":"Microsoft.BingWeather_4.53_x64__8wekyb3d8bbwe","Publisher":"CN=Microsoft","NonRemovable":false,"IsFramework":false,"Size":52428800},
              {"Name":"Microsoft.BingWeather","PackageFullName":"Microsoft.BingWeather_4.53_neutral__8wekyb3d8bbwe","Publisher":"CN=Microsoft","NonRemovable":false,"IsFramework":false,"Size":null},
              {"Name":"Microsoft.VCLibs.140.00","PackageFullName":"x","Publisher":"CN=Microsoft","NonRemovable":false,"IsFramework":true,"Size":null}],
             "provisioned":[],"provisionedError":"Access is denied."}
            """;
        var (packages, provisioned, error) = DebloatRules.ParseInventory(json);
        Assert.Equal(3, packages.Count);
        Assert.Empty(provisioned);
        Assert.Equal("Access is denied.", error);

        var items = DebloatRules.Classify(DebloatService.Catalog, packages, provisioned);
        Assert.Equal(2, items.Count); // pacote repetido por arquitetura vira um item
        Assert.Equal(52428800, items.Single(i => i.Package.Name == "Microsoft.BingWeather").Package.SizeBytes);
        Assert.Equal(DebloatCategory.Protected, items.Single(i => i.Package.IsFramework).Category);
    }

    [Fact]
    public void ParsesSingleObjectsFromConvertToJson()
    {
        const string json = """
            {"packages":{"Name":"Clipchamp.Clipchamp","PackageFullName":"Clipchamp.Clipchamp_3.0_neutral__yxz26nhyzhsrt","Publisher":"CN=Clipchamp","NonRemovable":false,"IsFramework":false,"Size":1048576},
             "provisioned":{"DisplayName":"Clipchamp.Clipchamp","PackageName":"Clipchamp.Clipchamp_3.0_neutral_~_yxz26nhyzhsrt"},"provisionedError":null}
            """;
        var (packages, provisioned, error) = DebloatRules.ParseInventory(json);
        var item = DebloatRules.Classify(DebloatService.Catalog, packages, provisioned).Single();
        Assert.Null(error);
        Assert.Equal("Clipchamp.Clipchamp_3.0_neutral_~_yxz26nhyzhsrt", item.ProvisionedPackageName);
        Assert.Equal("1 MB", item.SizeText);
    }
}

public class DebloatExtrasTests
{
    [Fact]
    public void PreferenceTweaksStayOutOfBoostAndScore()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        foreach (var id in new[] { "windows.classic-context-menu", "windows.onedrive-startup-off" })
        {
            var t = registry.Find(id)!;
            Assert.False(t.InBoost);
            Assert.Equal(0, t.Points);
            Assert.False(TweakText.IsRecommended(t));
        }
        foreach (var id in new[] { "privacy.start-lockscreen-ads-off", "windows.widgets-off", "windows.bing-search-off" })
            Assert.True(TweakText.IsRecommended(registry.Find(id)!));
        Assert.Equal(100, registry.All.Sum(registry.PointsOf));
    }
}
