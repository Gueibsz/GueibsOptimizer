using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>Cartão de "Outras ferramentas": mostra o comando exato antes de executar.</summary>
public sealed record MaintenanceToolCard(string Key, string Title, string Description, string Estimate, IReadOnlyList<IntegrityTool> Tools)
{
    public string CommandText => string.Join(Environment.NewLine, Tools.Select(t => IntegrityCommands.For(t).Display));
}

public sealed record IntegrityStepLine(string Title, string Outcome, string Summary, bool IsProblem);

/// <summary>Fase 8.5: revisão de integridade (CHKDSK leitura, DISM, SFC) e ferramentas avulsas, com saída, cancelar e resumo.</summary>
public sealed partial class WindowsViewModel
{
    private IntegrityToolRunner? _tools;
    private IRestorePointService? _restorePoints;
    private AppSettings? _settings;
    private Shell? _shell;
    private CancellationTokenSource? _toolCts;

    public ObservableCollection<MaintenanceToolCard> ToolCards { get; } = [];
    public ObservableCollection<IntegrityStepLine> ToolSteps { get; } = [];

    public string WeeklyCommandText => string.Join(Environment.NewLine, IntegrityCommands.WeeklyReview.Select(t => IntegrityCommands.For(t).Display));

    [ObservableProperty] private bool _createRestorePointFirst = true;
    [ObservableProperty] private bool _isToolRunning;
    [ObservableProperty] private string _toolStatus = string.Empty;
    [ObservableProperty] private string _toolOutput = string.Empty;
    [ObservableProperty] private string? _toolSummary;
    [ObservableProperty] private string _lastReviewText = string.Empty;
    [ObservableProperty] private bool _canScheduleChkdsk;

    public void ConfigureMaintenance(IntegrityToolRunner tools, IRestorePointService restorePoints, AppSettings settings, Shell shell)
    {
        _tools = tools;
        _restorePoints = restorePoints;
        _settings = settings;
        _shell = shell;
        ToolCards.Clear();
        ToolCards.Add(new("full", "Reparo completo", "DISM repara a imagem do Windows e, em seguida, o SFC corrige os arquivos de sistema.", "15 a 30 min", IntegrityCommands.FullRepair));
        ToolCards.Add(new("sfc", "SFC", "Verifica os arquivos protegidos do Windows e troca os corrompidos.", "5 a 15 min", [IntegrityTool.Sfc]));
        ToolCards.Add(new("dism", "DISM", "Verifica e repara a imagem do Windows usada pelo SFC (usa a internet).", "5 a 20 min", [IntegrityTool.Dism]));
        ToolCards.Add(new("chkdsk", $"CHKDSK {IntegrityCommands.SystemDrive}", "Verifica o sistema de arquivos em modo leitura (sem /f): não altera nada.", "2 a 10 min", [IntegrityTool.ChkdskScan]));
        UpdateLastReviewText();
    }

    private void UpdateLastReviewText()
    {
        var days = IntegrityCommands.DaysSince(_settings?.LastIntegrityReview, DateTime.Now);
        LastReviewText = days switch
        {
            null => "Última revisão: nunca feita.",
            0 => "Última revisão: hoje.",
            1 => "Última revisão: há 1 dia.",
            _ => $"Última revisão: há {days} dias."
        };
    }

    private bool CanRunTool() => !IsToolRunning && _tools is not null;

