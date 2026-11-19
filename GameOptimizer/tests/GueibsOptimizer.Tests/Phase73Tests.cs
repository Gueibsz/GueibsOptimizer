using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class SetupCommandTests
{
    [Theory]
    [InlineData("--autostart-off", SetupCommand.AutoStartOff)]
    [InlineData("--defender-add", SetupCommand.DefenderAdd)]
    [InlineData("--UNINSTALL-CLEANUP", SetupCommand.UninstallCleanup)]
    public void ParsesInstallerCommands(string arg, SetupCommand expected) =>
        Assert.Equal(expected, CommandLineOptions.Parse([arg]).Setup);

    [Fact]
    public void AutostartFlagIsSeparateFromSetupCommands()
    {
        var o = CommandLineOptions.Parse(["--autostart"]);
        Assert.True(o.AutoStart);
        Assert.Equal(SetupCommand.None, o.Setup);
        Assert.Empty(o.Unknown);
    }
}

/// <summary>Checagens estruturais do script do instalador.</summary>
public class InstallerScriptTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static string Iss => File.ReadAllText(RepoFile("installer", "GueibsOptimizer.iss"));

    [Fact]
    public void RequiresAdminAndInstallsToProgramFiles()
    {
        Assert.Contains("PrivilegesRequired=admin", Iss);
        Assert.Contains(@"DefaultDirName={autopf}\{#AppName}", Iss);
        Assert.Contains("OutputBaseFilename=GueibsOptimizer-Setup-{#AppVersion}", Iss);
        Assert.Contains("BrazilianPortuguese.isl", Iss);
    }

    [Fact]
    public void OptionalTasksAreUncheckedByDefault()
    {
        var tasks = Iss.Split('\n').Where(l => l.StartsWith("Name: \"", StringComparison.Ordinal) && l.Contains("GroupDescription")).ToList();
        foreach (var name in new[] { "defender" })
            Assert.Contains(tasks, l => l.Contains($"\"{name}\"") && l.Contains("Flags: unchecked"));
    }

    [Fact]
    public void ShortcutsAreOptionalAndThereIsNoSimulationShortcut()
    {
        var lines = Iss.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains(lines, l => l.StartsWith("Name: \"desktopicon\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Name: \"startmenuicon\"", StringComparison.Ordinal));
        var icons = lines.SkipWhile(l => l != "[Icons]").Skip(1).TakeWhile(l => !l.StartsWith('[')).Where(l => l.StartsWith("Name:")).ToList();
        Assert.NotEmpty(icons);
        Assert.All(icons, l => Assert.Matches(@"Tasks: (desktopicon|startmenuicon)\s*$", l));
        Assert.DoesNotContain(icons, l => l.Contains("--dry-run"));
        Assert.Contains(@"DefaultDirName={autopf}\{#AppName}", Iss);
    }

    [Fact]
    public void UninstallRemovesTaskAndExclusionAndAsksAboutData()
    {
        Assert.Contains("--uninstall-cleanup", Iss);
        Assert.Contains("usPostUninstall", Iss);
        Assert.Contains("{userappdata}\\GueibsOptimizer", Iss);
        Assert.Contains("UninstallSilent()", Iss);
    }

    // O app exige administrador no manifesto. Sem shellexec, o Inno Setup abre o exe com CreateProcess como o usuário
    // original (sem elevação) e o Windows recusa com o erro 740.
    [Fact]
    public void OpenAfterInstallGoesThroughShellExecuteSoUacCanElevate()
    {
        var line = Iss.Split('\n').Single(l => l.Contains("Flags: postinstall", StringComparison.Ordinal));
        Assert.Contains("shellexec", line);
    }

    [Fact]
    public void TasksCallTheAppCommandsThatExist()
    {
        foreach (var arg in new[] { "--defender-add", "--uninstall-cleanup", "--migrate-legacy" })
        {
            Assert.Contains(arg, Iss);
            Assert.NotEqual(SetupCommand.None, CommandLineOptions.Parse([arg]).Setup);
        }
    }

    [Fact]
    public void FilesReferencedByTheScriptExist()
    {
        Assert.True(File.Exists(RepoFile("src", "GueibsOptimizer", "Resources", "app.ico")));
        Assert.True(File.Exists(RepoFile("package", "LEIA-ME.txt")));
        Assert.True(File.Exists(RepoFile("package", "THIRD-PARTY-NOTICES.txt")));
        Assert.True(File.Exists(RepoFile("tools", "build-installer.ps1")));
    }
}
