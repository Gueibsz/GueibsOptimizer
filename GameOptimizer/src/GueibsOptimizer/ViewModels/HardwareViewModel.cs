using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>Um medidor do painel: valor atual + histórico de 60 s.</summary>
public sealed partial class MetricViewModel(string title, string unit, double maximum) : ObservableObject
{
    private readonly List<double> _history = [];

    public string Title { get; } = title;
    public double Maximum { get; } = maximum;

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private IReadOnlyList<double>? _history60;

    public void Push(float? value)
    {
        if (value is not { } v || float.IsNaN(v))
        {
            ValueText = "—";
            return;
        }
        ValueText = $"{v:0.#} {unit}";
        _history.Add(v);
        if (_history.Count > 60) _history.RemoveAt(0);
        History60 = _history.ToArray();
    }
}

public sealed partial class HardwareViewModel : ObservableObject, ITabbedPage
{
    [ObservableProperty] private int _selectedTab;

    private readonly HardwareMonitorService _monitor;
    private readonly SystemInfoService _info;
    private readonly CheckupService _checkup;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;
    private bool _active;

    public const string PawnIOWingetId = "namazso.PawnIO";
    private readonly WingetService _winget;

    public HardwareViewModel(HardwareMonitorService monitor, SystemInfoService info, CheckupService checkup,
        WingetService winget, IDialogService dialogs, Logger logger)
    {
        _winget = winget;
        _monitor = monitor;
        _info = info;
        _checkup = checkup;
        _dialogs = dialogs;
        _logger = logger;
        _monitor.SnapshotReady += OnSnapshot;
    }

    public MetricViewModel CpuLoad { get; } = new("Carga da CPU", "%", 100);
    public MetricViewModel CpuTemp { get; } = new("Temperatura da CPU", "°C", 100);
    public MetricViewModel GpuLoad { get; } = new("Carga da GPU", "%", 100);
    public MetricViewModel GpuTemp { get; } = new("Temperatura da GPU", "°C", 100);
    public MetricViewModel RamLoad { get; } = new("Uso da RAM", "%", 100);
    public MetricViewModel CpuPower { get; } = new("Potência da CPU", "W", 0);

    [ObservableProperty] private string _cpuDetail = "—";
    [ObservableProperty] private string _gpuDetail = "—";
    [ObservableProperty] private string _ramDetail = "—";
    [ObservableProperty] private IReadOnlyList<HardwareGroup> _groups = [];
    [ObservableProperty] private string _sensorNote = "Iniciando sensores...";
    [ObservableProperty] private bool _cpuSensorsMissing;

    [ObservableProperty] private string _pcSummary = "Carregando...";
    [ObservableProperty] private bool _isLoadingInfo;
    public ObservableCollection<CheckItem> CheckItems { get; } = [];
    [ObservableProperty] private bool _isCheckingUp;

    private SystemSummary? _summary;

    /// <summary>Liga os sensores só enquanto a página estiver visível.</summary>
    public void Activate()
    {
        if (_active) return;
        _active = true;
        _monitor.Acquire();
        if (_summary is null) _ = LoadInfoAsync();
        if (CheckItems.Count == 0) _ = RunCheckupAsync();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _monitor.Release();
    }

