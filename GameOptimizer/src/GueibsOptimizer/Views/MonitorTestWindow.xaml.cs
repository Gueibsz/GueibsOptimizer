using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Views;

/// <summary>
/// Animações sincronizadas com CompositionTarget.Rendering usando o delta de tempo real entre frames
/// (a velocidade em px/s fica correta em qualquer taxa de atualização).
/// </summary>
public partial class MonitorTestWindow : Window
{
    private readonly RefreshEstimator _estimator = new();
    private TimeSpan _lastRender = TimeSpan.Zero;
    private double _x;
    private int _frames;

    public MonitorTestWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args) return;
        // Rendering pode disparar mais de uma vez por frame: só avança quando o tempo de renderização muda.
        if (args.RenderingTime == _lastRender) return;
        double dt = _lastRender == TimeSpan.Zero ? 0 : (args.RenderingTime - _lastRender).TotalMilliseconds;
        _lastRender = args.RenderingTime;
        if (dt <= 0) return;
        _estimator.AddInterval(dt);

        _x += SpeedSlider.Value * dt / 1000.0;
        Move(UfoCanvas, Ufo, _x);
        Move(UfoCanvas2, Ufo2, _x);
        Move(BarsCanvas, Bars, _x);

        if (++_frames % 60 == 0) UpdateRefreshText();
    }

    private static void Move(Canvas canvas, FrameworkElement element, double x)
    {
        double width = canvas.ActualWidth + element.ActualWidth;
        if (width <= 0) return;
        Canvas.SetLeft(element, x % width - element.ActualWidth);
    }

    private void UpdateRefreshText()
    {
        var monitor = DisplayInfo.Monitors().FirstOrDefault(m => m.Primary);
        string reported = monitor is null ? string.Empty : $"Windows informa {monitor.CurrentHz} Hz (máximo {monitor.MaxHzAtResolution} Hz). ";
        double measured = _estimator.EstimatedHz;
        RefreshText.Text = measured > 0
            ? $"{reported}Medido nesta janela: {measured:0} quadros/s."
            : $"{reported}Medindo...";
    }
}
