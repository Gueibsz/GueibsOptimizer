using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed record SummaryLine(string Icon, string Text, bool IsError);

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly TweakRegistry _registry;
    private readonly ScoreService _scoreService;
    private readonly BoostService _boostService;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;

    private readonly HardwareDetectionService? _detection;

    public HomeViewModel(TweakRegistry registry, ScoreService scoreService, BoostService boostService,
        IDialogService dialogs, Logger logger, bool dryRun, HardwareDetectionService? detection = null)
    {
        _detection = detection;
        _registry = registry;
        _scoreService = scoreService;
        _boostService = boostService;
        _dialogs = dialogs;
        _logger = logger;
        IsDryRun = dryRun;
        WeakReferenceMessenger.Default.Register<HomeViewModel, TweaksChangedMessage>(this,
            static (vm, msg) => _ = vm.RefreshAsync());
    }

    public bool IsDryRun { get; }

    public ObservableCollection<LogEntry> Activity { get; } = [];
    public ObservableCollection<SummaryLine> SummaryLines { get; } = [];

    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreCaption = "Calculando...";
    [ObservableProperty] private string _appliedText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string? _summaryTitle;

    // --- "Seu PC foi reconhecido" ---
    [ObservableProperty] private string _pcSummary = "Reconhecendo o hardware...";
    [ObservableProperty] private string _pcTierText = string.Empty;
    [ObservableProperty] private bool _pcRecognized;
    public ObservableCollection<string> PcDetails { get; } = [];

    /// <summary>Reconhece o PC uma vez (WMI é lento) e preenche o card da tela Início.</summary>
    public async Task LoadHardwareAsync()
    {
        if (_detection is null || PcRecognized) return;
        try
        {
            var profile = await _detection.DetectAsync();
            PcSummary = profile.Summary;
            PcTierText = $"Nível {profile.TierText}";
            PcDetails.Clear();
            foreach (var r in profile.TierReasons) PcDetails.Add("• " + r);
            foreach (var r in profile.Recommendations) PcDetails.Add("→ " + r);
            PcRecognized = true;
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao reconhecer o hardware", ex);
            PcSummary = "Não foi possível reconhecer o hardware. Veja os detalhes em Hardware > Meu PC.";
        }
    }

    public void AddActivity(LogEntry entry)
    {
        Activity.Insert(0, entry);
        while (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var result = await _scoreService.EvaluateAsync(_registry.All);
        Score = result.Score;
        ScoreCaption = result.Band switch
        {
            ScoreBand.Low => "Há muito espaço para otimizar.",
            ScoreBand.Medium => "Bom, mas ainda dá para melhorar.",
            _ => "Seu PC está bem otimizado."
        };
        int applicable = result.States.Count(s => s.Applicable);
        int applied = result.States.Count(s => s.Applied);
        AppliedText = $"{applied} de {applicable} ajustes aplicados";
        SetAppliedCounts(applied, applicable);
        UpdateLevels(result.States);
    }

    [RelayCommand(CanExecute = nameof(CanBoost))]
    private Task BoostAsync() => RunBoostAsync(null);

    private async Task RunBoostAsync(Func<GueibsOptimizer.Models.ITweak, bool>? selector)
    {
        IsBusy = true;
        BoostCommand.NotifyCanExecuteChanged(); ApplyLevelCommand.NotifyCanExecuteChanged();
        SummaryTitle = null;
        SummaryLines.Clear();
        try
        {
            var progress = new Progress<BoostProgress>(p =>
            {
                ProgressValue = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                ProgressText = p.Message;
            });

            var snapshotBefore = await MeasureSnapshotAsync();
            var summary = await Task.Run(() => _boostService.RunAsync(
                rp => Task.FromResult(_dialogs.Confirm("Ponto de restauração",
                    $"{rp.Message}\n\nSem o ponto de restauração você ainda pode desfazer cada ajuste pela aba Restauração. Deseja continuar mesmo assim?")),
                progress, default, selector));

            ShowSummary(summary);
            if (!(summary.Cancelled && summary.Steps.Count == 0))
                await ShowComparisonAsync(snapshotBefore);
        }
        catch (Exception ex)
        {
            _logger.Error("Erro inesperado no Boost", ex);
            _dialogs.Error("Boost", $"O Boost foi interrompido por um erro inesperado: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BoostCommand.NotifyCanExecuteChanged(); ApplyLevelCommand.NotifyCanExecuteChanged();
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
    }

    private bool CanBoost() => !IsBusy;

    private void ShowSummary(BoostSummary summary)
    {
        if (summary.Cancelled && summary.Steps.Count == 0)
        {
            SummaryTitle = "Boost cancelado: nenhuma alteração foi feita.";
            ProgressText = string.Empty;
            ProgressValue = 0;
            return;
        }

        string prefix = summary.Simulated ? "Simulação concluída" : "Boost concluído";
        SummaryTitle = $"{prefix}: pontuação {summary.ScoreBefore} → {summary.ScoreAfter}";
        if (summary.RestorePoint is { } rp)
            SummaryLines.Add(new SummaryLine(rp.Success ? "" : "", rp.Message, !rp.Success));

        foreach (var step in summary.Steps)
        {
            string icon = step.Status switch
            {
                BoostStepStatus.Applied => "",
                BoostStepStatus.AlreadyApplied => "",
                BoostStepStatus.NotApplicable => "",
                _ => ""
            };
            SummaryLines.Add(new SummaryLine(icon, $"{step.Tweak.Name}: {step.Message}", step.Status == BoostStepStatus.Failed));
            if (step.Tweak.Notice is { } notice && step.Status == BoostStepStatus.Applied)
                SummaryLines.Add(new SummaryLine("", $"    {notice}", false));
        }
        if (summary.Simulated)
            SummaryLines.Add(new SummaryLine("", "Modo simulação: nada foi alterado. Veja os detalhes em Atividade.", false));
    }
}
