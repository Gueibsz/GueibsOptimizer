using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class ElevationTests
{
    [Theory]
    [InlineData(true, false, false, StartupDecision.Continue)]   // já é admin
    [InlineData(true, false, true, StartupDecision.Continue)]
    [InlineData(false, true, false, StartupDecision.Continue)]   // simulação não precisa de admin
    [InlineData(false, false, false, StartupDecision.Relaunch)]  // 1ª vez sem admin: pede elevação
    [InlineData(false, false, true, StartupDecision.ShowError)]  // já tentou: não entra em ciclo
    public void DecidesWithoutInfiniteLoop(bool isAdmin, bool dryRun, bool triedBefore, StartupDecision expected) =>
        Assert.Equal(expected, Elevation.Decide(isAdmin, dryRun, triedBefore));

    [Fact]
    public void RelaunchKeepsArgumentsAndAddsElevatedOnce()
    {
        var args = Elevation.RelaunchArguments(["--autostart", "--ELEVATED", "--dry-run"]);
        Assert.Equal(["--autostart", "--dry-run", "--elevated"], args);
        Assert.Equal(1, args.Count(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ElevatedFlagIsParsedAndNotUnknown()
    {
        var o = CommandLineOptions.Parse(["--elevated", "--autostart"]);
        Assert.True(o.Elevated);
        Assert.True(o.AutoStart);
        Assert.Empty(o.Unknown);
    }

    [Fact]
    public void SecondAttemptAfterRelaunchNeverRelaunchesAgain()
    {
        // Simula a cadeia: 1ª instância sem admin → reinicia com --elevated → se ainda sem admin, erro.
        var first = CommandLineOptions.Parse([]);
        Assert.Equal(StartupDecision.Relaunch, Elevation.Decide(false, first.DryRun, first.Elevated));
        var second = CommandLineOptions.Parse(Elevation.RelaunchArguments([]));
        Assert.Equal(StartupDecision.ShowError, Elevation.Decide(false, second.DryRun, second.Elevated));
    }
}

/// <summary>2.2: não existe mais "Iniciar com o Windows"; o app só abre manualmente e sempre como administrador.</summary>
public class NoAutoStartTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void ServiceCanOnlyRemoveTasksNeverCreate()
    {
        var methods = typeof(AutoStartService).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain("Enable", methods);
        Assert.Contains("Disable", methods);
        Assert.Equal(["GueibsOptimizerAutoStart", "GameOptimizerAutoStart"], AutoStartService.TaskNames);
    }

    [Fact]
    public void DisableInDryRunChangesNothing()
    {
        var before = AutoStartService.TaskNames.Select(n => TaskSchedulerCom.IsEnabled("\\", n)).ToList();
        Assert.Null(new AutoStartService(new Logger(null), dryRun: true).Disable());
        Assert.Equal(before, AutoStartService.TaskNames.Select(n => TaskSchedulerCom.IsEnabled("\\", n)).ToList());
    }

    [Fact]
    public void AutostartOnCommandNoLongerExists() =>
        Assert.Equal(SetupCommand.None, CommandLineOptions.Parse(["--autostart-on"]).Setup);

    [Fact]
    public void InstallerAndSettingsHaveNoAutoStartOption()
    {
        string iss = File.ReadAllText(RepoFile("installer", "GueibsOptimizer.iss"));
        Assert.DoesNotContain("Name: \"autostart\"", iss);
        Assert.DoesNotContain("--autostart-on", iss);
        string settings = File.ReadAllText(RepoFile("src", "GueibsOptimizer", "Views", "SettingsView.xaml"));
        Assert.DoesNotContain("IsAutoStartEnabled", settings);
    }

    [Fact]
    public void ManifestAlwaysRequiresAdministrator() =>
        Assert.Contains("level=\"requireAdministrator\"", File.ReadAllText(RepoFile("src", "GueibsOptimizer", "app.manifest")));
}