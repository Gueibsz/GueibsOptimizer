using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Xml.Linq;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>Ajuste com nível de desempenho declarado (em vez do mapeamento por categoria e risco).</summary>
public interface ILeveledTweak
{
    PerformanceLevel Level { get; }
}

/// <summary>Níveis de desempenho. Cada nível inclui o anterior; só o Extremo inclui ajustes de risco Alto (fora do Boost).</summary>
public enum PerformanceLevel
{
    Baixo = 1,
    Medio = 2,
    Alto = 3,
    Maximo = 4,
    /// <summary>Máximo + ajustes agressivos de desempenho (risco Alto, nunca no Boost). Não desliga proteções do Windows.</summary>
    Extremo = 5
}

/// <summary>
/// Mapeamento nível → ajustes (documentado e visível na interface), sempre filtrando o TweakRegistry:
/// Baixo = risco Baixo de Energia e Jogos; Médio = Baixo + risco Baixo de GPU e Periféricos;
/// Alto = todos os de risco Baixo (inclui Windows e Privacidade); Máximo = Alto + risco Médio (igual ao Boost).
/// Extremo = Máximo + ajustes declarados com nível Extremo (risco Alto, fora do Boost; só desempenho, nunca segurança).
/// Fora disso, ajustes de preferência (InBoost = false) e de risco Alto nunca entram em nível.
/// </summary>
public static class PerformanceLevels
{
    public static readonly PerformanceLevel[] All = [PerformanceLevel.Baixo, PerformanceLevel.Medio, PerformanceLevel.Alto, PerformanceLevel.Maximo, PerformanceLevel.Extremo];

    public static bool Includes(PerformanceLevel level, ITweak tweak)
    {
        if (IsExtreme(tweak)) return level == PerformanceLevel.Extremo;
        if (!tweak.InBoost || tweak.Risk == TweakRisk.High) return false;
        // Ajustes declarativos (9.1) trazem o nível explícito no tweaks.json.
        if (tweak is ILeveledTweak leveled) return level >= leveled.Level;
        if (tweak.Risk == TweakRisk.Medium) return level >= PerformanceLevel.Maximo;
        return level switch
        {
            PerformanceLevel.Baixo => tweak.Category is TweakCategory.Power or TweakCategory.Gaming,
            PerformanceLevel.Medio => tweak.Category is TweakCategory.Power or TweakCategory.Gaming or TweakCategory.Gpu or TweakCategory.Input,
            _ => true
        };
    }

    public static IReadOnlyList<ITweak> TweaksFor(PerformanceLevel level, IEnumerable<ITweak> tweaks) =>
        tweaks.Where(t => Includes(level, t)).ToList();

    /// <summary>
    /// Nível atual calculado pelo estado REAL: o maior nível cujos ajustes aplicáveis estão todos aplicados.
    /// Null quando nem o nível Baixo está completo.
    /// </summary>
    public static PerformanceLevel? Current(IEnumerable<TweakState> states)
    {
        var list = states.ToList();
        PerformanceLevel? current = null;
        foreach (var level in All)
        {
            var inLevel = list.Where(s => s.Applicable && Includes(level, s.Tweak)).ToList();
            if (inLevel.Count == 0 || inLevel.Any(s => !s.Applied)) break;
            // Extremo só é o nível atual se ele tiver itens próprios (senão seria igual ao Máximo).
            if (level == PerformanceLevel.Extremo && !inLevel.Any(s => IsExtreme(s.Tweak))) break;
            current = level;
        }
        return current;
    }

    /// <summary>Ajuste exclusivo do nível Extremo (só entra quando esse nível é escolhido de propósito).</summary>
    public static bool IsExtreme(ITweak tweak) => tweak is ILeveledTweak { Level: PerformanceLevel.Extremo };

    public static bool RequiresConfirmation(PerformanceLevel level) => level >= PerformanceLevel.Maximo;

    public static string Text(PerformanceLevel? level) => level switch
    {
        PerformanceLevel.Baixo => "Baixo",
        PerformanceLevel.Medio => "Médio",
        PerformanceLevel.Alto => "Alto",
        PerformanceLevel.Maximo => "Máximo",
        PerformanceLevel.Extremo => "Extremo",
        _ => "Abaixo do Baixo"
    };

    public static string Description(PerformanceLevel level) => level switch
    {
        PerformanceLevel.Baixo => "Ajustes de risco Baixo de Energia e Jogos (plano de energia, Modo de Jogo e prioridades).",
        PerformanceLevel.Medio => "Nível Baixo + ajustes de risco Baixo de GPU e Periféricos (HAGS, Game DVR, aceleração do mouse, teclado).",
        PerformanceLevel.Alto => "Todos os ajustes de risco Baixo, incluindo Windows e Privacidade.",
        PerformanceLevel.Maximo => "Nível Alto + ajustes de risco Médio. É o mesmo conjunto do Boost e pede confirmação.",
        _ => "Nível Máximo + ajustes agressivos de desempenho, de risco Alto: desligam recursos do Windows (pesquisa, SysMain, relatórios de erro, economia de energia em segundo plano) para sobrar mais para o jogo. Não desliga Defender, Firewall, UAC nem proteções do processador. Fica fora do Boost e pede confirmação dupla."
    };
}

/// <summary>Números MEDIDOS do sistema, usados no comparativo antes/depois do Boost.</summary>
public sealed record SystemSnapshot(
    DateTime TakenAt,
    int Processes,
    int RunningServices,
    ulong RamUsedBytes,
    int StartupItems,
    TimeSpan? LastBootDuration);

