using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed record FixCard(FixAction Action)
{
    public string Title => Action.Title;
    public string Description => Action.Description;
    public string RiskText => $"Risco {TweakText.RiskText(Action.Risk)}";
    public string CommandText => string.Join(Environment.NewLine, Action.Commands);
}

/// <summary>9.6: correções avulsas com comando visível, ponto de restauração nas de risco Médio, Cancelar e resumo.</summary>
public sealed partial class FixesViewModel(FixesService fixes, IRestorePointService restorePoints, IDialogService dialogs, Logger logger) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<FixCard> Cards { get; } = new(FixesService.Actions.Select(a => new FixCard(a)));

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _output = string.Empty;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string _status = string.Empty;

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsRunning;
    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        Status = "Cancelando...";
    }

    /// <summary>Definido pelo App depois que a navegação existe.</summary>
    public Action? OpenMaintenanceAction { get; set; }

    [RelayCommand] private void OpenMaintenance() => OpenMaintenanceAction?.Invoke();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(FixCard? card)
    {
        if (card is null) return;
        var action = card.Action;
        string rp = action.Risk >= TweakRisk.Medium ? "\n\nUm ponto de restauração é criado antes." : string.Empty;
        string reboot = action.NeedsReboot ? "\nDepois será preciso reiniciar o PC." : string.Empty;
        if (!dialogs.Confirm(action.Title, $"Comandos que serão executados:\n\n{card.CommandText}{rp}{reboot}\n\nContinuar?")) return;

        IsRunning = true;
        Summary = null;
        Output = string.Empty;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(line => Output = Output.Length == 0 ? line : Output + Environment.NewLine + line);
        IReadOnlyList<FixStepResult> results = [];
        bool cancelled = false;
        try
        {
            if (action.Risk >= TweakRisk.Medium)
            {
                Status = "Criando ponto de restauração...";
                var point = await Task.Run(() => restorePoints.CreateAsync($"Gueibs Optimizer - antes de: {action.Title}"));
                ((IProgress<string>)progress).Report(point.Message);
                if (!point.Success && !dialogs.Confirm("Ponto de restauração", $"{point.Message}\n\nContinuar mesmo assim?"))
                {
                    Summary = "Cancelado antes de começar.";
                    return;
                }
            }
            Status = $"Executando: {action.Title}...";
            results = await Task.Run(() => fixes.RunAsync(action, progress, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            logger.Error($"Falha na correção {action.Key}", ex);
            Summary = $"A correção foi interrompida: {ex.Message}";
        }
        finally
        {
            Summary ??= FixesService.FinalSummary(action, results, cancelled);
            logger.Info($"Correção \"{action.Title}\": {Summary}");
            Status = string.Empty;
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }
}
