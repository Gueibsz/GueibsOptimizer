using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Games;

namespace GueibsOptimizer.ViewModels;

/// <summary>Uma opção de ajuste de um jogo (GPU, prioridade, Booster, tela cheia), com estado lido do sistema.</summary>
public sealed partial class GameOptionViewModel(GameEntry game, GameOptionInfo info, GameProfileService profiles, Logger logger, bool dryRun) : ObservableObject
{
    public GameOptionInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Description => Info.Description;
    public string RiskText => Info.Risk switch { TweakRisk.Low => "Risco baixo", TweakRisk.Medium => "Risco médio", _ => "Risco alto" };
    public string InfoText => $"{Info.Description}\n\n{RiskText}. Desfaz pelo botão Desfazer aqui ou na aba Restauração." +
                              (Info.InDefault ? "\n\nIncluído em \"Otimizar para o meu PC\"." : "\n\nNão incluído em \"Otimizar para o meu PC\".");

    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private string _status = string.Empty;

    public void Refresh()
    {
        IsApplied = profiles.IsApplied(game, Info.Option);
        if (Status.Length == 0 || !Status.StartsWith("Simulado", StringComparison.Ordinal))
            Status = IsApplied ? "Aplicado" : "Não aplicado";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        try
        {
            await profiles.ApplyAsync(game, Info.Option);
            Status = dryRun ? "Simulado (nada foi alterado)" : string.Empty;
        }
        catch (Exception ex)
        {
            logger.Error($"{game.Name}: falha em {Info.Name}", ex);
            Status = $"Erro: {ex.Message}";
            return;
        }
        Refresh();
    }

    [RelayCommand]
    private async Task RevertAsync()
    {
        try
        {
            await profiles.RevertAsync(game, Info.Option);
            Status = dryRun ? "Simulado (nada foi alterado)" : string.Empty;
        }
        catch (Exception ex)
        {
            logger.Error($"{game.Name}: falha ao desfazer {Info.Name}", ex);
            Status = $"Erro: {ex.Message}";
            return;
        }
        Refresh();
    }
}

/// <summary>Uma linha da tabela: Configuração | Atual | Recomendado | usar o recomendado?</summary>
public sealed partial class GameSettingRowViewModel(GameSettingState state) : ObservableObject
{
    public GameSettingState State { get; } = state;
    public string Label => State.Setting.Label;
    public string CurrentText => State.Setting.Display(State.Current);
    public string RecommendedText => State.Setting.Display(State.Recommended);
    public bool IsRecommended => State.IsRecommended;
    public bool CanSelect => !IsRecommended && State.Recommended is not null;