public sealed record ComparisonRow(string Label, string Before, string After, string Change)
{
    /// <summary>"Depois" com a diferença entre parênteses, ex.: "171 (+3)". Sem parênteses quando igual ou sem medida.</summary>
    public string AfterText => Change.Length == 0 || Change == "igual" ? After : $"{After} ({Change})";
}

public static class SnapshotComparison
{
    /// <summary>Linhas lado a lado. Itens sem medida (ex.: tempo de boot indisponível) aparecem como "não disponível".</summary>
    public static IReadOnlyList<ComparisonRow> Rows(SystemSnapshot before, SystemSnapshot after) =>
    [
        Count("Processos em execução", before.Processes, after.Processes),
        Count("Serviços em execução", before.RunningServices, after.RunningServices),
        new("RAM em uso", MemoryCleanResult.FormatBytes(before.RamUsedBytes), MemoryCleanResult.FormatBytes(after.RamUsedBytes),
            Delta((long)after.RamUsedBytes - (long)before.RamUsedBytes, v => (v > 0 ? "+" : "-") + MemoryCleanResult.FormatBytes((ulong)Math.Abs(v)))),
        Count("Itens de inicialização ativos", before.StartupItems, after.StartupItems),
        new("Último tempo de boot", Boot(before.LastBootDuration), Boot(after.LastBootDuration), string.Empty)
    ];

    private static ComparisonRow Count(string label, int before, int after) =>
        new(label, before.ToString(), after.ToString(), Delta(after - before, v => v.ToString("+0;-0")));

    private static string Delta(long diff, Func<long, string> format) => diff == 0 ? "igual" : format(diff);

    public static string Boot(TimeSpan? t) => t is { } v ? $"{v.TotalSeconds:0.0} s" : "não disponível";

    /// <summary>Lê o campo BootTime (ms) do evento 100 do log Diagnostics-Performance.</summary>
    public static TimeSpan? ParseBootEvent(string eventXml)
    {
        try
        {
            var doc = XDocument.Parse(eventXml);
            var data = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == "BootTime");
            return data is not null && long.TryParse(data.Value, out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;
        }
        catch { return null; }
    }
}

/// <summary>Seção do --dry-run: nível atual, ajustes de cada nível e o snapshot medido agora.</summary>
public static class PerformanceLevelsReport
{
    public static async Task WriteAsync(System.IO.StringWriter w, TweakRegistry registry, SystemSnapshotService snapshots)
    {
        w.WriteLine();
        w.WriteLine("== Níveis de desempenho ==");
        var states = registry.All.Select(t =>
        {
            bool applicable = false, applied = false;
            try { applicable = t.IsApplicable(); applied = applicable && t.IsApplied(); } catch { }
            return new TweakState(t, applicable, applied);
        }).ToList();
        w.WriteLine($"Nível atual (estado real): {PerformanceLevels.Text(PerformanceLevels.Current(states))}");
        foreach (var level in PerformanceLevels.All)
        {
            var inLevel = states.Where(s => PerformanceLevels.Includes(level, s.Tweak)).ToList();
            var missing = inLevel.Where(s => s.Applicable && !s.Applied).ToList();
            w.WriteLine($"{PerformanceLevels.Text(level)}: {inLevel.Count} ajustes; aplicaria {missing.Count}" +
                        (PerformanceLevels.RequiresConfirmation(level) ? " (pede confirmação)" : string.Empty));
            foreach (var s in missing) w.WriteLine($"  - {s.Tweak.Name}");
        }
        var snap = await snapshots.MeasureAsync().ConfigureAwait(false);
        w.WriteLine($"Snapshot medido agora: {snap.Processes} processos, {snap.RunningServices} serviços em execução, " +
                    $"{MemoryCleanResult.FormatBytes(snap.RamUsedBytes)} de RAM em uso, {snap.StartupItems} itens de inicialização ativos, " +
                    $"último boot {SnapshotComparison.Boot(snap.LastBootDuration)}" +
                    (snap.LastBootDuration is null ? " (o Windows não registrou o evento 100 do log Diagnostics-Performance neste PC)." : "."));
    }
}

/// <summary>Mede o snapshot do sistema. Somente leitura; cada medida falha de forma isolada.</summary>
public sealed class SystemSnapshotService(Logger logger, Func<int>? startupCounter = null)
{
    public Task<SystemSnapshot> MeasureAsync() => Task.Run(Measure);

    public SystemSnapshot Measure()
    {
        int processes = Safe(() => Process.GetProcesses().Length, "processos");
        int services = Safe(CountRunningServices, "serviços");
        var (avail, total) = MemoryCleanerService.ReadMemory();
        int startup = startupCounter is null ? 0 : Safe(startupCounter, "inicialização");
        return new SystemSnapshot(DateTime.Now, processes, services, total > avail ? total - avail : 0, startup, ReadLastBoot());
    }

    private static int CountRunningServices()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE State = 'Running'");
        using var results = searcher.Get();
        return results.Count;
    }

    private TimeSpan? ReadLastBoot()
    {
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            return record is null ? null : SnapshotComparison.ParseBootEvent(record.ToXml());
        }
        catch (Exception ex)
        {
            logger.Warn($"Tempo de boot indisponível: {ex.Message}");
            return null;
        }
    }

    private int Safe(Func<int> f, string what)
    {
        try { return f(); }
        catch (Exception ex)
        {
            logger.Warn($"Não foi possível contar {what}: {ex.Message}");
            return 0;
        }
    }
}
