using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>Dados exibidos no overlay, atualizados a cada 500 ms.</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly FpsService _fps;
    private readonly HardwareMonitorService _hardware;
    private readonly DispatcherTimer _timer;

    public OverlayViewModel(FpsService fps, HardwareMonitorService hardware, AppSettings settings)
    {
        _fps = fps;
        _hardware = hardware;
        Settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Update();
    }

    public AppSettings Settings { get; }

    [ObservableProperty] private string _fpsText = "FPS —";
    [ObservableProperty] private string _lowsText = "1% — · 0.1% —";
    [ObservableProperty] private string _cpuText = "CPU —";
    [ObservableProperty] private string _gpuText = "GPU —";
    [ObservableProperty] private bool _showFps;
    [ObservableProperty] private bool _showLows;
    [ObservableProperty] private bool _showCpu;
    [ObservableProperty] private bool _showGpu;
    [ObservableProperty] private bool _isEditing;

    public void Start()
    {
        ApplySettings();
        _hardware.Acquire();
        _timer.Start();
        Update();
    }

    public void Stop()
    {
        _timer.Stop();
        _hardware.Release();
    }

    public void ApplySettings()
    {
        ShowFps = Settings.OverlayShowFps;
        ShowLows = Settings.OverlayShowLows;
        ShowCpu = Settings.OverlayShowCpu;
        ShowGpu = Settings.OverlayShowGpu;
    }

    private void Update()
    {
        var f = _fps.Snapshot();
        FpsText = f.Frames == 0 ? "FPS —" : $"FPS {f.AverageFps:0}";
        LowsText = f.Frames == 0 ? "1% — · 0.1% —" : $"1% {f.Low1Fps:0} · 0.1% {f.Low01Fps:0}";

        if (_hardware.Latest is { } snapshot)
        {
            var s = HardwareSummary.From(snapshot);
            bool temps = Settings.OverlayShowTemps;
            CpuText = $"CPU {Pct(s.CpuLoad)}{(temps ? $" · {Temp(s.CpuTemp)}" : "")}";
            GpuText = $"GPU {Pct(s.GpuLoad)}{(temps ? $" · {Temp(s.GpuTemp)}" : "")}";
        }
    }

    private static string Pct(float? v) => v is { } x ? $"{x:0}%" : "—";
    private static string Temp(float? v) => v is { } x ? $"{x:0}°C" : "—°C";
}
