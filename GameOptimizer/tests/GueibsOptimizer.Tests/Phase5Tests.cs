using System.IO;
using GueibsOptimizer.Services;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Tests;

public class CatalogTests
{
    [Fact]
    public void EmbeddedCatalogHasAtLeast230ValidatedApps()
    {
        var apps = CatalogService.Apps;
        Assert.True(apps.Count >= 230, $"Catálogo com {apps.Count} itens");
        Assert.Equal(apps.Count, apps.Select(a => a.WingetId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(apps, a =>
        {
            Assert.True(CatalogParser.IsValidWingetId(a.WingetId), a.WingetId);
            Assert.Contains(a.Category, CatalogParser.Categories);
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
        });
        Assert.All(CatalogParser.Categories, c => Assert.Contains(apps, a => a.Category == c));
    }

    [Fact]
    public void GamingEssentialsAreInTheCatalog() =>
        Assert.All(CatalogService.GamingEssentials, id => Assert.Contains(CatalogService.Apps, a => a.WingetId == id));

    [Fact]
    public void ParserSkipsInvalidAndDuplicateItems()
    {
        const string json = """
            {"apps":[
              {"name":"A","wingetId":"Vendor.App","category":"Utilitários","description":"x"},
              {"name":"Dup","wingetId":"vendor.app","category":"Utilitários","description":"x"},
              {"name":"Injeção","wingetId":"Vendor.App & calc","category":"Utilitários"},
              {"name":"","wingetId":"Vendor.Other","category":"Mídia"},
              {"name":"Sem categoria","wingetId":"Vendor.Third"}
            ]}
            """;
        var apps = CatalogParser.Parse(json);
        Assert.Equal("Vendor.App", Assert.Single(apps).WingetId);
    }

    [Fact]
    public void ParserRejectsCatalogWithoutApps() =>
        Assert.Throws<InvalidDataException>(() => CatalogParser.Parse("""{"programas":[]}"""));

    [Theory]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("Zen-Team.Zen-Browser", true)]
    [InlineData("a b", false)]
    [InlineData("x;rm", false)]
    [InlineData("\"quoted\"", false)]
    [InlineData("--source", false)]
    public void ValidatesWingetIds(string id, bool expected) => Assert.Equal(expected, CatalogParser.IsValidWingetId(id));
}

public class WingetTests
{
    [Theory]
    [InlineData(0, "Successfully installed", InstallOutcome.Installed)]
    [InlineData(0, "Found an existing package already installed.", InstallOutcome.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A15002B), "", InstallOutcome.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150061), "", InstallOutcome.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150109), "", InstallOutcome.RebootRequired)]
    [InlineData(3010, "", InstallOutcome.RebootRequired)]
    [InlineData(unchecked((int)0x8A150014), "", InstallOutcome.Failed)]
    [InlineData(unchecked((int)0x8A150107), "", InstallOutcome.Failed)]
    [InlineData(1603, "", InstallOutcome.Failed)]
    public void ClassifiesExitCodes(int code, string output, InstallOutcome expected) =>
        Assert.Equal(expected, WingetService.Classify("x", code, output).Outcome);

    [Fact]
    public void FailureMessagesArePortuguese() =>
        Assert.Contains("internet", WingetService.Classify("x", unchecked((int)0x8A150107), "").Message);

    [Fact]
    public void ParsesEnglishUpgradeTable()
    {
        const string output = """
               - \ |
            Name                         Id                       Version      Available    Source
            ---------------------------------------------------------------------------------------
            Mozilla Firefox (x64 en-US)  Mozilla.Firefox          120.0        121.0        winget
            7-Zip 23.01 (x64)            7zip.7zip                23.01        24.08        winget
            2 upgrades available.
            """;
        var list = WingetService.ParseUpgradeTable(output);
        Assert.Equal(2, list.Count);
        Assert.Equal("Mozilla.Firefox", list[0].Id);
        Assert.Equal("121.0", list[0].Available);
        Assert.Equal("7-Zip 23.01 (x64)", list[1].Name);
    }

    [Fact]
    public void ParsesPortugueseUpgradeTable()
    {
        const string output = """
            Nome                Id                  Versão   Disponível Origem
            ------------------------------------------------------------------
            Discord             Discord.Discord     1.0.9    1.0.10     winget
            1 atualizações disponíveis.
            """;
        var u = Assert.Single(WingetService.ParseUpgradeTable(output));
        Assert.Equal("Discord.Discord", u.Id);
        Assert.Equal("1.0.10", u.Available);
    }

    [Fact]
    public void EmptyOrMessageOnlyOutputHasNoUpgrades()
    {
        Assert.Empty(WingetService.ParseUpgradeTable("No installed package found matching input criteria."));
        Assert.Empty(WingetService.ParseUpgradeTable(""));
    }

    [Fact]
    public void ReportCountsOutcomes()
    {
        var report = InstallAppsViewModel.BuildReport(
        [
            new InstallResult("a", InstallOutcome.Installed, 0, "ok"),
            new InstallResult("b", InstallOutcome.AlreadyInstalled, 0, "já"),
            new InstallResult("c", InstallOutcome.Failed, 1, "Sem conexão com a internet.")
        ]);
        Assert.Contains("1 instalados", report);
        Assert.Contains("1 falharam", report);
        Assert.Contains("c: Sem conexão", report);
    }
}

