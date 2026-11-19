using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed record DefenderRow(string Label, string Value, bool IsProblem);

/// <summary>
/// Fase 8.9: tela consolidada. Defender só em leitura (o app nunca desliga a proteção), mais a exclusão da pasta do app;
/// IA e privacidade reaproveitam os ITweaks da categoria Privacidade.
/// </summary>
public sealed partial class DefenderAiViewModel : ObservableObject
{
    private readonly DefenderService _defender;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;

    public DefenderAiViewModel(DefenderService defender, CategoryTweaksViewModel ai, IDialogService dialogs, Logger logger)
    {
        _defender = defender;
        Ai = ai;
        _dialogs = dialogs;
        _logger = logger;
    }

    public CategoryTweaksViewModel Ai { get; }
    public string AppFolder => DefenderService.AppFolder;

    [ObservableProperty] private IReadOnlyList<DefenderRow> _rows = [];
    [ObservableProperty] private string _statusMessage = "Verificando...";
    [ObservableProperty] private bool _isManaged;
    [ObservableProperty] private bool _hasExclusion;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isBusy;

    public static IReadOnlyList<DefenderRow> BuildRows(DefenderStatus s)
    {
        if (!s.Available) return [new DefenderRow("Microsoft Defender", "não disponível (outro antivírus pode estar ativo)", true)];
        static string OnOff(bool? v) => v switch { true => "Ligada", false => "Desligada", _ => "não informado" };
        var rows = new List<DefenderRow>
        {
            new("Proteção em tempo real", OnOff(s.RealTimeProtection), s.RealTimeProtection == false),
            new("Definições atualizadas em", s.SignaturesUpdated is { } d ? d.ToString("dd/MM/yyyy HH:mm") : "não informado",
                s.SignaturesUpdated is { } old && (DateTime.Now - old).TotalDays > 7),
            new("Versão das definições", string.IsNullOrWhiteSpace(s.SignatureVersion) ? "não informado" : s.SignatureVersion, false),
            new("Proteção contra violações", OnOff(s.TamperProtected), false),
            new("Modo do antivírus", string.IsNullOrWhiteSpace(s.Mode) ? "não informado" : s.Mode, s.ManagedByPolicyOrThirdParty),
            new("Exclusão da pasta do app", s.ExclusionPresent ? "presente" : "ausente", false)
        };
        return rows;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var s = await _defender.GetStatusAsync();
            Rows = BuildRows(s);
            StatusMessage = s.Message;
            IsManaged = s.ManagedByPolicyOrThirdParty;
            HasExclusion = s.ExclusionPresent;
            IsAvailable = s.Available;
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao ler o estado do Defender", ex);
            StatusMessage = "Não foi possível ler o estado do Microsoft Defender.";
        }
        finally
        {
            IsBusy = false;
        }
        await Ai.RefreshAsync();
    }

    [RelayCommand]
    private async Task AddExclusionAsync()
    {
        if (!_dialogs.Confirm("Exclusão do Defender",
                $"Adicionar SOMENTE a pasta do Gueibs Optimizer às exclusões do Microsoft Defender?\n\n{AppFolder}\n\nIsso evita que o antivírus atrase ou bloqueie as leituras de sensores e as ações do app. A proteção do Windows continua ligada para todo o resto. Pode ser desfeito aqui ou na aba Restauração."))
            return;
        var error = await _defender.AddExclusionAsync();
        if (error is not null) _dialogs.Error("Defender", error);
        await RefreshAsync();
        WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
    }

    [RelayCommand]
    private async Task RemoveExclusionAsync()
    {
        var error = await _defender.RemoveExclusionAsync();
        if (error is not null) _dialogs.Error("Defender", error);
        await RefreshAsync();
        WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
    }

    [RelayCommand]
    private void OpenWindowsSecurity()
    {
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true })?.Dispose();
            _logger.Info("Segurança do Windows aberta.");
        }
        catch (Exception ex)
        {
            _logger.Error("Não foi possível abrir a Segurança do Windows", ex);
            _dialogs.Error("Segurança do Windows", "Não foi possível abrir a Segurança do Windows.");
        }
    }
}
