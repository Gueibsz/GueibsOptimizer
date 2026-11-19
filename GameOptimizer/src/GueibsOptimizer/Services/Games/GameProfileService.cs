using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services.Games;

public enum GameOption
{
    HighPerformanceGpu,
    CpuPriority,
    GameBooster,
    DisableFullscreenOptimizations
}

public sealed record GameOptionInfo(GameOption Option, string Name, string Description, TweakRisk Risk, bool InDefault);

/// <summary>Onde ficam as chaves por jogo. Os testes trocam tudo por uma subchave de teste em HKCU.</summary>
public sealed record GameRegistryPaths(
    RegHive GpuHive, string GpuPreferences,
    RegHive LayersHive, string Layers,
    RegHive IfeoHive, string Ifeo)
{
    public static GameRegistryPaths Windows { get; } = new(
        RegHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences",
        RegHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options");
}

/// <summary>
/// Ajustes por jogo, todos no nível do Windows (nada dentro da pasta ou da memória do jogo) e reversíveis:
/// GPU de alto desempenho, prioridade de CPU Alta via IFEO\PerfOptions (nunca Realtime, nunca "Debugger"),
/// entrada na lista do Game Booster e, opcional, desativar otimizações de tela cheia.
/// Cada opção é um item "game:&lt;exe&gt;:&lt;opção&gt;" no histórico de Restauração.
/// </summary>
public sealed class GameProfileService(TweakContext ctx, GameBoosterService booster, GameRegistryPaths? paths = null) : IUndoProvider
{
    public const string UndoPrefix = "game:";
    public const string FullscreenFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
    /// <summary>3 = Alta. 4 seria Realtime e nunca é usado.</summary>
    public const uint HighPriorityClass = 3;

    private readonly GameRegistryPaths _paths = paths ?? GameRegistryPaths.Windows;

    public static IReadOnlyList<GameOptionInfo> Options { get; } =
    [
        new(GameOption.HighPerformanceGpu, "Placa de vídeo de alto desempenho",
            "Diz ao Windows para rodar o jogo na placa de vídeo dedicada (Configurações > Sistema > Tela > Elementos gráficos). " +
            "Em notebooks com vídeo integrado + dedicado evita que o jogo rode na GPU fraca. Em PCs com uma só placa não muda nada.",
            TweakRisk.Low, true),
        new(GameOption.CpuPriority, "Prioridade de CPU Alta",
            "Grava em Image File Execution Options\\<jogo>.exe\\PerfOptions o valor CpuPriorityClass = 3 (Alta). " +
            "O Windows já abre o jogo com prioridade Alta, sem o app precisar estar aberto. Nunca usa Realtime, que pode travar o PC.",
            TweakRisk.Medium, true),
        new(GameOption.GameBooster, "Incluir no Game Booster",
            "Adiciona o .exe à lista do Game Booster, que dá prioridade Alta enquanto o jogo roda e devolve a original quando ele fecha.",
            TweakRisk.Low, true),
        new(GameOption.DisableFullscreenOptimizations, "Desativar otimizações de tela cheia",
            "Marca a opção de compatibilidade \"Desabilitar otimizações de tela inteira\". Em alguns jogos antigos (DirectX 9/11) " +
            "reduz o atraso de entrada; em jogos atuais costuma não ajudar. Por isso fica fora do \"Otimizar para o meu PC\".",
            TweakRisk.Low, false)
    ];

    public static string UndoId(GameEntry game, GameOption option) => $"{UndoPrefix}{game.ExeName.ToLowerInvariant()}:{option}";

    // --- Leitura do estado real ---

    public bool IsApplied(GameEntry game, GameOption option)
    {
        if (game.Executable is null) return false;
        return option switch
        {
            GameOption.HighPerformanceGpu =>
                ctx.Registry.Read(_paths.GpuHive, _paths.GpuPreferences, game.Executable)?.Display().Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase) == true,
            GameOption.CpuPriority =>
                ctx.Registry.Matches(_paths.IfeoHive, IfeoKey(game.ExeName), "CpuPriorityClass", RegistryValueData.DWord(HighPriorityClass)),
            GameOption.GameBooster => booster.LoadList().Contains(game.ExeName.ToLowerInvariant()),
            GameOption.DisableFullscreenOptimizations =>
                HasLayerFlag(ReadString(_paths.LayersHive, _paths.Layers, game.Executable), FullscreenFlag),
            _ => false
        };
    }

    private string? ReadString(RegHive hive, string key, string value) =>
        ctx.Registry.Read(hive, key, value) is { } v ? v.ToRegistryObject() as string : null;

    // --- Aplicar / desfazer ---

    public async Task ApplyAsync(GameEntry game, GameOption option)
    {
        if (game.Executable is null) throw new InvalidOperationException("Executável do jogo não encontrado.");
        var id = UndoId(game, option);
        switch (option)
        {
            case GameOption.HighPerformanceGpu:
                ctx.Registry.Set(id, _paths.GpuHive, _paths.GpuPreferences, game.Executable, RegistryValueData.String("GpuPreference=2;"));
                break;
            case GameOption.CpuPriority:
                var (key, value, data) = IfeoSetting(game.ExeName, HighPriorityClass);
                ctx.Registry.Set(id, _paths.IfeoHive, key, value, RegistryValueData.DWord(data));
                break;
            case GameOption.GameBooster:
                if (booster.AddToList(game.ExeName))
                    ctx.SaveCustomBackup(id, "booster-added", game.ExeName.ToLowerInvariant(), $"Game Booster: {game.ExeName} adicionado à lista");
                break;
            case GameOption.DisableFullscreenOptimizations:
                var current = ReadString(_paths.LayersHive, _paths.Layers, game.Executable);
                ctx.Registry.Set(id, _paths.LayersHive, _paths.Layers, game.Executable, RegistryValueData.String(MergeLayerFlag(current, FullscreenFlag)));
                break;
        }
        ctx.Logger.Info($"{game.Name}: {Info(option).Name} aplicado{(ctx.DryRun ? " (simulação)" : "")}.");
        await Task.CompletedTask;
    }

    public async Task RevertAsync(GameEntry game, GameOption option) => await UndoAsync(UndoId(game, option));

    /// <summary>Aplica as opções recomendadas (as marcadas como padrão). Falha em uma não impede as outras.</summary>
    public async Task<IReadOnlyList<string>> OptimizeAsync(GameEntry game)
    {
        var errors = new List<string>();
        foreach (var info in Options.Where(o => o.InDefault))
        {
            try
            {
                if (!IsApplied(game, info.Option)) await ApplyAsync(game, info.Option);
            }
            catch (Exception ex)
            {
                ctx.Logger.Error($"{game.Name}: falha em {info.Name}", ex);
                errors.Add($"{info.Name}: {ex.Message}");
            }
        }
        return errors;
    }

    public static GameOptionInfo Info(GameOption option) => Options.First(o => o.Option == option);

    // --- IUndoProvider ---

    public bool CanUndo(string id) => id.StartsWith(UndoPrefix, StringComparison.Ordinal);

    public string DisplayName(string id)
    {
        var parts = id.Split(':');
        if (parts.Length == 3 && Enum.TryParse<GameOption>(parts[2], out var option))
            return $"{Info(option).Name} ({parts[1]})";
        return id;
    }

    public Task UndoAsync(string id)
    {
        if (ctx.Backup.GetCustom(id, "booster-added") is { } exe)
        {
            if (ctx.DryRun) ctx.Logger.Simulated($"Lista do Game Booster: remover {exe}");
            else booster.RemoveFromList(exe);
        }
        ctx.Registry.Restore(id);
        if (!ctx.DryRun) ctx.Backup.RemoveTweak(id);
        ctx.Logger.Info($"Desfeito: {DisplayName(id)}");
        return Task.CompletedTask;
    }

    // --- Regras puras (testadas) ---

    /// <summary>Só um nome de arquivo .exe (sem pastas), para a subchave de IFEO não sair do lugar.</summary>
    public static string ValidExeName(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName) || exeName.IndexOfAny(['\\', '/', ':']) >= 0 ||
            !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Nome de executável inválido: {exeName}", nameof(exeName));
        return exeName;
    }

    private string IfeoKey(string exeName) => $@"{_paths.Ifeo}\{ValidExeName(exeName)}\PerfOptions";

    /// <summary>
    /// Única escrita permitida em IFEO: PerfOptions\CpuPriorityClass com 1 a 3 (Ociosa…Alta).
    /// Bloqueia Realtime (4), qualquer valor fora de PerfOptions e, sobretudo, "Debugger" (que sequestraria o jogo).
    /// </summary>
    public (string Key, string Value, uint Data) IfeoSetting(string exeName, uint priorityClass)
    {
        var key = IfeoKey(exeName);
        return EnsureSafeIfeo(key, "CpuPriorityClass", priorityClass);
    }

    public static (string Key, string Value, uint Data) EnsureSafeIfeo(string key, string valueName, uint data)
    {
        if (!key.EndsWith(@"\PerfOptions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Só a subchave PerfOptions pode ser alterada em Image File Execution Options.");
        if (!valueName.Equals("CpuPriorityClass", StringComparison.Ordinal))
            throw new InvalidOperationException($"Valor proibido em Image File Execution Options: {valueName}.");
        if (data is < 1 or > 3)
            throw new InvalidOperationException("Prioridade Realtime (ou desconhecida) nunca é usada.");
        return (key, valueName, data);
    }

    /// <summary>Flags de compatibilidade no formato "~ FLAG1 FLAG2". Mantém as que já existiam.</summary>
    public static string MergeLayerFlag(string? existing, string flag)
    {
        var flags = Tokens(existing).ToList();
        if (!flags.Contains(flag, StringComparer.OrdinalIgnoreCase)) flags.Add(flag);
        return "~ " + string.Join(' ', flags);
    }

    public static bool HasLayerFlag(string? existing, string flag) => Tokens(existing).Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Tokens(string? s) =>
        (s ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "~");
}
