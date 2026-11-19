using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>
/// Gera o relatório do --dry-run: estado atual de cada ajuste e cada alteração que o Boost faria.
/// Exige um contexto em modo simulação.
/// </summary>
public sealed class DryRunReporter(TweakRegistry registry, TweakContext context, DebloatService? debloat = null)
{
    /// <summary>Seção extra para o relatório (ex.: perfis de jogos da 7.8). Recebe o writer e escreve o que quiser.</summary>
    public List<Func<StringWriter, Task>> ExtraSections { get; } = [];

    public async Task<string> BuildReportAsync()
    {
        var text = await BuildTweaksReportAsync().ConfigureAwait(false);
        var writer = new StringWriter();
        writer.Write(text);
        if (debloat is not null) await WriteDebloatAsync(writer).ConfigureAwait(false);
        WriteCompatibility(writer);
        foreach (var section in ExtraSections) await section(writer).ConfigureAwait(false);
        return writer.ToString();
    }

    /// <summary>8.11: versão do Windows e ajustes que não existem nela.</summary>
    private void WriteCompatibility(StringWriter writer)
    {
        writer.WriteLine();
        writer.WriteLine("== Compatibilidade ==");
        writer.WriteLine($"Windows: {OsInfo.Current}");
        var notHere = registry.All.Where(t => OsInfo.RequirementText(t.Id) is not null).ToList();
        if (notHere.Count == 0) writer.WriteLine("Todos os ajustes com requisito de versão existem neste Windows.");
        foreach (var t in notHere) writer.WriteLine($"Não aplicável: {t.Name} ({OsInfo.RequirementText(t.Id)})");
    }

    /// <summary>Debloat: o que está instalado em cada categoria e o que a predefinição Equilibrado removeria.</summary>
    private async Task WriteDebloatAsync(StringWriter writer)
    {
        writer.WriteLine();
        writer.WriteLine("== Debloat (apps da Microsoft Store) ==");
        IReadOnlyList<DebloatItem> items;
        try { items = await debloat!.ListAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            writer.WriteLine($"Não foi possível listar os apps: {ex.Message}");
            return;
        }
        foreach (var group in items.GroupBy(i => i.Category))
            writer.WriteLine($"{group.First().CategoryText}: {group.Count()}");
        var plan = DebloatRules.BuildPlan(DebloatRules.PresetSelection(DebloatPreset.Equilibrado, items), includeProvisioned: false);
        writer.WriteLine($"A predefinição Equilibrado removeria {plan.ToRemove.Count} app(s):");
        foreach (var i in plan.ToRemove)
            writer.WriteLine($"    → Remove-AppxPackage {i.Package.FullName}  ({i.DisplayName}, {i.SizeText})");
        if (plan.ToRemove.Count == 0) writer.WriteLine("    (nenhum app da categoria Seguro remover está instalado)");
    }

    private async Task<string> BuildTweaksReportAsync()
    {
        if (!context.DryRun)
            throw new InvalidOperationException("O relatório de simulação exige o modo --dry-run.");

        var simulated = new List<string>();
        void Capture(LogEntry e)
        {
            if (e.Level == LogLevel.Simulation) lock (simulated) simulated.Add(e.Message);
        }

        var writer = new StringWriter();
        writer.WriteLine($"Gueibs Optimizer — relatório de simulação ({DateTime.Now:yyyy-MM-dd HH:mm})");
        writer.WriteLine("Nenhuma alteração foi aplicada.");
        writer.WriteLine();

        var score = new ScoreService(context.Logger).Evaluate(registry.All);
        writer.WriteLine($"Pontuação atual: {score.Score}/100");
        writer.WriteLine();

        context.Logger.EntryLogged += Capture;
        try
        {
            foreach (var state in score.States)
            {
                var t = state.Tweak;
                string status = !state.Applicable ? "não aplicável" : state.Applied ? "já aplicado" : "pendente";
                writer.WriteLine($"[{status}] {t.Name} ({t.Id}) — risco {TweakText.RiskText(t.Risk)}, {registry.PointsOf(t)} pts");

                if (!state.Applicable || state.Applied || !TweakText.IsRecommended(t)) continue;

                lock (simulated) simulated.Clear();
                try
                {
                    await t.ApplyAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"    ! erro na simulação: {ex.Message}");
                }
                lock (simulated)
                    foreach (var line in simulated) writer.WriteLine($"    → {line}");
            }
        }
        finally
        {
            context.Logger.EntryLogged -= Capture;
        }
        return writer.ToString();
    }
}