    /// <summary>Marcado = trocar pelo recomendado. Começa desmarcado: a decisão é sempre do usuário.</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Preset de um jogo no estilo "atual × ideal": lê o arquivo de opções do jogo, compara com o recomendado para o
/// nível do PC e aplica só os itens marcados (com backup). Sem arquivo conhecido, mostra só recomendações em texto.
/// </summary>
public sealed partial class GamePresetViewModel(GamePreset preset, GamePresetService service, IDialogService dialogs, Logger logger,
    bool dryRun, string? gameFolder = null) : ObservableObject
{
    public GamePreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public bool CanAutoApply => Preset.CanAutoApply;
    public string? ConfigPath => Preset.ConfigFile?.ResolvePath(gameFolder);
    public string ModeText => CanAutoApply
        ? "O app lê o arquivo de opções do jogo (o mesmo que o menu do jogo grava) e mostra o atual e o recomendado para o seu PC. Você escolhe item por item; antes de gravar é feito um backup."
        : $"Apenas recomendação: {Preset.ConfigNote ?? "o app não altera as configurações deste jogo."}";
    public string General => string.Join("\n", Preset.General.Select(g => "• " + g));

    public ObservableCollection<GameSettingRowViewModel> Rows { get; } = [];

    [ObservableProperty] private PcTier _tier = PcTier.Medio;
    [ObservableProperty] private string _recommendations = string.Empty;
    [ObservableProperty] private bool _hasBackup;
    [ObservableProperty] private bool _hasTable;
    [ObservableProperty] private bool _isOptimized;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _shortStatus = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    partial void OnTierChanged(PcTier value) => Refresh();

    public void Refresh()
    {
        Recommendations = string.Join("\n", Preset.RecommendationsFor(Tier).Select(r => "• " + r));
        Rows.Clear();
        HasTable = false;
        ShortStatus = string.Empty;
        if (!CanAutoApply || ConfigPath is null)
        {
            Summary = string.Empty;
            return;
        }
        HasBackup = GamePresetService.HasBackup(ConfigPath);
        if (!File.Exists(ConfigPath))
        {
            Summary = "Arquivo de configuração ainda não existe: abra o jogo uma vez, feche e clique em Atualizar.";
            return;
        }
        try
        {
            foreach (var s in GamePresetService.ReadStates(Preset, Tier, ConfigPath)) Rows.Add(new GameSettingRowViewModel(s));
            HasTable = Rows.Count > 0;
            int different = Rows.Count(r => r.CanSelect);
            IsOptimized = different == 0;
            ShortStatus = IsOptimized ? "Gráficos otimizados" : "Gráficos não otimizados";
            Summary = IsOptimized
                ? $"O jogo está otimizado para o seu PC (nível {PcTierClassifier.Text(Tier)})."
                : $"O jogo não está otimizado: {different} de {Rows.Count} opções diferentes do recomendado para o nível {PcTierClassifier.Text(Tier)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Summary = $"Não foi possível ler {ConfigPath}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Reload() => Refresh();

    [RelayCommand]
    private void SelectAllRecommended()
    {
        foreach (var r in Rows) r.IsSelected = r.CanSelect;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Rows) r.IsSelected = false;
    }

    [RelayCommand]
    private void ApplySelected()
    {
        if (ConfigPath is null) return;
        var chosen = Rows.Where(r => r.IsSelected && r.CanSelect).ToList();
        if (chosen.Count == 0)
        {
            Status = "Marque as opções que quer trocar pelo recomendado (ou use \"Marcar todas\").";
            return;
        }
        var list = string.Join("\n", chosen.Select(r => $"• {r.Label}: {r.CurrentText} → {r.RecommendedText}"));
        if (!dialogs.Confirm($"Otimizar {Name}",
                $"Aplicar estas {chosen.Count} opção(ões) em\n{ConfigPath}?\n\n{list}\n\n" +
                "Feche o jogo antes. Uma cópia do arquivo original é salva ao lado dele (.bak com data) e pode ser restaurada aqui."))
            return;
        try
        {
            service.ApplySettings(Preset, ConfigPath, chosen.ToDictionary(r => r.State.Setting, r => r.State.Recommended!), Tier);
            Status = dryRun ? "Simulado (nada foi alterado)." : $"{chosen.Count} opção(ões) aplicada(s). Abra o jogo para ver.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            logger.Warn($"{Name}: {ex.Message}");
            Status = ex.Message;
        }
        Refresh();
    }

    [RelayCommand]
    private void RestoreConfig()
    {
        if (ConfigPath is null) return;
        if (!dialogs.Confirm($"Restaurar {Name}", "Voltar o arquivo de configuração ao original (de antes da primeira otimização pelo app)?"))
            return;
        try
        {
            Status = service.RestoreConfig(Preset, ConfigPath)
                ? dryRun ? "Simulado (nada foi alterado)." : "Configuração original restaurada."
                : "Nenhum backup encontrado.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Warn($"{Name}: {ex.Message}");
            Status = ex.Message;
        }
        Refresh();
    }
}

public sealed partial class GameItemViewModel : ObservableObject
{
    private readonly GameProfileService _profiles;
    private readonly Logger _logger;
    private readonly bool _dryRun;

    public GameItemViewModel(GameEntry game, GameProfileService profiles, GamePresetViewModel? preset, Logger logger, bool dryRun)
    {
        Game = game;
        _profiles = profiles;
        _logger = logger;
        _dryRun = dryRun;
        Preset = preset;
        foreach (var info in GameProfileService.Options)
            Options.Add(new GameOptionViewModel(game, info, profiles, logger, dryRun));
    }

    public GameEntry Game { get; }
    public string Name => Game.Name;
    public string SourceText => Game.SourceText;
    public string Folder => Game.Folder;
    public string? Executable => Game.Executable;
    public string ExeText => Game.Executable is null ? "Executável não encontrado (adicione o .exe manualmente)" : Game.ExeName;
    public bool HasExecutable => Game.Executable is not null;
    public GamePresetViewModel? Preset { get; }
    public bool HasPreset => Preset is not null;
    public ObservableCollection<GameOptionViewModel> Options { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    public void Refresh()
    {
        if (!HasExecutable) { Summary = "Sem executável"; return; }
        foreach (var o in Options) o.Refresh();
        int applied = Options.Count(o => o.IsApplied);
        Summary = applied == 0 ? "Windows: sem ajustes" : $"Windows: {applied} de {Options.Count} ajustes";
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (!HasExecutable) return;
        var errors = await _profiles.OptimizeAsync(Game);
        Status = errors.Count > 0 ? "Alguns ajustes falharam: " + string.Join("; ", errors)
               : _dryRun ? "Simulado: nada foi alterado (veja o log)." : "Otimizado. Vale na próxima vez que abrir o jogo.";
        Refresh();
    }

    [RelayCommand]
    private async Task UndoAllAsync()
    {
        foreach (var info in GameProfileService.Options)
        {
            try { await _profiles.RevertAsync(Game, info.Option); }
            catch (Exception ex) { _logger.Error($"{Game.Name}: falha ao desfazer {info.Name}", ex); }
        }
        Status = _dryRun ? "Simulado: nada foi alterado." : "Ajustes deste jogo desfeitos.";
        Refresh();
    }
}

/// <summary>Tela Meus Jogos: detecta jogos, aplica ajustes por jogo (reversíveis) e mostra presets para o nível do PC.</summary>
public sealed partial class GamesViewModel(
    GameLibraryService library, GameProfileService profiles, GamePresetService presets,
    HardwareDetectionService? detection, IDialogService dialogs, Logger logger, bool dryRun) : ObservableObject
{
    private bool _loaded;
    private PcTier _tier = PcTier.Medio;

    public ObservableCollection<GameItemViewModel> Games { get; } = [];
    public ObservableCollection<GamePresetViewModel> Presets { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Procurando jogos...";
    [ObservableProperty] private string _tierText = "Nível do PC: detectando...";

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var p in GamePresetService.All)
            Presets.Add(new GamePresetViewModel(p, presets, dialogs, logger, dryRun));
        await DetectTierAsync();
        await RefreshAsync();
    }

    private async Task DetectTierAsync()
    {
        if (detection is null) { TierText = "Nível do PC: Médio (padrão)"; ApplyTier(); return; }
        try
        {
            var profile = await detection.DetectAsync();
            _tier = profile.Tier;
            TierText = $"Nível do seu PC: {PcTierClassifier.Text(_tier)}. As recomendações abaixo são para este nível.";
        }
        catch (Exception ex)
        {
            logger.Warn($"Meus Jogos: não foi possível detectar o nível do PC ({ex.Message}). Usando Médio.");
            TierText = "Nível do PC: Médio (não foi possível detectar)";
        }
        ApplyTier();
    }

    private void ApplyTier()
    {
        foreach (var p in Presets) p.Tier = _tier;
        foreach (var p in Presets) p.Refresh();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Procurando jogos (Steam, Epic, Riot, EA, Ubisoft, Battle.net e os adicionados por você)...";
        try
        {
            var games = await library.ScanAsync();
            Games.Clear();
            foreach (var g in games)
            {
                GamePresetViewModel? preset = null;
                if (GamePresetService.ForGameOrDetect(g) is { } p)
                {
                    preset = new GamePresetViewModel(p, presets, dialogs, logger, dryRun, g.Folder) { Tier = _tier };
                    preset.Refresh();
                }
                var item = new GameItemViewModel(g, profiles, preset, logger, dryRun);
                item.Refresh();
                Games.Add(item);
            }
            Status = games.Count == 0
                ? "Nenhum jogo encontrado. Use \"Adicionar pasta\" ou \"Adicionar .exe\"."
                : $"{games.Count} jogo(s) encontrado(s).";
        }
        catch (Exception ex)
        {
            logger.Error("Meus Jogos: falha ao procurar jogos", ex);
            Status = "Não foi possível procurar jogos. Veja o log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (dialogs.PickFolder("Escolha a pasta do jogo") is { } folder) await AddAsync(folder);
    }

    [RelayCommand]
    private async Task AddExeAsync()
    {
        if (dialogs.PickOpenFile("Executável do jogo (*.exe)|*.exe") is { } exe) await AddAsync(exe);
    }

    private async Task AddAsync(string path)
    {
        try
        {
            var entry = library.AddManual(path);
            if (entry.Executable is null)
                dialogs.Info("Meus Jogos", "A pasta foi adicionada, mas nenhum executável de jogo foi encontrado nela. Tente \"Adicionar .exe\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            dialogs.Error("Meus Jogos", $"Não foi possível adicionar: {ex.Message}");
            return;
        }
        await RefreshAsync();
    }
}
