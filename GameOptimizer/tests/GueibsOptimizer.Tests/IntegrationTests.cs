using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

/// <summary>
/// Testes que usam a internet e baixam o PresentMon oficial. Só rodam com GO_INTEGRATION=1
/// (fora disso retornam sem fazer nada), para o "dotnet test" normal continuar offline.
/// Marcados com Category=RequiresHardware: o CI os exclui com --filter (precisam de internet,
/// jogos instalados, PresentMon e um programa real em primeiro plano).
/// </summary>
[Trait("Category", "RequiresHardware")]
public class IntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("GO_INTEGRATION") == "1";

    [Fact]
    public async Task DownloadsOfficialPresentMonAndDetectsVersion()
    {
        if (!Enabled) return;

        var settingsPath = Path.Combine(Path.GetTempPath(), $"go-settings-{Guid.NewGuid():N}.json");
        var settings = new AppSettings();
        var logger = new Logger(null);
        var lines = new List<string>();
        logger.EntryLogged += e => lines.Add(e.ToString());

        var path = await new PresentMonDownloader(logger, settings, dryRun: false).DownloadAsync(null);

        Assert.True(File.Exists(path), path);
        Assert.True(Authenticode.IsTrusted(path));
        Assert.Contains("O=Intel Corporation", Authenticode.SignerName(path));

        var fps = new FpsService(logger, settings);
        var version = await fps.DetectVersionAsync(path);
        Assert.NotNull(version);
        Assert.True(version!.Major >= 2, version.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "go-presentmon-result.txt"),
            $"path={path}\nversion={version}\nsigner={Authenticode.SignerName(path)}\n" + string.Join("\n", lines));
    }

    /// <summary>Lista os apps Appx reais (só leitura) e grava um resumo da classificação.</summary>
    [Fact]
    public async Task ListsAndClassifiesInstalledApps()
    {
        if (!Enabled) return;
        using var key = new TestRegistryKey();
        var service = new DebloatService(key.CreateContext(dryRun: true));
        var items = await service.ListAsync();
        Assert.NotEmpty(items);
        var lines = items.Select(i => $"{i.CategoryText,-24} {i.DisplayName} [{i.Package.Name}] {i.SizeText}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "go-debloat-result.txt"),
            $"provisionados: {service.ProvisionedError ?? "ok"}\n" + string.Join("\n", lines));
        Assert.DoesNotContain(items, i => i.IsRemovable && (i.Package.NonRemovable || i.Package.IsFramework));
    }

    /// <summary>Detecta jogos reais (Steam/Epic/launchers) e grava nome, origem e .exe escolhido.</summary>
    [Fact]
    public async Task ScansRealGameLibrary()
    {
        if (!Enabled) return;
        var path = Path.Combine(Path.GetTempPath(), $"go-lib-{Guid.NewGuid():N}.json");
        var games = await new GueibsOptimizer.Services.Games.GameLibraryService(new Logger(null), path).ScanAsync();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "go-games-result.txt"),
            string.Join("\n", games.Select(g => $"{g.SourceText,-11} {g.Name} -> {g.Executable ?? "(sem exe)"}")));
    }

    /// <summary>Lê (sem gravar) os arquivos de opções reais dos jogos e grava a tabela atual × recomendado.</summary>
    [Fact]
    public void ReadsRealGameConfigFiles()
    {
        if (!Enabled) return;
        var lines = new List<string>();
        foreach (var preset in GueibsOptimizer.Services.Games.GamePresetService.All.Where(p => p.CanAutoApply))
        {
            var path = preset.ConfigFile!.ResolvePath();
            if (!File.Exists(path)) { lines.Add($"{preset.Name}: (arquivo não existe) {path}"); continue; }
            var before = File.GetLastWriteTimeUtc(path);
            var states = GueibsOptimizer.Services.Games.GamePresetService.ReadStates(preset, GueibsOptimizer.Models.PcTier.Medio, path);
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
            lines.Add($"{preset.Name}: {path}");
            lines.AddRange(states.Select(s => $"    {s.Setting.Label,-28} atual={s.Setting.Display(s.Current),-14} recomendado={s.Setting.Display(s.Recommended)}{(s.IsRecommended ? "  ok" : "")}"));
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "go-gamecfg-result.txt"), string.Join("\n", lines));
    }

    /// <summary>Para cada jogo real: preset próprio, detecção automática (Unreal) ou nada. Só leitura.</summary>
    [Fact]
    public async Task DetectsGraphicsOptionsForRealGames()
    {
        if (!Enabled) return;
        var list = Path.Combine(Path.GetTempPath(), $"go-lib-{Guid.NewGuid():N}.json");
        var games = await new GueibsOptimizer.Services.Games.GameLibraryService(new Logger(null), list).ScanAsync();
        var lines = games.Select(g =>
        {
            var p = GueibsOptimizer.Services.Games.GamePresetService.ForGameOrDetect(g);
            var kind = p is null ? "sem opções detectáveis" : p.CanAutoApply ? $"{p.Id}: {p.ConfigFile!.Settings.Count(s => !s.Hidden)} opções ({p.ConfigFile.ResolvePath(g.Folder)})" : $"{p.Id}: só recomendação";
            if (p is { NeverLowerUnlessEntrada: true } && File.Exists(p.ConfigFile!.ResolvePath()))
                kind += "\n" + string.Join("\n", GueibsOptimizer.Services.Games.GamePresetService.ReadStates(p, GueibsOptimizer.Models.PcTier.Medio, p.ConfigFile.ResolvePath())
                    .Select(s => $"      {s.Setting.Label,-26} atual={s.Setting.Display(s.Current),-10} recomendado={s.Setting.Display(s.Recommended)}{(s.IsRecommended ? "  ok" : "")}"));
            return $"{g.Name,-32} {kind}";
        });
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "go-detect-result.txt"), string.Join("\n", lines));
    }

    /// <summary>Usa o PresentMon já baixado para medir o programa em primeiro plano por alguns segundos.</summary>
    [Fact]
    public async Task CapturesForegroundApplication()
    {
        if (!Enabled) return;

        var logger = new Logger(null);
        var lines = new List<string>();
        logger.EntryLogged += e => lines.Add(e.ToString());
        using var fps = new FpsService(logger, new AppSettings());
        Assert.NotNull(fps.Locate());
        Assert.True(await fps.StartAsync());

        await Task.Delay(TimeSpan.FromSeconds(8));
        var s = fps.Snapshot();
        var target = fps.TargetName;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "go-fps-result.txt"),
            $"exe={fps.ExecutablePath}\nalvo={target}\nstatus={fps.Status}\nframes={s.Frames}\n" +
            $"media={s.AverageFps:0.0}\nlow1={s.Low1Fps:0.0}\nlow01={s.Low01Fps:0.0}\n" + string.Join("\n", lines));
        fps.Stop();
        Assert.True(s.Frames > 0, $"Nenhum quadro capturado de {target}");
    }
}