    partial void OnIsToolRunningChanged(bool value)
    {
        RunWeeklyReviewCommand.NotifyCanExecuteChanged();
        RunToolCommand.NotifyCanExecuteChanged();
        CancelToolCommand.NotifyCanExecuteChanged();
        ScheduleChkdskRepairCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RunWeeklyReviewAsync()
    {
        if (!_dialogs.Confirm("Revisão semanal de integridade",
                "Serão executados, nesta ordem:\n\n" + WeeklyCommandText +
                "\n\nO CHKDSK roda só em modo leitura (sem /f). Estimativa: 10 a 30 minutos. Você pode cancelar a qualquer momento. Continuar?"))
            return;
        await RunSequenceAsync(IntegrityCommands.WeeklyReview, CreateRestorePointFirst, isWeeklyReview: true);
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RunToolAsync(MaintenanceToolCard? card)
    {
        if (card is null) return;
        if (!_dialogs.Confirm(card.Title, $"Comando que será executado:\n\n{card.CommandText}\n\nEstimativa: {card.Estimate}. Você pode cancelar a qualquer momento. Continuar?"))
            return;
        await RunSequenceAsync(card.Tools, createRestorePoint: false, isWeeklyReview: false);
    }

    private bool CanCancelTool() => IsToolRunning;

    [RelayCommand(CanExecute = nameof(CanCancelTool))]
    private void CancelTool()
    {
        _toolCts?.Cancel();
        ToolStatus = "Cancelando...";
    }

    private async Task RunSequenceAsync(IReadOnlyList<IntegrityTool> tools, bool createRestorePoint, bool isWeeklyReview)
    {
        if (_tools is null) return;
        IsToolRunning = true;
        CanScheduleChkdsk = false;
        ToolSteps.Clear();
        ToolSummary = null;
        ToolOutput = string.Empty;
        _toolCts = new CancellationTokenSource();
        var results = new List<IntegrityResult>();
        var progress = new Progress<string>(AppendOutput);
        try
        {
            if (createRestorePoint && _restorePoints is not null)
            {
                ToolStatus = "Criando ponto de restauração...";
                var rp = await Task.Run(() => _restorePoints.CreateAsync("Gueibs Optimizer - antes da revisão de integridade"));
                ToolSteps.Add(new IntegrityStepLine("Ponto de restauração", rp.Success ? "OK" : "Falhou", rp.Message, !rp.Success));
                if (!rp.Success && !_dialogs.Confirm("Ponto de restauração", $"{rp.Message}\n\nContinuar a revisão mesmo assim? As ferramentas só verificam e reparam arquivos do próprio Windows."))
                {
                    ToolSummary = "Cancelado antes de começar.";
                    return;
                }
            }

            for (int i = 0; i < tools.Count; i++)
            {
                var command = IntegrityCommands.For(tools[i]);
                ToolStatus = $"Etapa {i + 1} de {tools.Count}: {command.Title}";
                AppendOutput($"> {command.Display}");
                var result = await Task.Run(() => _tools.RunAsync(command, progress, _toolCts.Token));
                results.Add(result);
                ToolSteps.Add(new IntegrityStepLine(command.Title, IntegrityCommands.OutcomeText(result.Outcome), result.Summary,
                    result.Outcome is IntegrityOutcome.Failed or IntegrityOutcome.ErrorsFound));
                if (result.OfferChkdskRepair) CanScheduleChkdsk = true;
                if (result.Outcome == IntegrityOutcome.Cancelled) break;
            }

            ToolSummary = IntegrityCommands.FinalSummary(results);
            bool completed = results.Count == tools.Count && results.All(r => r.Outcome is not (IntegrityOutcome.Cancelled or IntegrityOutcome.Simulated));
            if (isWeeklyReview && completed && _settings is not null)
            {
                _settings.LastIntegrityReview = DateTime.Now;
                _settings.Save();
                UpdateLastReviewText();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Falha na manutenção", ex);
            ToolSummary = $"A execução foi interrompida: {ex.Message}";
        }
        finally
        {
            ToolStatus = string.Empty;
            _toolCts.Dispose();
            _toolCts = null;
            IsToolRunning = false;
        }
    }

    private void AppendOutput(string line)
    {
        // Mantém o log da tela leve: só as últimas ~400 linhas.
        var text = ToolOutput.Length == 0 ? line : ToolOutput + Environment.NewLine + line;
        var parts = text.Split(Environment.NewLine);
        ToolOutput = parts.Length > 400 ? string.Join(Environment.NewLine, parts[^400..]) : text;
    }

    private bool CanSchedule() => CanScheduleChkdsk && !IsToolRunning;

    partial void OnCanScheduleChkdskChanged(bool value) => ScheduleChkdskRepairCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanSchedule))]
    private async Task ScheduleChkdskRepairAsync()
    {
        if (_shell is null) return;
        string drive = IntegrityCommands.SystemDrive;
        if (!_dialogs.Confirm("Agendar reparo do disco",
                $"O disco {drive} será marcado para o Windows verificar e reparar na próxima inicialização (comando: fsutil {string.Join(" ", IntegrityCommands.ScheduleRepairArguments(drive))}).\n\nNa próxima vez que o PC ligar, o reparo pode levar vários minutos; não desligue o computador durante ele. Agendar?"))
            return;
        var r = await _shell.RunMutatingAsync("Agendar reparo do disco no próximo boot", "fsutil.exe", IntegrityCommands.ScheduleRepairArguments(drive));
        if (r.Success)
        {
            CanScheduleChkdsk = false;
            _dialogs.Info("Reparo agendado", r.Simulated ? "Simulação: nada foi agendado." : "O reparo será feito na próxima inicialização. Reinicie quando puder.");
        }
        else _dialogs.Error("Agendar reparo", $"Não foi possível agendar o reparo: {r.StdErr.Trim()}{r.StdOut.Trim()}");
    }

    // --- 9.2: Limpeza de Disco do Windows (cleanmgr) como ação avulsa ---
    public const int CleanMgrProfile = 64;
    public static IReadOnlyList<string> CleanMgrSetArgs => [$"/sageset:{CleanMgrProfile}"];
    public static IReadOnlyList<string> CleanMgrRunArgs => [$"/sagerun:{CleanMgrProfile}"];

    [ObservableProperty] private string _cleanMgrStatus = string.Empty;

    [RelayCommand]
    private async Task RunWindowsDiskCleanupAsync(string? mode)
    {
        if (_shell is null) return;
        bool configure = mode == "set";
        var args = configure ? CleanMgrSetArgs : CleanMgrRunArgs;
        string estimate = CleanupOptions.Any(o => o.Bytes > 0)
            ? $"Estimativa do app para as categorias analisadas: {MemoryCleanResult.FormatBytes((ulong)CleanupOptions.Sum(o => o.Bytes))}. A Limpeza de Disco pode encontrar outras categorias (ex.: arquivos de atualização)."
            : "Clique em Analisar acima para ver uma estimativa do espaço.";
        string message = configure
            ? $"Abre a Limpeza de Disco do Windows para você marcar o que ela deve limpar (comando: cleanmgr {args[0]}). Nada é apagado nesta etapa."
            : $"Executa a Limpeza de Disco do Windows com as opções escolhidas em \"Escolher categorias\" (comando: cleanmgr {args[0]}).\n\n{estimate}\n\nContinuar?";
        if (!_dialogs.Confirm("Limpeza de Disco do Windows", message)) return;
        CleanMgrStatus = configure ? "Aguardando a janela da Limpeza de Disco..." : "Limpeza de Disco em andamento...";
        var r = await _shell.RunMutatingAsync($"Limpeza de Disco do Windows ({args[0]})", "cleanmgr.exe", args, TimeSpan.FromMinutes(60));
        CleanMgrStatus = r.Simulated ? $"Simulação: cleanmgr {args[0]} não foi executado."
            : r.TimedOut ? "A Limpeza de Disco demorou demais e foi interrompida."
            : configure ? "Categorias salvas. Agora use Executar." : "Limpeza de Disco concluída.";
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception ex) { _logger.Warn($"Não foi possível copiar: {ex.Message}"); }
    }
}
