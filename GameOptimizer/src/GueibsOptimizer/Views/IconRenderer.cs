using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GueibsOptimizer.Core;
using MediaColor = System.Windows.Media.Color;

namespace GueibsOptimizer.Views;

/// <summary>
/// Desenha o símbolo do app (quadrado arredondado + raio) em qualquer cor. É o mesmo desenho de
/// tools/make-icon.ps1, usado quando a cor escolhida não tem um .ico pronto (HEX personalizado)
/// e para o ícone da bandeja (GDI, que não lê bem .ico com imagens PNG pequenas).
/// </summary>
public static class IconRenderer
{
    public static Icon CreateIcon(MediaColor background, int size = 32)
    {
        using var bmp = Draw(background, size);
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static System.Windows.Media.ImageSource CreateImageSource(MediaColor background, int size = 48)
    {
        using var icon = CreateIcon(background, size);
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Bitmap Draw(MediaColor bg, int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = s / 32f;
        float r = Math.Max(2, 10 * k);
        using var path = new GraphicsPath();
        path.AddArc(1 * k, 1 * k, r, r, 180, 90);
        path.AddArc(s - 1 * k - r, 1 * k, r, r, 270, 90);
        path.AddArc(s - 1 * k - r, s - 1 * k - r, r, r, 0, 90);
        path.AddArc(1 * k, s - 1 * k - r, r, r, 90, 90);
        path.CloseFigure();
        using var fill = new SolidBrush(System.Drawing.Color.FromArgb(bg.R, bg.G, bg.B));
        g.FillPath(fill, path);

        var boltColor = ColorMath.BestTextOn(bg);
        using var bolt = new SolidBrush(System.Drawing.Color.FromArgb(boltColor.R, boltColor.G, boltColor.B));
        g.FillPolygon(bolt, new[]
        {
            new PointF(18 * k, 4 * k), new PointF(8 * k, 18 * k), new PointF(15 * k, 18 * k),
            new PointF(12 * k, 28 * k), new PointF(24 * k, 13 * k), new PointF(17 * k, 13 * k)
        });
        return bmp;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
