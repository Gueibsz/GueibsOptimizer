using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;

namespace GueibsOptimizer.Tests;

/// <summary>Versão 2.1: o app passou de GameOptimizer para Gueibs Optimizer sem perder o que a versão antiga deixou.</summary>
public class RenameMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"go-rename-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string OldDir => Path.Combine(_root, "GameOptimizer");
    private string NewDir => Path.Combine(_root, "GueibsOptimizer");

    [Fact]
    public void DataFolderIsMovedWithBackupAndSettingsPathsFixed()
    {
        Directory.CreateDirectory(Path.Combine(OldDir, "profiles"));
        File.WriteAllText(Path.Combine(OldDir, "backup.json"), "[]");
        File.WriteAllText(Path.Combine(OldDir, "profiles", "meu.gopro"), "{}");
        string pm = Path.Combine(OldDir, "tools", "PresentMon.exe");
        File.WriteAllText(Path.Combine(OldDir, "settings.json"), $$"""{"presentMonPath":{{System.Text.Json.JsonSerializer.Serialize(pm)}}}""");

        Assert.Equal(DataMigrationOutcome.Moved, LegacyMigration.MigrateDataFolder(OldDir, NewDir));

        Assert.False(Directory.Exists(OldDir));
        Assert.True(File.Exists(Path.Combine(NewDir, "backup.json")));
        Assert.True(File.Exists(Path.Combine(NewDir, "profiles", "meu.gopro")));
        string settings = File.ReadAllText(Path.Combine(NewDir, "settings.json"));
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Path.Combine(NewDir, "tools", "PresentMon.exe")).Trim('"'), settings);
        Assert.DoesNotContain(@"\\GameOptimizer\\", settings);
    }

    [Fact]
    public void ExistingNewFolderIsNeverOverwritten()
    {
        Directory.CreateDirectory(OldDir);
        Directory.CreateDirectory(NewDir);
        File.WriteAllText(Path.Combine(OldDir, "backup.json"), "antigo");
        File.WriteAllText(Path.Combine(NewDir, "backup.json"), "novo");

        Assert.Equal(DataMigrationOutcome.BothExist, LegacyMigration.MigrateDataFolder(OldDir, NewDir));
        Assert.Equal("novo", File.ReadAllText(Path.Combine(NewDir, "backup.json")));
        Assert.True(Directory.Exists(OldDir));
    }

    [Fact]
    public void LockedFileFallsBackToCopyAndKeepsOldFolder()
    {
        Directory.CreateDirectory(Path.Combine(OldDir, "logs"));
        File.WriteAllText(Path.Combine(OldDir, "backup.json"), "[]");
        using var locked = new FileStream(Path.Combine(OldDir, "logs", "app.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(DataMigrationOutcome.Copied, LegacyMigration.MigrateDataFolder(OldDir, NewDir));
        Assert.True(File.Exists(Path.Combine(NewDir, "backup.json")));
        Assert.True(Directory.Exists(OldDir));
    }

    [Fact]
    public void NoOldFolderMeansNothingToDo() =>
        Assert.Equal(DataMigrationOutcome.NothingToDo, LegacyMigration.MigrateDataFolder(OldDir, NewDir));

    [Fact]
    public void LegacyPowerPlanIsRecognizedAndNewNameWins()
    {
        var legacyOnly = PowerCfgParser.ParseList("Power Scheme GUID: 11111111-2222-3333-4444-555555555555  (GameOptimizer Performance) *");
        Assert.Equal("11111111-2222-3333-4444-555555555555", PowerPlanTweak.FindOurPlan(legacyOnly));
        Assert.Equal("11111111-2222-3333-4444-555555555555", PowerPlanTweak.FindLegacyPlan(legacyOnly));
        Assert.True(PowerPlanTweak.IsOurPlanName("GameOptimizer Performance"));

        var both = PowerCfgParser.ParseList(
            "Power Scheme GUID: 11111111-2222-3333-4444-555555555555  (GameOptimizer Performance)\n" +
            "Power Scheme GUID: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee  (Gueibs Optimizer Performance) *");
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", PowerPlanTweak.FindOurPlan(both));
        Assert.Null(PowerPlanTweak.FindLegacyPlan(both)); // nunca renomeia para um nome que já existe
        Assert.False(PowerPlanTweak.IsOurPlanName("Equilibrado"));
    }

    [Fact]
    public void ProfilesExportedByOldVersionStillImport()
    {
        var service = new ProfileService(new TweakRegistry([new FakeTweak("a", 100)]), new Logger(null), Path.Combine(_root, "p"));
        var v = service.ValidateJson("""{"format":"GameOptimizer.Profile","version":1,"name":"Antigo","tweaks":["a"]}""");
        Assert.True(v.IsValid);
        Assert.Equal(Profile.FormatId, v.Profile!.Format);
    }

    [Fact]
    public void MigrateLegacyCommandIsParsed() =>
        Assert.Equal(SetupCommand.MigrateLegacy, CommandLineOptions.Parse(["--migrate-legacy"]).Setup);

    [Fact]
    public void InstallerUsesNewNamesAndRemovesOldExe()
    {
        string iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "GueibsOptimizer.iss"));
        Assert.Contains("#define AppName \"Gueibs Optimizer\"", iss);
        Assert.Contains("#define AppExe \"GueibsOptimizer.exe\"", iss);
        Assert.Contains("AppId={{7C1E2B54-3F8A-4D6B-9E21-5A0C8D4F6B13}", iss); // mesmo AppId: atualiza a instalação antiga
        Assert.Contains("Name: \"{app}\\GameOptimizer.exe\"", iss);
        Assert.Contains("--migrate-legacy", iss);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
