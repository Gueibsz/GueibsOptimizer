using System.Management;
using System.Runtime.InteropServices;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>Resumo dos pontos de restauração existentes (somente leitura).</summary>
public sealed record RestorePointsInfo(int Count, DateTime? Last, string? Error = null)
{
    public string Text => Error is not null ? Error
        : Count == 0 ? "Nenhum ponto de restauração encontrado."
        : $"{Count} ponto(s) · último em {Last:dd/MM/yyyy HH:mm}";

    /// <summary>Monta o resumo a partir das datas de criação (formato DMTF do WMI, ex.: 20261118093000.000000-180).</summary>
    public static RestorePointsInfo FromDmtf(IEnumerable<string> creationTimes)
    {
        var dates = new List<DateTime>();
        foreach (var t in creationTimes)
        {
            try { dates.Add(ManagementDateTimeConverter.ToDateTime(t)); }
            catch (ArgumentOutOfRangeException) { }
        }
        return new RestorePointsInfo(dates.Count, dates.Count == 0 ? null : dates.Max());
    }
}

/// <summary>Lista os pontos de restauração pela classe WMI root\default:SystemRestore (exige administrador).</summary>
public static class RestorePointReader
{
    public static Task<RestorePointsInfo> ReadAsync() => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"),
                new ObjectQuery("SELECT CreationTime FROM SystemRestore"));
            var times = new List<string>();
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    if (o["CreationTime"] is string s) times.Add(s);
            return RestorePointsInfo.FromDmtf(times);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return new RestorePointsInfo(0, null, "Não foi possível ler os pontos de restauração (Proteção do Sistema desligada?).");
        }
    });
}

/// <summary>Uso total de CPU medido entre duas leituras de GetSystemTimes (sem contadores de desempenho).</summary>
public sealed class CpuUsageSampler
{
    private (ulong Idle, ulong Kernel, ulong User)? _last;

    /// <summary>% de uso desde a leitura anterior; null na primeira leitura ou se a API falhar.</summary>
    public double? Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var now = (ToUlong(idle), ToUlong(kernel), ToUlong(user));
        var prev = _last;
        _last = now;
        return prev is { } p ? Compute(p, now) : null;
    }

    /// <summary>Kernel inclui o tempo ocioso: uso = 1 − Δocioso / (Δkernel + Δusuário).</summary>
    public static double? Compute((ulong Idle, ulong Kernel, ulong User) before, (ulong Idle, ulong Kernel, ulong User) after)
    {
        double idle = after.Idle - before.Idle;
        double total = (after.Kernel - before.Kernel) + (after.User - before.User);
        if (total <= 0) return null;
        return Math.Clamp((1 - idle / total) * 100, 0, 100);
    }

    private static ulong ToUlong(FILETIME f) => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}

/// <summary>Textos do Painel (selo, saudação, linha-resumo). Regras puras, testadas.</summary>
public static class DashboardText
{
    public enum BadgeLevel { Attention, AlmostThere, Optimized }

    public static BadgeLevel Badge(int score) => score >= 90 ? BadgeLevel.Optimized : score >= 60 ? BadgeLevel.AlmostThere : BadgeLevel.Attention;

    public static string BadgeText(int score) => Badge(score) switch
    {
        BadgeLevel.Optimized => "Sistema otimizado",
        BadgeLevel.AlmostThere => "Quase lá",
        _ => "Precisa de atenção"
    };

    public static string Greeting(string? displayName, string windowsUser)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? windowsUser : displayName.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Olá!" : $"Olá, {name}!";
    }

    public static string SummaryLine(int applied, int applicable, int memoryPercent, long? cleanupBytes) =>
        $"Otimizações {applied}/{applicable} · memória {memoryPercent}% em uso · limpeza " +
        (cleanupBytes is { } b ? FormatGb(b) : "calculando...");

    public static string FormatGb(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    public static int MemoryPercent(ulong available, ulong total) =>
        total == 0 ? 0 : (int)Math.Round((1 - available / (double)total) * 100);
}
