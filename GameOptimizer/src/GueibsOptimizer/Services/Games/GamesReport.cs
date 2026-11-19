using System.IO;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Games;

/// <summary>Seção "Meus Jogos" do relatório --dry-run: jogos encontrados e o que "Otimizar para o meu PC" mudaria.</summary>
public static class GamesReport
{
    public static async Task WriteAsync(StringWriter writer, GameLibraryService library, GameProfileService profiles, PcTier tier = PcTier.Medio)
    {
        writer.WriteLine();
        writer.WriteLine("== Meus Jogos ==");
        IReadOnlyList<GameEntry> games;
        try
        {
            games = await library.ScanAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Não foi possível procurar jogos: {ex.Message}");
            return;
        }
        if (games.Count == 0) writer.WriteLine("Nenhum jogo encontrado.");
        foreach (var game in games)
        {
            writer.WriteLine($"- {game.Name} [{game.SourceText}] {game.Executable ?? "(executável não encontrado)"}");
            if (game.Executable is null) continue;
            foreach (var info in GameProfileService.Options)
            {
                var applied = profiles.IsApplied(game, info.Option);
                var action = applied ? "já aplicado" : info.InDefault ? "seria aplicado por \"Otimizar para o meu PC\"" : "opcional (não aplicado)";
                writer.WriteLine($"    {info.Name}: {action}");
            }
            if (GamePresetService.ForGameOrDetect(game) is { } preset)
                writer.WriteLine($"    Preset: {(preset.CanAutoApply ? "atual × recomendado, aplica o que você escolher (com backup)" : "apenas recomendação")}");
        }
        writer.WriteLine();
        writer.WriteLine($"Presets para o nível {tier}:");
        foreach (var preset in GamePresetService.All)
            writer.WriteLine($"- {preset.Name}: {(preset.CanAutoApply ? "aplicável (" + preset.ConfigFile!.ResolvePath() + ", com .bak)" : "apenas recomendação")}");
    }
}
