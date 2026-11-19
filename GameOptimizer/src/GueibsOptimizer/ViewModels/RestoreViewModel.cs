using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class RestoreViewModel : ObservableObject
{
    private readonly RestorationService _restoration;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;

    public RestoreViewModel(RestorationService restoration, BackupStore store, IDialogService dialogs, Logger logger)
    {
        _restoration = restoration;
        _dialogs = dialogs;
        _logger = logger;
        store.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        WeakReferenceMessenger.Default.Register<RestoreViewModel, TweaksChangedMessage>(this, static (vm, _) =>
            Application.Current?.Dispatcher.BeginInvoke(vm.Refresh));
        Refresh();
    }

    public ObservableCollection<HistoryItem> History { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty = true;

    [RelayCommand]
    public void Refresh()
    {
        History.Clear();
        foreach (var item in _restoration.GetHistory()) History.Add(item);
        IsEmpty = History.Count == 0;
    }

    [RelayCommand]
    private async Task UndoAsync(HistoryItem? item)
    {
        if (item is null || IsBusy) return;
        if (!_dialogs.Confirm("Desfazer ajuste", $"Desfazer \"{item.TweakName}\"?\n\nTodas as alterações deste ajuste voltarão ao valor anterior."))
            return;

        IsBusy = true;
        try
        {
            var error = await _restoration.UndoTweakAsync(item.Entry.TweakId);
            if (error is not null) _dialogs.Error("Restauração", $"Não foi possível desfazer: {error}");
        }
        finally
        {
            IsBusy = false;
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
    }

    [RelayCommand]
    private async Task UndoAllAsync()
    {
        if (IsBusy || History.Count == 0) return;
        if (!_dialogs.Confirm("Desfazer tudo", "Desfazer TODOS os ajustes feitos pelo Gueibs Optimizer, do mais recente ao mais antigo?"))
            return;

        IsBusy = true;
        try
        {
            var errors = await _restoration.UndoAllAsync();
            if (errors.Count > 0)
                _dialogs.Error("Restauração", "Alguns itens não foram desfeitos:\n\n" + string.Join("\n", errors));
            else
                _dialogs.Info("Restauração", "Todos os ajustes foram desfeitos.");
        }
        catch (Exception ex)
        {
            _logger.Error("Erro ao desfazer tudo", ex);
        }
        finally
        {
            IsBusy = false;
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
    }

    [RelayCommand]
    private void OpenSystemRestore()
    {
        if (!RestorePointService.OpenSystemRestore(_logger))
            _dialogs.Error("Restaurar Sistema", "Não foi possível abrir o Restaurar Sistema do Windows (rstrui.exe).");
    }
}
