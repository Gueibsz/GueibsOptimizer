using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GueibsOptimizer.Views.Controls;

/// <summary>Anel de pontuação 0–100 com animação e cor por faixa (vermelho &lt; 40, amarelo &lt; 75, verde).</summary>
public sealed class ScoreRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ScoreRing),
        new PropertyMetadata(0d, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(ScoreRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ScoreRing),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = DependencyProperty.Register(
        nameof(LowBrush), typeof(Brush), typeof(ScoreRing),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MediumBrushProperty = DependencyProperty.Register(
        nameof(MediumBrush), typeof(Brush), typeof(ScoreRing),
        new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighBrushProperty = DependencyProperty.Register(
        nameof(HighBrush), typeof(Brush), typeof(ScoreRing),
        new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(ScoreRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(ScoreRing),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush LowBrush { get => (Brush)GetValue(LowBrushProperty); set => SetValue(LowBrushProperty, value); }
    public Brush MediumBrush { get => (Brush)GetValue(MediumBrushProperty); set => SetValue(MediumBrushProperty, value); }
    public Brush HighBrush { get => (Brush)GetValue(HighBrushProperty); set => SetValue(HighBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ScoreRing)d;
        var target = Math.Clamp((double)e.NewValue, 0, 100);
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ring.BeginAnimation(DisplayValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeWidth * 2) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = (size - StrokeWidth) / 2;
        double value = (double)GetValue(DisplayValueProperty);

        dc.DrawEllipse(null, new Pen(TrackBrush, StrokeWidth), center, radius, radius);

        var brush = value < 40 ? LowBrush : value < 75 ? MediumBrush : HighBrush;
        if (value > 0.1)
        {
            var pen = new Pen(brush, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (value >= 99.95)
            {
                dc.DrawEllipse(null, pen, center, radius, radius);
            }
            else
            {
                double angle = value / 100 * 360;
                var start = PointOn(center, radius, 0);
                var end = PointOn(center, radius, angle);
                var figure = new PathFigure { StartPoint = start, IsClosed = false };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, angle > 180,
                    SweepDirection.Clockwise, true));
                dc.DrawGeometry(null, pen, new PathGeometry([figure]));
            }
        }

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var number = new FormattedText(Math.Round(value).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size * 0.28, TextBrush, dip);
        var caption = new FormattedText("de 100", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size * 0.075, brush, dip);

        double totalHeight = number.Height + caption.Height;
        dc.DrawText(number, new Point(center.X - number.Width / 2, center.Y - totalHeight / 2));
        dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y - totalHeight / 2 + number.Height));
    }

    private static Point PointOn(Point center, double radius, double angleDegrees)
    {
        // 0° no topo, sentido horário.
        double rad = (angleDegrees - 90) * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
