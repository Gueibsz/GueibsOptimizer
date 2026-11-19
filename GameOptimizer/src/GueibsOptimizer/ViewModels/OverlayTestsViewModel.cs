using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;
using GueibsOptimizer.Views;

namespace GueibsOptimizer.ViewModels;

public sealed partial class OverlayTestsViewModel : ObservableObject
{
    private readonly FpsService _fps;
    private readonly NetworkTestService _network;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;
    private readonly Action _openPolling;
    private readonly Action _openMonitor;
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<double> _pingHistory = [];
    private CancellationTokenSource? _networkCts;

    private readonly PresentMonDownloader _downloader;

    public OverlayTestsViewModel(FpsService fps, PresentMonDownloader downloader, NetworkTestService network, AppSettings settings,
        IDialogService dialogs, Logger logger, Action openPolling, Action openMonitor)
    {
        _downloader = downloader;
        _fps = fps;
        _network = network;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _openPolling = openPolling;
        _openMonitor = openMonitor;
        _pingTargets = settings.PingTargets;
        _fpsTimer.Tick += (_, _) => UpdateFps();
    }

    /// <summary>Definido pelo App depois que a janela principal existe (o atalho global precisa de um HWND).</summary>
    public IOverlayController? Overlay { get; set; }

    // --- Overlay ---
    public bool ShowFps { get => _settings.OverlayShowFps; set { _settings.OverlayShowFps = value; SaveItems(); } }
    public bool ShowLows { get => _settings.OverlayShowLows; set { _settings.OverlayShowLows = value; SaveItems(); } }
    public bool ShowCpu { get => _settings.OverlayShowCpu; set { _settings.OverlayShowCpu = value; SaveItems(); } }
    public bool ShowGpu { get => _settings.OverlayShowGpu; set { _settings.OverlayShowGpu = value; SaveItems(); } }
    public bool ShowTemps { get => _settings.OverlayShowTemps; set { _settings.OverlayShowTemps = value; SaveItems(); } }

    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private string _hotkeyText = "Atalho: Ctrl+Shift+F10";

    // --- FPS ---
    [ObservableProperty] private string _fpsStatus = "Parado.";
    [ObservableProperty] private string _fpsValues = "—";
    [ObservableProperty] private bool _isFpsRunning;
    [ObservableProperty] private string _presentMonPath = string.Empty;

    // --- Rede ---
    [ObservableProperty] private string _pingTargets;
    [ObservableProperty] private bool _isNetworkBusy;
    [ObservableProperty] private string _speedStatus = "Mede download e upload com 4 conexões paralelas (servidores da Cloudflare).";
    [ObservableProperty] private double _speedProgress;
    [ObservableProperty] private IReadOnlyList<double>? _pingSeries;
    public ObservableCollection<string> PingResults { get; } = [];

    public void OnShown()
    {
        if (Overlay is not null)
        {
            IsOverlayVisible = Overlay.IsVisible;
            HotkeyText = Overlay.HotkeyRegistered
                ? "Atalho global: Ctrl+Shift+F10 mostra/oculta"
                : "O atalho Ctrl+Shift+F10 já está em uso por outro programa; use o botão.";
        }
        PresentMonPath = _fps.ExecutablePath ?? _fps.Locate() ?? "PresentMon não encontrado";
    }

    private void SaveItems()
    {
        _settings.Save();
        Overlay?.RefreshItems();
    }

    [RelayCommand]
    private void ToggleOverlay()
    {
        Overlay?.Toggle();
        IsOverlayVisible = Overlay?.IsVisible == true;
        if (!IsOverlayVisible) IsEditMode = false;
    }

    partial void OnIsEditModeChanged(bool value)
    {
        Overlay?.SetEditMode(value);
        IsOverlayVisible = Overlay?.IsVisible == true;
    }

    [RelayCommand]
    private async Task ToggleFpsAsync()
    {
        if (_fps.IsRunning)
        {
            _fps.Stop();
            _fpsTimer.Stop();
        }
        else if (await _fps.StartAsync())
        {
            _fpsTimer.Start();
        }
        IsFpsRunning = _fps.IsRunning;
        FpsStatus = _fps.Status;
        PresentMonPath = _fps.ExecutablePath ?? "PresentMon não encontrado";
    }

