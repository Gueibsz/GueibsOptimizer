using System.Windows;
using System.Windows.Media;

namespace GueibsOptimizer.Views.Controls;

/// <summary>Gráfico de linha leve (sem bibliotecas): desenha a série inteira a cada atualização.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        // Cor da grade vem do tema (GridLineBrush): clara no tema escuro, escura no claro.
        var gridBrush = TryFindResource("GridLineBrush") as Brush ?? Brushes.Gray;
        var gridPen = new Pen(gridBrush, 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(gridPen, new Point(0, h * i / 4), new Point(w, h * i / 4));

        var values = Values;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0) return;

        double max = Maximum > 0 ? Maximum : Math.Max(1, values.Max());
        double step = w / Math.Max(1, Capacity - 1);
        double startX = w - (values.Count - 1) * step;

        var geometry = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var ctx = geometry.Open())
        using (var f = fill.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                var p = new Point(startX + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);
                if (i == 0)
                {
                    ctx.BeginFigure(p, false, false);
                    f.BeginFigure(new Point(p.X, h), true, true);
                    f.LineTo(p, false, false);
                }
                else
                {
                    ctx.LineTo(p, true, true);
                    f.LineTo(p, false, false);
                }
            }
            f.LineTo(new Point(w, h), false, false);
        }

        var fillBrush = Stroke.Clone();
        fillBrush.Opacity = 0.15;
        dc.DrawGeometry(fillBrush, null, fill);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, geometry);
    }
}
