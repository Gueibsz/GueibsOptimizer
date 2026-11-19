using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>Dependências dos cartões vivos do Painel (8.1).</summary>
public sealed record DashboardDeps(
    AppSettings Settings,
    IRestorePointService RestorePoints,
    Func<Task<RestorePointsInfo>> ReadRestorePoints,
    CleanupService Cleanup,
    MemoryCleanerService Memory,
    Action OpenCleanup,
    Action OpenCheckup,
    Action OpenWizard);

/// <summary>
/// Painel: saudação, selo e medidor, cartões de pontos de restauração, arquivos para limpar, memória (2 s) e CPU.
/// Tudo o que aparece é medido; nada promete ganho de desempenho.
/// </summary>
public sealed partial class HomeViewModel
{
    private DashboardDeps? _dash;
    private readonly CpuUsageSampler _cpu = new();
    private readonly List<double> _cpuHistory = [];
    private CancellationTokenSource? _live;
    private int _applied, _applicable;

    [ObservableProperty] private string _greeting = "Olá!";
    [ObservableProperty] private string _badgeText = string.Empty;
    [ObservableProperty] private DashboardText.BadgeLevel _badgeLevel;
    [ObservableProperty] private string _scoreText = string.Empty;
    [ObservableProperty] private string _summaryLine = string.Empty;

    [ObservableProperty] private string _restoreText = "Lendo...";
    [ObservableProperty] private bool _isCreatingRestorePoint;
    [ObservableProperty] private string _restoreStatus = string.Empty;

    [ObservableProperty] private long? _cleanupBytes;
    [ObservableProperty] private string _cleanupText = "Calculando...";

    [ObservableProperty] private int _memoryPercent;
    [ObservableProperty] private string _memoryText = string.Empty;
    [ObservableProperty] private string _memoryStatus = string.Empty;
    [ObservableProperty] private bool _isCleaningMemory;

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuText = "Medindo...";
    [ObservableProperty] private IReadOnlyList<double> _cpuValues = [];

    public bool HasDashboard => _dash is not null;

    public void ConfigureDashboard(DashboardDeps deps)
    {
        _dash = deps;
        UpdateGreeting();
    }

    public void UpdateGreeting() =>
        Greeting = DashboardText.Greeting(_dash?.Settings.DisplayName, Environment.UserName);

    partial void OnScoreChanged(int value) => UpdateBadge();

    private void UpdateBadge()
    {
        BadgeLevel = DashboardText.Badge(Score);
        BadgeText = DashboardText.BadgeText(Score);
        ScoreText = $"Pontuação {Score} de 100";
        UpdateSummaryLine();
    }

    private void UpdateSummaryLine() =>
        SummaryLine = DashboardText.SummaryLine(_applied, _applicable, MemoryPercent, CleanupBytes);

    internal void SetAppliedCounts(int applied, int applicable)
    {
        _applied = applied;
        _applicable = applicable;
        UpdateSummaryLine();
    }

    /// <summary>Carrega os cartões que não mudam a cada segundo (pontos de restauração e limpeza).</summary>
    public async Task LoadDashboardAsync()
    {
        if (_dash is null) return;
        UpdateReviewDue();
        await Task.WhenAll(LoadRestorePointsAsync(), LoadCleanupAsync());
    }

    private async Task LoadRestorePointsAsync()
    {
        if (_dash is null) return;
        try { RestoreText = (await _dash.ReadRestorePoints()).Text; }
        catch (Exception ex) { _logger.Warn($"Painel: pontos de restauração: {ex.Message}"); RestoreText = "Não foi possível ler."; }
    }

    private async Task LoadCleanupAsync()
    {
        if (_dash is null) return;
        try
        {
            // Só calcula o tamanho (somente leitura), com as categorias padrão da tela de Limpeza.
            var estimates = await _dash.Cleanup.EstimateAsync(CleanupService.Categories().Where(c => c.DefaultSelected));
            CleanupBytes = estimates.Sum(e => e.Bytes);
            CleanupText = $"{DashboardText.FormatGb(CleanupBytes.Value)} em {estimates.Sum(e => e.Files)} arquivo(s)";
        }
        catch (Exception ex)
        {
            _logger.Warn($"Painel: cálculo da limpeza: {ex.Message}");
            CleanupText = "Não foi possível calcular.";
        }
        UpdateSummaryLine();
    }

