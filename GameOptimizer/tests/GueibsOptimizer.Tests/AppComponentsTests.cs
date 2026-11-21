using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

/// <summary>Versão 2.5: componentes do app sempre baixados pelo instalador (PawnIO e PresentMon).</summary>
public class AppComponentsTests
{
    [Fact]
    public void CommandLineAcceptsOnlyKnownComponents()
    {
        var o = CommandLineOptions.Parse(["--install-components", "PawnIO,presentmon,calc.exe,pawnio"]);
        Assert.Equal(SetupCommand.InstallComponents, o.Setup);
        Assert.Equal(["pawnio", "presentmon"], o.Components);
        Assert.Empty(CommandLineOptions.Parse(["--install-components"]).Components);
        Assert.Empty(AppComponentsInstaller.ParseList("&& del c:\\"));
    }

    [Fact]
    public async Task PresentMonAlreadyPresentIsNotDownloadedAgain()
    {
        var logger = new Logger(null);
        var shell = new Shell(logger, dryRun: true);
        var installer = new AppComponentsInstaller(new WingetService(shell, logger),
            new PresentMonDownloader(logger, new AppSettings(), dryRun: true), () => @"C:\PresentMon\PresentMon-2.3.0-x64.exe", logger);
        Assert.Equal(0, await installer.InstallAsync([AppComponentsInstaller.PresentMon]));
        Assert.Equal(0, await installer.InstallAsync(["desconhecido"]));
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void InstallerAlwaysInstallsBothComponents()
    {
        var iss = File.ReadAllText(RepoFile("installer", "GueibsOptimizer.iss"));
        // Obrigatórios: sem caixa de seleção (Tasks) para desmarcar.
        Assert.Contains("--install-components pawnio\"; Flags:", iss);
        Assert.Contains("--install-components presentmon\"; Flags:", iss);
        Assert.DoesNotContain("Name: \"pawnio\"", iss);
        Assert.DoesNotContain("Name: \"presentmon\"", iss);
        Assert.Contains("ComponentsNote", iss);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GueibsComponentsTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AppComponentsInstaller Installer(string root, bool dryRun) =>
        new(new WingetService(new Shell(new Logger(null), dryRun: true), new Logger(null)),
            new PresentMonDownloader(new Logger(null), new AppSettings(), dryRun: true), () => null, new Logger(null),
            dryRun, Path.Combine(root, "components.json"), Path.Combine(root, "tools", "PresentMon"));

    [Fact]
    public void ComponentsStateRemembersPawnIOInstalledByApp()
    {
        var root = TempDir();
        try
        {
            var file = Path.Combine(root, "sub", "components.json");
            Assert.False(ComponentsState.Load(file).PawnIOInstalledByApp);
            ComponentsState.MarkPawnIOInstalledByApp(file);
            Assert.True(ComponentsState.Load(file).PawnIOInstalledByApp);
            File.WriteAllText(file, "{ não é json");
            Assert.False(ComponentsState.Load(file).PawnIOInstalledByApp);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UninstallRemovesDownloadedPresentMonAndKeepsForeignPawnIO()
    {
        var root = TempDir();
        try
        {
            var pm = Path.Combine(root, "tools", "PresentMon");
            Directory.CreateDirectory(pm);
            File.WriteAllText(Path.Combine(pm, "PresentMon-2.3.0-x64.exe"), "x");

            // Simulação: nada é apagado.
            Assert.Equal(0, await Installer(root, dryRun: true).UninstallAsync());
            Assert.True(Directory.Exists(pm));

            // De verdade (só numa pasta temporária): o PresentMon do app sai; o PawnIO sem marca "do app" fica (sem chamar o winget).
            Assert.Equal(0, await Installer(root, dryRun: false).UninstallAsync());
            Assert.False(Directory.Exists(pm));
            Assert.False(Directory.Exists(Path.Combine(root, "tools")));
            Assert.Equal(0, await Installer(root, dryRun: false).UninstallAsync()); // segunda vez: nada a remover
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstallerRemovesComponentsAndRecognizesInstalledVersion()
    {
        var iss = File.ReadAllText(RepoFile("installer", "GueibsOptimizer.iss"));
        Assert.Contains("--uninstall-cleanup\"; Flags: runhidden waituntilterminated; RunOnceId: \"GueibsOptimizerCleanup\"; StatusMsg:", iss);
        Assert.Contains("PresentMon e o driver PawnIO", iss);
        // Atualizar por cima: o AppId nunca muda e a versão instalada é lida pela chave de desinstalação dele.
        Assert.Contains("AppId={{7C1E2B54-3F8A-4D6B-9E21-5A0C8D4F6B13}", iss);
        Assert.Contains("Uninstall\\{7C1E2B54-3F8A-4D6B-9E21-5A0C8D4F6B13}_is1", iss);
        Assert.Contains("UpgradeNote()", iss);
    }
}