    private void UpdateFps()
    {
        FpsStatus = _fps.Status;
        var s = _fps.Snapshot();
        FpsValues = s.Frames == 0
            ? "—"
            : $"{s.AverageFps:0} FPS médio · 1% low {s.Low1Fps:0} · 0.1% low {s.Low01Fps:0} · {s.LastFrameMs:0.0} ms";
    }

    [ObservableProperty] private bool _isDownloading;

    [RelayCommand]
    private async Task DownloadPresentMonAsync()
    {
        if (IsDownloading) return;
        if (!_dialogs.Confirm("Baixar PresentMon",
                "O Gueibs Optimizer vai baixar a versão de console do PresentMon da página oficial da Intel no GitHub (GameTechDev) " +
                "e só vai usá-la se a assinatura digital da Intel for válida.\n\nO arquivo fica em %AppData%\\GueibsOptimizer\\tools. Baixar agora?"))
            return;

        IsDownloading = true;
        try
        {
            var progress = new Progress<double>(p => FpsStatus = $"Baixando PresentMon... {p:0}%");
            var path = await Task.Run(() => _downloader.DownloadAsync(progress));
            PresentMonPath = path;
            FpsStatus = "PresentMon pronto. Clique em Iniciar / parar.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao baixar o PresentMon", ex);
            FpsStatus = "Não foi possível baixar o PresentMon.";
            _dialogs.Error("PresentMon", $"Não foi possível baixar o PresentMon: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void PickPresentMon()
    {
        var path = _dialogs.PickOpenFile("PresentMon (*.exe)|*.exe");
        if (path is null) return;
        _settings.PresentMonPath = path;
        _settings.Save();
        PresentMonPath = path;
    }

    [RelayCommand]
    private void OpenPresentMonPage()
    {
        try { Process.Start(new ProcessStartInfo(FpsService.DownloadPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { _logger.Error("Não foi possível abrir a página do PresentMon", ex); }
    }

    [RelayCommand]
    private async Task RunPingAsync()
    {
        if (IsNetworkBusy) return;
        var targets = NetworkTestService.ParseTargets(PingTargets);
        if (targets.Count == 0)
        {
            _dialogs.Info("Ping", "Informe ao menos um endereço válido (ex.: 1.1.1.1, 8.8.8.8).");
            return;
        }
        _settings.PingTargets = string.Join(", ", targets);
        _settings.Save();

        IsNetworkBusy = true;
        _networkCts = new CancellationTokenSource();
        PingResults.Clear();
        _pingHistory.Clear();
        try
        {
            foreach (var target in targets)
            {
                var stats = await _network.PingAsync(target, 20, ms =>
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (ms is { } v) _pingHistory.Add(v);
                        if (_pingHistory.Count > 120) _pingHistory.RemoveAt(0);
                        PingSeries = _pingHistory.ToArray();
                    }), _networkCts.Token);
                PingResults.Add(stats.Summary);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Falha no teste de ping", ex);
            PingResults.Add($"Erro: {ex.Message}");
        }
        finally
        {
            IsNetworkBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunSpeedTestAsync()
    {
        if (IsNetworkBusy) return;
        IsNetworkBusy = true;
        _networkCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<SpeedProgress>(p =>
            {
                SpeedProgress = p.Phase == "Download" ? p.Percent / 2 : 50 + p.Percent / 2;
                SpeedStatus = $"{p.Phase}: {p.CurrentMbps:0.0} Mbps";
            });
            var r = await _network.SpeedTestAsync(progress, _networkCts.Token);
            SpeedStatus = $"Download: {r.DownloadMbps:0.0} Mbps · Upload: {r.UploadMbps:0.0} Mbps";
            _logger.Info($"Teste de velocidade: {SpeedStatus}");
        }
        catch (OperationCanceledException)
        {
            SpeedStatus = "Teste cancelado.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha no teste de velocidade", ex);
            SpeedStatus = $"Não foi possível concluir o teste: {ex.Message}";
        }
        finally
        {
            IsNetworkBusy = false;
            SpeedProgress = 0;
        }
    }

    [RelayCommand]
    private void CancelNetwork() => _networkCts?.Cancel();

    [RelayCommand]
    private void OpenPolling() => _openPolling();

    [RelayCommand]
    private void OpenMonitor() => _openMonitor();
}
