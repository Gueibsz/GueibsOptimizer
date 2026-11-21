using System.Globalization;
using System.Text.RegularExpressions;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>Leitura dos nomes de CPU e GPU informados pelo Windows (ex.: "Intel(R) Core(TM) i5-7400 CPU @ 3.00GHz").</summary>
public static partial class HardwareNameParser
{
    [GeneratedRegex(@"\((R|TM|C)\)|®|™", RegexOptions.IgnoreCase)]
    private static partial Regex Trademarks();

    [GeneratedRegex(@"@\s*([\d.]+)\s*GHz", RegexOptions.IgnoreCase)]
    private static partial Regex ClockGhz();

    [GeneratedRegex(@"Core\s*Ultra\s*(\d)\s*(\d{3})([A-Z]{0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUltra();

    [GeneratedRegex(@"Core\s*i([3579])[\s-]*(\d{4,5})([A-Z]{0,3})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCore();

    [GeneratedRegex(@"\b(Pentium|Celeron|Xeon|Atom)\b[\s\w-]*?([A-Z]?\d{3,5}[A-Z]{0,3})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelOther();

    [GeneratedRegex(@"Ryzen\s+(AI\s+)?(\d)\s+(PRO\s+)?(HX\s+)?(\d{3,4})([A-Z0-9]{0,5})", RegexOptions.IgnoreCase)]
    private static partial Regex Ryzen();

    [GeneratedRegex(@"\b(Athlon|Threadripper|FX|A\d{1,2})\b[\s\w-]*?(\d{3,4}[A-Z0-9]{0,4})?", RegexOptions.IgnoreCase)]
    private static partial Regex AmdOther();

    [GeneratedRegex(@"\b(RTX|GTX|GT)\s*(\d{3,4})\s*(Ti\s*SUPER|Ti|SUPER)?", RegexOptions.IgnoreCase)]
    private static partial Regex Nvidia();

    [GeneratedRegex(@"\bRX\s*(\d{3,4})\s*(XTX|XT|GRE)?", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRx();

    [GeneratedRegex(@"\bArc\s*([AB])(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelArc();

    // Radeon dedicadas sem o padrão "RX 1234": RX Vega 56/64, Radeon VII e Radeon Pro (estação de trabalho).
    [GeneratedRegex(@"\bRX\s*Vega\s*(56|64)\b|\bRadeon\s+(VII)\b|\bRadeon\s+Pro\s+([A-Z]{0,2}\d{3,4}[A-Z]{0,2})", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonOtherDiscrete();

    // Ryzen de portáteis de jogo (ROG Ally, Legion Go): "Ryzen Z1 Extreme", "Ryzen Z2 Go".
    [GeneratedRegex(@"Ryzen\s+(Z\d)\s*(Extreme|Go|A)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenZ();

    public static string Clean(string name) =>
        Regex.Replace(Trademarks().Replace(name, string.Empty), @"\s+", " ").Trim();

    public static CpuIdentity ParseCpu(string rawName)
    {
        var name = Clean(rawName ?? string.Empty);
        int? clock = null;
        var c = ClockGhz().Match(name);
        if (c.Success && double.TryParse(c.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ghz))
            clock = (int)Math.Round(ghz * 1000);

        var ultra = IntelUltra().Match(name);
        if (ultra.Success)
        {
            string model = ultra.Groups[2].Value + ultra.Groups[3].Value.ToUpperInvariant();
            int series = ultra.Groups[2].Value[0] - '0';
            return new CpuIdentity(HardwareVendor.Intel, $"Core Ultra {ultra.Groups[1].Value}", model, series, clock, false, true,
                $"Intel Core Ultra {ultra.Groups[1].Value} {model} (Série {series})");
        }

        var core = IntelCore().Match(name);
        if (core.Success)
        {
            string digits = core.Groups[2].Value;
            string model = digits + core.Groups[3].Value.ToUpperInvariant();
            // 4 dígitos: geração = 1º dígito (2ª–9ª); 5 dígitos: 2 primeiros (10ª–14ª).
            int gen = digits.Length == 5 ? int.Parse(digits[..2], CultureInfo.InvariantCulture) : digits[0] - '0';
            return new CpuIdentity(HardwareVendor.Intel, $"Core i{core.Groups[1].Value}", model, gen, clock, false, gen >= 12,
                $"Intel Core i{core.Groups[1].Value}-{model} ({gen}ª geração)");
        }

        var ryzen = Ryzen().Match(name);
        if (ryzen.Success)
        {
            bool ai = ryzen.Groups[1].Success;
            string tier = ryzen.Groups[2].Value;
            string digits = ryzen.Groups[5].Value;
            string suffix = ryzen.Groups[6].Value.ToUpperInvariant();
            string hx = ryzen.Groups[4].Success ? "HX " : string.Empty;
            string model = hx + digits + suffix;
            bool x3d = suffix.Contains("X3D", StringComparison.Ordinal);
            // Ryzen de mesa/notebook: série pelo 1º dígito (3600 → 3000, 5800X3D → 5000). Ryzen AI: série 300.
            int? series = ai ? null : digits[0] - '0';
            string brand = ai ? $"Ryzen AI {tier}" : $"Ryzen {tier}";
            string friendly = ai ? $"AMD Ryzen AI {tier} {model}" : $"AMD Ryzen {tier} {model} (série {series}000)";
            return new CpuIdentity(HardwareVendor.Amd, brand, model, series, clock, x3d, false, friendly);
        }

        var ryzenZ = RyzenZ().Match(name);
        if (ryzenZ.Success)
        {
            string model = (ryzenZ.Groups[1].Value.ToUpperInvariant() + " " + ryzenZ.Groups[2].Value).Trim();
            return new CpuIdentity(HardwareVendor.Amd, "Ryzen", model, null, clock, false, false, $"AMD Ryzen {model}");
        }

        var intelOther = IntelOther().Match(name);
        if (intelOther.Success && name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            string model = intelOther.Groups[2].Value.ToUpperInvariant();
            string brand = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(intelOther.Groups[1].Value.ToLowerInvariant());
            return new CpuIdentity(HardwareVendor.Intel, brand, model, null, clock, false, false, $"Intel {brand} {model}".Trim());
        }

        var amdOther = AmdOther().Match(name);
        if (amdOther.Success && name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            string model = amdOther.Groups[2].Value.ToUpperInvariant();
            return new CpuIdentity(HardwareVendor.Amd, amdOther.Groups[1].Value, model, null, clock, false, false,
                $"AMD {amdOther.Groups[1].Value} {model}".Trim());
        }

        var vendor = name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? HardwareVendor.Intel
            : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? HardwareVendor.Amd
            : HardwareVendor.Unknown;
        string plain = ClockGhz().Replace(name, string.Empty).Replace(" CPU", string.Empty).Trim();
        return new CpuIdentity(vendor, plain, string.Empty, null, clock, false, false, plain.Length > 0 ? plain : "Processador desconhecido");
    }

    public static GpuIdentity ParseGpu(string rawName)
    {
        var name = Clean(rawName ?? string.Empty);

        var nv = Nvidia().Match(name);
        if (nv.Success)
        {
            string line = nv.Groups[1].Value.ToUpperInvariant();
            string digits = nv.Groups[2].Value;
            string suffix = NormalizeSuffix(nv.Groups[3].Value);
            int number = int.Parse(digits, CultureInfo.InvariantCulture);
            // 4 dígitos: geração = 2 primeiros (10, 16, 20, 30, 40, 50) e classe = 2 últimos (50 a 90).
            // 3 dígitos (GTX 970): geração = 1º dígito, classe = 2 últimos.
            int gen = number / 100;
            int cls = number % 100;
            bool reflex = line == "RTX" || (line == "GTX" && (digits.Length == 4 || gen >= 9));
            double index = line == "GT" ? 0 : NvidiaIndex(gen, cls, suffix);
            string model = (digits + " " + suffix).Trim();
            return new GpuIdentity(HardwareVendor.Nvidia, $"GeForce {line}", model, false, reflex, index, $"NVIDIA GeForce {line} {model}");
        }

        var rx = RadeonRx().Match(name);
        if (rx.Success)
        {
            string digits = rx.Groups[1].Value;
            string suffix = rx.Groups[2].Value.ToUpperInvariant();
            string model = (digits + " " + suffix).Trim();
            return new GpuIdentity(HardwareVendor.Amd, "Radeon RX", model, false, false, AmdIndex(digits, suffix), $"AMD Radeon RX {model}");
        }

        var radeonOther = RadeonOtherDiscrete().Match(name);
        if (radeonOther.Success)
        {
            // Índices aproximados: Vega 56/64 e Radeon VII ficam perto das RX 5600/5700; Radeon Pro conta como intermediária.
            if (radeonOther.Groups[1].Success)
            {
                string vega = radeonOther.Groups[1].Value;
                return new GpuIdentity(HardwareVendor.Amd, "Radeon RX Vega", vega, false, false, vega == "64" ? 2.5 : 2, $"AMD Radeon RX Vega {vega}");
            }
            if (radeonOther.Groups[2].Success)
                return new GpuIdentity(HardwareVendor.Amd, "Radeon VII", string.Empty, false, false, 3, "AMD Radeon VII");
            string pro = radeonOther.Groups[3].Value.ToUpperInvariant();
            return new GpuIdentity(HardwareVendor.Amd, "Radeon Pro", pro, false, false, 3, $"AMD Radeon Pro {pro}");
        }

        var arc = IntelArc().Match(name);
        if (arc.Success)
        {
            string series = arc.Groups[1].Value.ToUpperInvariant();
            int number = int.Parse(arc.Groups[2].Value, CultureInfo.InvariantCulture);
            // A-series = 1ª geração (base 2), B-series = 2ª (base 3); classe pelo 1º dígito (3, 5, 7).
            double index = (series == "B" ? 3 : 2) + (number / 100 - 5);
            return new GpuIdentity(HardwareVendor.Intel, "Arc", $"{series}{number}", false, false, Math.Max(0, index), $"Intel Arc {series}{number}");
        }

        // Integradas: Intel HD/UHD/Iris/"Arc Graphics" de notebook e AMD "Radeon Graphics"/Vega/7x0M.
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            string brand = name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ? "Iris Graphics"
                : name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? "Arc Graphics"
                : name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ? "UHD Graphics" : "HD Graphics";
            return new GpuIdentity(HardwareVendor.Intel, brand, string.Empty, true, false, 0, name);
        }
        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return new GpuIdentity(HardwareVendor.Amd, "Radeon Graphics", string.Empty, true, false, 0.5, name);

        return new GpuIdentity(HardwareVendor.Unknown, name, string.Empty, true, false, 0, name.Length > 0 ? name : "Placa de vídeo desconhecida");
    }

    private static string NormalizeSuffix(string s) => s.Trim().ToUpperInvariant() switch
    {
        "TI" => "Ti",
        "SUPER" => "SUPER",
        var x when x.StartsWith("TI", StringComparison.Ordinal) => "Ti SUPER",
        _ => string.Empty
    };

    /// <summary>
    /// Índice aproximado de desempenho de NVIDIA: peso da geração + peso da classe + sufixo.
    /// Geração: 9→0, 10→1, 16→1.5, 20→2, 30→3, 40→4, 50→5. Classe: x50→0, x60→1, x70→2, x80→3, x90→4.
    /// Sufixo: Ti/SUPER +0.5, Ti SUPER +0.75.
    /// </summary>
    public static double NvidiaIndex(int gen, int cls, string suffix)
    {
        double g = gen switch { <= 9 => 0, 10 => 1, 16 => 1.5, 20 => 2, 30 => 3, 40 => 4, _ => 5 };
        double c = cls switch { < 50 => -1, < 60 => 0, < 70 => 1, < 80 => 2, < 90 => 3, _ => 4 };
        double s = suffix switch { "Ti SUPER" => 0.75, "Ti" or "SUPER" => 0.5, _ => 0 };
        return Math.Max(0, g + c + s);
    }

    /// <summary>
    /// Índice aproximado de AMD Radeon RX: geração pelo 1º dígito (5000→1.5, 6000→2.5, 7000→3.5, 9000→4.5;
    /// 3 dígitos tipo RX 580 → 1), classe pelo 2º dígito (≤5 → −0.5, 6 → +1, 7 → +2, 8 → +3, 9 → +4) e XT +0.5 / XTX +1 / GRE +0.25.
    /// </summary>
    public static double AmdIndex(string digits, string suffix)
    {
        if (digits.Length == 3) return 1;
        double g = digits[0] switch { '5' => 1.5, '6' => 2.5, '7' => 3.5, '9' => 4.5, _ => 1 };
        // Série 9000 (RX 9070): a classe está no 3º dígito.
        char classDigit = digits[0] == '9' ? digits[2] : digits[1];
        double c = classDigit switch { <= '5' => -0.5, '6' => 1, '7' => 2, '8' => 3, _ => 4 };
        double s = suffix switch { "XTX" => 1, "XT" => 0.5, "GRE" => 0.25, _ => 0 };
        return Math.Max(0, g + c + s);
    }
}
