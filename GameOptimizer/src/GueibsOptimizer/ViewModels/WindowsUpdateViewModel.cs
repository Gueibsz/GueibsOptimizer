using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>9.7: modo atual (estado real), três modos reversíveis e a data da última atualização instalada.</summary>
public sealed partial class WindowsUpdateViewModel(WindowsUpdateModeService service, IDialogService dialogs, Logger logger) : ObservableObject
{
    public IReadOnlyList<int> PauseOptions { get; } = [7, 14, 21, 28, 35];

    [ObservableProperty] private string _currentText = "Lendo...";
    [ObservableProperty] private string _lastUpdateText = "Lendo...";
    [ObservableProperty] private UpdateMode _currentMode;
    [ObservableProperty] private int _pauseDays = 7;
    [ObservableProperty] private string _status = string.Empty;

    public string PauseEndText => $"Até {DateTime.Now.AddDays(WindowsUpdateModeService.ClampPauseDays(PauseDays)):dd/MM/yyyy}";

    partial void OnPauseDaysChanged(int value) => OnPropertyChanged(nameof(PauseEndText));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var state = service.ReadState();
        CurrentMode = state.Mode;
        CurrentText = state.Text;
        var last = await Task.Run(service.ReadLastUpdate);
        LastUpdateText = last is { } l
            ? $"{l.Id}{(l.Date is { } d ? $" · {d:dd/MM/yyyy}" : string.Empty)}"
            : "não foi possível ler o histórico";
        OnPropertyChanged(nameof(PauseEndText));
    }

    [RelayCommand]
    private async Task ApplyAsync(string? mode)
    {
        var target = mode switch { "security" => UpdateMode.SecurityOnly, "pause" => UpdateMode.Paused, _ => UpdateMode.Default };
        string text = target switch
        {
            UpdateMode.SecurityOnly => "Modo Somente segurança: as atualizações de recursos (versões novas do Windows) ficam adiadas por 365 dias; as atualizações de segurança e qualidade continuam chegando normalmente.",
            UpdateMode.Paused => $"Pausar todas as atualizações por {WindowsUpdateModeService.ClampPauseDays(PauseDays)} dias ({PauseEndText.ToLowerInvariant()}). Durante a pausa o PC fica sem correções de segurança; depois o Windows volta a atualizar sozinho.",
            _ => "Voltar ao padrão do Windows: remove as políticas e a pausa colocadas pelo app."
        };
        if (!dialogs.Confirm("Windows Update", $"{text}\n\nOs valores anteriores ficam guardados e podem ser desfeitos na aba Restauração. Continuar?")) return;
        try
        {
            await Task.Run(() => service.Apply(target, PauseDays));
            Status = "Modo aplicado. O Windows Update pode levar alguns minutos para mostrar a mudança.";
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao alterar o modo do Windows Update", ex);
            Status = $"Não foi possível alterar o modo: {ex.Message}";
        }
        await RefreshAsync();
        WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        int n = await Task.Run(service.Restore);
        Status = n > 0 ? "Valores anteriores restaurados." : "Nada a desfazer: o app não alterou o Windows Update.";
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { logger.Error("Não foi possível abrir o Windows Update", ex); }
    }
}
