using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace GueibsOptimizer.Core;

/// <summary>Cores: leitura de HEX, luminância e contraste (fórmulas da WCAG 2.x).</summary>
public static partial class ColorMath
{
    [GeneratedRegex("^#?([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$")]
    private static partial Regex HexPattern();

    /// <summary>Aceita "#RRGGBB", "RRGGBB" ou "#RGB". Transparência não é aceita (destaque precisa ser opaco).</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var t = text?.Trim() ?? string.Empty;
        if (!HexPattern().IsMatch(t)) return false;
        t = t.TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(c => $"{c}{c}"));
        color = Color.FromRgb(
            byte.Parse(t[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(t[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(t[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Luminância relativa (0 = preto, 1 = branco).</summary>
    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Razão de contraste entre duas cores (1 a 21).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static readonly Color NearBlack = Color.FromRgb(0x0B, 0x12, 0x16);
    public static readonly Color NearWhite = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>Texto claro ou escuro, o que tiver mais contraste sobre a cor dada.</summary>
    public static Color BestTextOn(Color background) =>
        ContrastRatio(background, NearBlack) >= ContrastRatio(background, NearWhite) ? NearBlack : NearWhite;

    /// <summary>Mistura linear entre duas cores (t = 0 → a, t = 1 → b).</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    /// <summary>
    /// Ajusta a cor (escurecendo ou clareando aos poucos) até atingir o contraste mínimo sobre o fundo.
    /// Usada para textos na cor de destaque: um ciano claro vira um ciano mais escuro no tema claro.
    /// </summary>
    public static Color EnsureContrast(Color color, Color background, double minRatio)
    {
        if (ContrastRatio(color, background) >= minRatio) return color;
        var target = RelativeLuminance(background) > 0.5 ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255);
        for (int i = 1; i <= 20; i++)
        {
            var candidate = Mix(color, target, i * 0.05);
            if (ContrastRatio(candidate, background) >= minRatio) return candidate;
        }
        return target;
    }
}