public class DefenderTests
{
    [Fact]
    public void RejectsDriveRootsSystemFoldersAndWildcards()
    {
        Assert.False(DefenderService.IsSafeExclusion(@"C:\"));
        Assert.False(DefenderService.IsSafeExclusion(@"D:"));
        Assert.False(DefenderService.IsSafeExclusion(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(DefenderService.IsSafeExclusion(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(DefenderService.IsSafeExclusion(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(DefenderService.IsSafeExclusion(@"C:\Jogos\*"));
        Assert.False(DefenderService.IsSafeExclusion(""));
    }

    [Fact]
    public void RejectsRelativePathsWhateverTheCurrentFolder()
    {
        // "X:" sem barra é relativo à pasta atual do drive; no CI o código fica em D:\, então "D:"
        // virava uma pasta comum. Nenhum caminho relativo pode ser aceito.
        string drive = Path.GetPathRoot(Environment.CurrentDirectory)!.TrimEnd('\\');
        Assert.False(DefenderService.IsSafeExclusion(drive));
        Assert.False(DefenderService.IsSafeExclusion(@"pasta\sub"));
        Assert.False(DefenderService.IsSafeExclusion(@"\Jogos"));
        Assert.True(DefenderService.IsSafeExclusion(@"C:\Jogos\Gueibs Optimizer"));
    }

    [Fact]
    public void AcceptsADedicatedAppFolder()
    {
        Assert.True(DefenderService.IsSafeExclusion(@"C:\Ferramentas\GueibsOptimizer"));
        Assert.True(DefenderService.IsSafeExclusion(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GueibsOptimizer")));
    }

    [Fact]
    public void ParsesStatus()
    {
        var active = DefenderService.ParseStatus("MODE=Normal\nAV=True\nPOLICY=False\nEXCL=True");
        Assert.True(active.Available);
        Assert.False(active.ManagedByPolicyOrThirdParty);
        Assert.True(active.ExclusionPresent);

        var passive = DefenderService.ParseStatus("MODE=Passive Mode\nAV=True\nPOLICY=False\nEXCL=False");
        Assert.True(passive.ManagedByPolicyOrThirdParty);

        Assert.False(DefenderService.ParseStatus("NODEFENDER").Available);
    }

    [Fact]
    public async Task DryRunExclusionChangesNothing()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var service = new DefenderService(ctx);
        // Só funciona se a pasta de testes for "segura"; em qualquer caso, nada é gravado no backup.
        await service.AddExclusionAsync();
        Assert.Empty(key.Store.All);
    }
}

public class WizardStateTests
{
    [Fact]
    public void SavesLoadsAndTracksPendingApps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-wizard-{Guid.NewGuid():N}.json");
        try
        {
            var s = new WizardState { Step = WizardState.StepInstall, ProfileName = "Competitivo", Apps = ["A.A", "B.B", "C.C"] };
            s.InstalledApps.Add("b.b");
            s.Save(path);

            var loaded = WizardState.Load(path)!;
            Assert.Equal(WizardState.StepInstall, loaded.Step);
            Assert.Equal(["A.A", "C.C"], loaded.PendingApps);
            Assert.Equal("3. Instalar programas", loaded.StepTitle);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptOrInvalidStateIsIgnored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-wizard-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ quebrado");
            Assert.Null(WizardState.Load(path));
            File.WriteAllText(path, """{"Step":99}""");
            Assert.Null(WizardState.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
