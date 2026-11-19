using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GueibsOptimizer.Views.Controls;

/// <summary>
/// Medidor semicircular 0–100: arco com degradê (cores de perigo → atenção → sucesso do tema), marcações a cada 20,
/// ponteiro animado e o valor no centro. Desenho próprio, feito em OnRender.
/// </summary>
public sealed class SemiGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SemiGauge), new PropertyMetadata(0.0, OnValueChanged));

    /// <summary>Valor mostrado pelo ponteiro (animado até <see cref="Value"/>).</summary>
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(SemiGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = Brush(nameof(LowBrush));
    public static readonly DependencyProperty MediumBrushProperty = Brush(nameof(MediumBrush));
    public static readonly DependencyProperty HighBrushProperty = Brush(nameof(HighBrush));
    public static readonly DependencyProperty TrackBrushProperty = Brush(nameof(TrackBrush));
    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty MutedBrushProperty = Brush(nameof(MutedBrush));

    private static DependencyProperty Brush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(SemiGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }
    public Brush LowBrush { get => (Brush)GetValue(LowBrushProperty); set => SetValue(LowBrushProperty, value); }
    public Brush MediumBrush { get => (Brush)GetValue(MediumBrushProperty); set => SetValue(MediumBrushProperty, value); }
    public Brush HighBrush { get => (Brush)GetValue(HighBrushProperty); set => SetValue(HighBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (SemiGauge)d;
        var target = Math.Clamp((double)e.NewValue, 0, 100);
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        g.BeginAnimation(DisplayValueProperty, anim);
    }

    /// <summary>Ângulo do valor: 0 → 180° (esquerda), 100 → 0° (direita).</summary>
    public static double AngleFor(double value) => 180 - Math.Clamp(value, 0, 100) * 1.8;

    private static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Cor do degradê no valor: vermelho (0) → amarelo (50) → verde (100).</summary>
    private Color GradientAt(double value) => value < 50
        ? Lerp(ColorOf(LowBrush), ColorOf(MediumBrush), value / 50)
        : Lerp(ColorOf(MediumBrush), ColorOf(HighBrush), (value - 50) / 50);

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 150 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double stroke = Math.Max(8, w * 0.07);
        double r = Math.Min(w / 2, h - 44) - stroke / 2 - 2; // espaço abaixo do eixo para o número
        if (r <= 10) return;
        var c = new Point(w / 2, r + stroke / 2 + 2);
        Point At(double value, double radius)
        {
            double a = AngleFor(value) * Math.PI / 180;
            return new Point(c.X + radius * Math.Cos(a), c.Y - radius * Math.Sin(a));
        }

        // Trilho e arco em degradê (segmentos curtos, cada um com a sua cor).
        DrawArc(dc, c, r, 0, 100, new Pen(TrackBrush, stroke));
        const int steps = 60;
        for (int i = 0; i < steps; i++)
        {
            double a = i * 100.0 / steps, b = (i + 1) * 100.0 / steps + 0.4;
            var pen = new Pen(new SolidColorBrush(GradientAt(a)), stroke) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            DrawArc(dc, c, r, a, Math.Min(b, 100), pen);
        }

        // Marcações a cada 20.
        var tickPen = new Pen(MutedBrush, 1.5);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int v = 0; v <= 100; v += 20)
        {
            dc.DrawLine(tickPen, At(v, r - stroke / 2 - 2), At(v, r - stroke / 2 - 9));
            var label = new FormattedText(v.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, MutedBrush, dpi);
            var p = At(v, r - stroke / 2 - 18);
            dc.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
        }

        // Ponteiro.
        var needlePen = new Pen(TextBrush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(needlePen, c, At(DisplayValue, r - stroke / 2 - 24));
        dc.DrawEllipse(TextBrush, null, c, 6, 6);

        // Valor.
        var text = new FormattedText(Math.Round(DisplayValue).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 30, TextBrush, dpi);
        dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y + 6));
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double from, double to, Pen pen)
    {
        Point P(double v) { double a = AngleFor(v) * Math.PI / 180; return new Point(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a)); }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(P(from), false, false);
            ctx.ArcTo(P(to), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
