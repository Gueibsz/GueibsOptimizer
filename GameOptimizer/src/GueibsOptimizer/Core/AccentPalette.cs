using System.Windows.Media;

namespace GueibsOptimizer.Core;

/// <summary>
/// Cores derivadas da cor de destaque escolhida, já com contraste garantido:
/// <list type="bullet">
/// <item><see cref="Accent"/>: preenchimentos (botão primário, anel, barras).</item>
/// <item><see cref="OnAccent"/>: texto sobre o destaque (preto ou branco, o que contrastar mais).</item>
/// <item><see cref="AccentText"/>: texto na cor de destaque sobre o fundo, ajustado até contraste ≥ 4.5:1 (WCAG AA).</item>
/// <item><see cref="Hover"/> e <see cref="Soft"/>: variações para hover e fundo suave de seleção.</item>
/// </list>
/// </summary>
public sealed record AccentPalette(Color Accent, Color Hover, Color Soft, Color AccentText, Color OnAccent)
{
    public const double MinTextContrast = 4.5;

    public static AccentPalette From(Color accent, Color background, bool isDark)
    {
        var hover = ColorMath.Mix(accent, isDark ? Colors.White : Colors.Black, 0.25);
        var soft = Color.FromArgb(isDark ? (byte)0x26 : (byte)0x1F, accent.R, accent.G, accent.B);
        var text = ColorMath.EnsureContrast(accent, background, MinTextContrast);
        var onAccent = ColorMath.BestTextOn(accent);
        return new AccentPalette(accent, hover, soft, text, onAccent);
    }

    public double TextContrast(Color background) => ColorMath.ContrastRatio(AccentText, background);
    public double OnAccentContrast => ColorMath.ContrastRatio(OnAccent, Accent);
}
