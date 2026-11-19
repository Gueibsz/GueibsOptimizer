using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>Um nível no seletor Baixo/Médio/Alto/Máximo, com a lista visível dos ajustes que ele inclui.</summary>
public sealed partial class LevelOptionViewModel(PerformanceLevel level, IReadOnlyList<string> tweakNames) : ObservableObject
{
    public PerformanceLevel Level { get; } = level;
    public string Title => PerformanceLevels.Text(Level);
    public string Description => PerformanceLevels.Description(Level);
    public IReadOnlyList<string> TweakNames { get; } = tweakNames;
    public string CountText => $"{TweakNames.Count} ajustes";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>Fase 8.4: nível de desempenho (calculado pelo estado real) e comparativo medido antes/depois do Boost.</summary>
public sealed partial class HomeViewModel
{
    private SystemSnapshotService? _snapshots;

    public ObservableCollection<LevelOptionViewModel> Levels { get; } = [];
    public ObservableCollection<ComparisonRow> Comparison { get; } = [];

    [ObservableProperty] private string _currentLevelText = "Calculando...";
    [ObservableProperty] private LevelOptionViewModel? _selectedLevel;
    [ObservableProperty] private string? _comparisonTitle;

    public void ConfigurePerformance(SystemSnapshotService snapshots) => _snapshots = snapshots;

    partial void OnSelectedLevelChanged(LevelOptionViewModel? value)
    {
        foreach (var l in Levels) l.IsSelected = ReferenceEquals(l, value);
    }

    private void UpdateLevels(IReadOnlyList<TweakState> states)
    {
        var tweaks = states.Select(s => s.Tweak).ToList();
        if (Levels.Count == 0)
        {
            foreach (var level in PerformanceLevels.All)
                Levels.Add(new LevelOptionViewModel(level, PerformanceLevels.TweaksFor(level, tweaks).Select(t => t.Name).ToList()));
        }
        var current = PerformanceLevels.Current(states);
        CurrentLevelText = $"Nível atual: {PerformanceLevels.Text(current)}";
        foreach (var l in Levels) l.IsCurrent = l.Level == current;
        SelectedLevel ??= Levels.FirstOrDefault(l => l.Level == (current ?? PerformanceLevel.Medio))
                          ?? Levels.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectLevel(LevelOptionViewModel? option)
    {
        if (option is not null) SelectedLevel = option;
    }

    [RelayCommand(CanExecute = nameof(CanBoost))]
    private async Task ApplyLevelAsync()
    {
        if (SelectedLevel is not { } option) return;
        var level = option.Level;
        string message = $"Aplicar o nível {option.Title}? Serão aplicados os ajustes que ainda faltam ({option.TweakNames.Count} no total do nível). " +
                         "Um ponto de restauração é criado antes, e cada ajuste pode ser desfeito na aba Restauração. " +
                         "Aplicar um nível menor não desfaz ajustes já aplicados. O resultado depende do hardware.";
        if (!_dialogs.Confirm("Nível de desempenho", message)) return;
        if (PerformanceLevels.RequiresConfirmation(level) &&
            !_dialogs.Confirm("Nível Máximo", "O nível Máximo inclui ajustes de risco Médio (por exemplo, serviços e recursos do Windows). Confirma que deseja continuar?"))
            return;
        _logger.Info($"Aplicando nível de desempenho: {option.Title}.");
        await RunBoostAsync(t => PerformanceLevels.Includes(level, t));
    }

    private async Task<SystemSnapshot?> MeasureSnapshotAsync()
    {
        if (_snapshots is null) return null;
        try
        {
            ProgressText = "Medindo o sistema antes...";
            return await _snapshots.MeasureAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao medir o sistema", ex);
            return null;
        }
    }

    private async Task ShowComparisonAsync(SystemSnapshot? before)
    {
        Comparison.Clear();
        ComparisonTitle = null;
        if (before is null || _snapshots is null) return;
        var after = await _snapshots.MeasureAsync();
        foreach (var row in SnapshotComparison.Rows(before, after)) Comparison.Add(row);
        ComparisonTitle = $"Medido antes ({before.TakenAt:HH:mm:ss}) e depois ({after.TakenAt:HH:mm:ss})";
        _logger.Info("Comparativo: " + string.Join("; ", Comparison.Select(r => $"{r.Label} {r.Before} → {r.After}")));
    }
}