    /// <summary>Memória e CPU a cada 2 s enquanto o Painel está visível.</summary>
    public async void StartLive()
    {
        if (_dash is null || _live is not null) return;
        _live = new CancellationTokenSource();
        var token = _live.Token;
        _cpu.Sample();
        SampleLive();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token)) SampleLive();
        }
        catch (OperationCanceledException) { }
    }

    public void StopLive()
    {
        _live?.Cancel();
        _live?.Dispose();
        _live = null;
    }

    private void SampleLive()
    {
        var (available, total) = MemoryCleanerService.ReadMemory();
        MemoryPercent = DashboardText.MemoryPercent(available, total);
        MemoryText = $"{MemoryCleanResult.FormatBytes(total - available)} de {MemoryCleanResult.FormatBytes(total)} em uso";

        if (_cpu.Sample() is { } cpu)
        {
            CpuPercent = cpu;
            CpuText = $"{cpu:0}%";
            _cpuHistory.Add(cpu);
            if (_cpuHistory.Count > 60) _cpuHistory.RemoveAt(0);
            CpuValues = _cpuHistory.ToArray();
        }
        UpdateSummaryLine();
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        if (_dash is null || IsCreatingRestorePoint) return;
        IsCreatingRestorePoint = true;
        RestoreStatus = "Criando (pode levar alguns minutos)...";
        try
        {
            var r = await _dash.RestorePoints.CreateAsync("Gueibs Optimizer - ponto manual");
            RestoreStatus = r.Message;
        }
        catch (Exception ex)
        {
            _logger.Error("Painel: criar ponto de restauração", ex);
            RestoreStatus = $"Falhou: {ex.Message}";
        }
        finally
        {
            IsCreatingRestorePoint = false;
        }
        await LoadRestorePointsAsync();
    }

    [RelayCommand]
    private async Task CleanMemoryAsync()
    {
        if (_dash is null || IsCleaningMemory) return;
        IsCleaningMemory = true;
        try
        {
            var r = await _dash.Memory.CleanAsync();
            MemoryStatus = r.Simulated ? "Simulado: nada foi alterado."
                : $"Liberado (medido): {MemoryCleanResult.FormatBytes(r.Freed)}. O resultado depende do hardware e do que está aberto.";
        }
        catch (Exception ex)
        {
            _logger.Error("Painel: limpar memória", ex);
            MemoryStatus = $"Falhou: {ex.Message}";
        }
        finally
        {
            IsCleaningMemory = false;
            SampleLive();
        }
    }

    [RelayCommand] private void OpenCleanup() => _dash?.OpenCleanup();
    [RelayCommand] private void OpenCheckup() => _dash?.OpenCheckup();
    [RelayCommand] private void OpenMaintenance() => _dash?.OpenCleanup();

    /// <summary>Mesma opção das Configurações: criar ponto de restauração antes do Boost e de outras mudanças.</summary>
    public bool CreateRestorePoint
    {
        get => _dash?.Settings.CreateRestorePoints ?? true;
        set
        {
            if (_dash is null || _dash.Settings.CreateRestorePoints == value) return;
            _dash.Settings.CreateRestorePoints = value;
            _dash.Settings.Save();
            OnPropertyChanged();
            RestorePointOptionChanged?.Invoke();
        }
    }

    public event Action? RestorePointOptionChanged;

    public void RefreshRestoreOption() => OnPropertyChanged(nameof(CreateRestorePoint));

    /// <summary>Aviso do Painel quando a revisão de integridade passou de 7 dias (ou nunca foi feita). Null = em dia.</summary>
    [ObservableProperty] private string? _reviewDueText;

    public void UpdateReviewDue()
    {
        var last = _dash?.Settings.LastIntegrityReview;
        if (_dash is null || !IntegrityCommands.IsReviewDue(last, DateTime.Now)) { ReviewDueText = null; return; }
        ReviewDueText = IntegrityCommands.DaysSince(last, DateTime.Now) is { } days
            ? $"A última revisão de integridade foi há {days} dias. O recomendado é uma vez por semana."
            : "A revisão de integridade (CHKDSK, DISM e SFC) ainda não foi feita. O recomendado é uma vez por semana.";
    }
    [RelayCommand] private void OpenWizard() => _dash?.OpenWizard();
}