    private void OnSnapshot(HardwareSnapshot snapshot)
    {
        var s = HardwareSummary.From(snapshot);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CpuLoad.Push(s.CpuLoad);
            CpuTemp.Push(s.CpuTemp);
            GpuLoad.Push(s.GpuLoad);
            GpuTemp.Push(s.GpuTemp);
            RamLoad.Push(s.RamLoad);
            CpuPower.Push(s.CpuPower);
            // Sem o driver PawnIO o LibreHardwareMonitor não lê clock nem potência da CPU: diz isso em vez de "—".
            CpuDetail = s.CpuClock is null && s.CpuPower is null
                ? "Clock e potência da CPU: indisponíveis sem o driver PawnIO"
                : $"Clock máx.: {Fmt(s.CpuClock, "MHz")} · Potência: {Fmt(s.CpuPower, "W")}";
            GpuDetail = $"Clock: {Fmt(s.GpuClock, "MHz")} · Ventoinha: {Fmt(s.GpuFan, "RPM")} · VRAM: {Fmt(s.GpuMemUsedMb, "MB")} / {Fmt(s.GpuMemTotalMb, "MB")}";
            RamDetail = $"Em uso: {Fmt(s.RamUsedGb, "GB")} · Disponível: {Fmt(s.RamAvailableGb, "GB")}";
            Groups = snapshot.Groups;
            if (!IsInstallingDriver)
                SensorNote = s.CpuTemp is null
                    ? "Temperatura da CPU indisponível: a leitura de sensores da CPU exige o driver PawnIO (pawnio.eu) instalado. GPU, RAM e discos funcionam sem ele."
                    : string.Empty;
            CpuSensorsMissing = s.CpuTemp is null;
        });
    }

    private static string Fmt(float? v, string unit) => v is { } x ? $"{x:0.#} {unit}" : "—";

    [ObservableProperty] private bool _isInstallingDriver;

    /// <summary>Instala o driver PawnIO pelo winget (necessário para sensores da CPU no LibreHardwareMonitorLib 0.9.6+).</summary>
    [RelayCommand]
    private async Task InstallPawnIOAsync()
    {
        if (IsInstallingDriver) return;
        if (!_dialogs.Confirm("Instalar PawnIO",
                "Para ler temperatura, clock e potência da CPU, o monitor de hardware usa o PawnIO, um driver de leitura de hardware " +
                "assinado digitalmente (pawnio.eu), o mesmo usado pelo Libre Hardware Monitor e pelo Fan Control.\n\n" +
                "Ele será instalado pelo winget (pacote namazso.PawnIO). Por ser um driver do Windows, só instale se confiar na fonte. " +
                "Pode ser removido depois em Configurações > Aplicativos.\n\nInstalar agora?"))
            return;

        IsInstallingDriver = true;
        SensorNote = "Instalando o PawnIO...";
        try
        {
            if (!await _winget.IsAvailableAsync())
            {
                _dialogs.Error("PawnIO", "O winget não foi encontrado. Instale o \"Instalador de Aplicativo\" da Microsoft Store e tente de novo.");
                return;
            }
            var result = await _winget.InstallAsync(PawnIOWingetId);
            if (result.Outcome == InstallOutcome.Failed)
            {
                _dialogs.Error("PawnIO", $"Não foi possível instalar: {result.Message}");
                return;
            }
            // Reabre a biblioteca de sensores para carregar o driver recém-instalado.
            _monitor.Release();
            _monitor.Acquire();
            SensorNote = result.Outcome == InstallOutcome.Simulated ? "Simulação: nada foi instalado." : "PawnIO instalado. Os sensores da CPU devem aparecer em alguns segundos.";
        }
        finally
        {
            IsInstallingDriver = false;
        }
    }

    [RelayCommand]
    private async Task LoadInfoAsync()
    {
        IsLoadingInfo = true;
        try
        {
            _summary = await _info.CollectAsync();
            PcSummary = _summary.ToText();
            await LoadDetailsAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao coletar informações do PC", ex);
            PcSummary = "Não foi possível coletar as informações do PC.";
        }
        finally
        {
            IsLoadingInfo = false;
        }
    }

    [RelayCommand]
    private void CopySummary()
    {
        _dialogs.CopyText(PcSummary);
        _logger.Info("Resumo do PC copiado para a área de transferência.");
    }

    [RelayCommand]
    private async Task RunCheckupAsync()
    {
        if (IsCheckingUp) return;
        IsCheckingUp = true;
        try
        {
            var items = await _checkup.RunAsync();
            CheckItems.Clear();
            foreach (var i in items) CheckItems.Add(i);
        }
        catch (Exception ex)
        {
            _logger.Error("Falha no check-up", ex);
        }
        finally
        {
            IsCheckingUp = false;
        }
    }
}
