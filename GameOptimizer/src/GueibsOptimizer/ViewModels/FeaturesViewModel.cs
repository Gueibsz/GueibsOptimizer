using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class FeatureRowViewModel(FeatureDefinition definition) : ObservableObject
{
    public FeatureDefinition Definition { get; } = definition;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string Warning => Definition.Warning;

    [ObservableProperty] private FeatureState _state = FeatureState.Unknown;
    [ObservableProperty] private bool _isBusy;

    public string StateText => WindowsFeatureService.StateText(State);
    public bool IsAvailable => State is FeatureState.Enabled or FeatureState.Disabled;
    public bool IsEnabled => State == FeatureState.Enabled;
    public string ActionText => IsEnabled ? "Desativar" : "Ativar";
    public string CommandText => WindowsFeatureService.CommandText(Definition.FeatureName, !IsEnabled);

    partial void OnStateChanged(FeatureState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CommandText));
    }
}

/// <summary>9.5: recursos opcionais do Windows com estado real, comando exato e pedido de reinício.</summary>
public sealed partial class FeaturesViewModel(WindowsFeatureService features, IDialogService dialogs, Logger logger) : ObservableObject
{
    public ObservableCollection<FeatureRowViewModel> Rows { get; } = new(WindowsFeatureService.Known.Select(k => new FeatureRowViewModel(k)));

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _rebootPending;
    [ObservableProperty] private bool _isLoading;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var states = await features.ReadStatesAsync();
            foreach (var r in Rows) r.State = states.GetValueOrDefault(r.Definition.FeatureName, FeatureState.Unknown);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleAsync(FeatureRowViewModel? row)
    {
        if (row is null || !row.IsAvailable || row.IsBusy) return;
        bool enable = !row.IsEnabled;
        string warning = enable && row.Warning.Length > 0 ? $"\n\nAtenção: {row.Warning}" : string.Empty;
        if (!dialogs.Confirm(row.Title, $"Comando que será executado:\n\n{row.CommandText}\n\nPode levar alguns minutos e talvez peça reinício. Pode ser desfeito aqui ou na aba Restauração.{warning}\n\nContinuar?"))
            return;
        row.IsBusy = true;
        Status = $"{(enable ? "Ativando" : "Desativando")} {row.Title}...";
        try
        {
            var r = await Task.Run(() => features.SetAsync(row.Definition, enable));
            Status = $"{row.Title}: {r.Message}";
            if (r.NeedsReboot) RebootPending = true;
            if (!r.Success) dialogs.Error(row.Title, r.Message);
            await RefreshAsync();
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
        catch (Exception ex)
        {
            logger.Error($"Falha ao alterar o recurso {row.Definition.FeatureName}", ex);
            Status = $"Falha: {ex.Message}";
        }
        finally { row.IsBusy = false; }
    }
}
