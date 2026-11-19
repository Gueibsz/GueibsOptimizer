using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

public enum BoostStepStatus
{
    Applied,
    AlreadyApplied,
    NotApplicable,
    Failed
}

public sealed record BoostStepResult(ITweak Tweak, BoostStepStatus Status, string Message);

public sealed record BoostProgress(int Done, int Total, string Message);

public sealed record BoostSummary(
    int ScoreBefore,
    int ScoreAfter,
    IReadOnlyList<BoostStepResult> Steps,
    RestorePointResult? RestorePoint,
    bool Simulated,
    bool Cancelled)
{
    public int AppliedCount => Steps.Count(s => s.Status == BoostStepStatus.Applied);
    public int FailedCount => Steps.Count(s => s.Status == BoostStepStatus.Failed);
}

/// <summary>
/// Boost em 1 Clique: ponto de restauração → ajustes recomendados (risco Baixo/Médio) → nova pontuação.
/// Falha em um ajuste não interrompe os demais.
/// </summary>
public sealed class BoostService(
    TweakRegistry registry,
    ScoreService scoreService,
    IRestorePointService restorePoints,
    Logger logger,
    bool dryRun)
{
    /// <param name="confirmWithoutRestorePoint">Chamado se o ponto falhar; retorna true para continuar mesmo assim.</param>
    public async Task<BoostSummary> RunAsync(
        Func<RestorePointResult, Task<bool>> confirmWithoutRestorePoint,
        IProgress<BoostProgress>? progress = null,
        CancellationToken ct = default,
        Func<ITweak, bool>? selector = null)
    {
        selector ??= TweakText.IsRecommended;
        var before = await scoreService.EvaluateAsync(registry.All).ConfigureAwait(false);
        var candidates = before.States.Where(s => selector(s.Tweak) && TweakText.IsRecommended(s.Tweak)).ToList();
        int total = candidates.Count + 1;

        progress?.Report(new BoostProgress(0, total, "Criando ponto de restauração..."));
        logger.Info(dryRun ? "Boost iniciado (modo simulação)." : "Boost iniciado.");

        var restorePoint = await restorePoints.CreateAsync("Gueibs Optimizer - antes do Boost", ct).ConfigureAwait(false);
        if (!restorePoint.Success && !await confirmWithoutRestorePoint(restorePoint).ConfigureAwait(false))
        {
            logger.Warn("Boost cancelado pelo usuário: sem ponto de restauração.");
            return new BoostSummary(before.Score, before.Score, [], restorePoint, dryRun, Cancelled: true);
        }

        var steps = new List<BoostStepResult>();
        int done = 1;
        foreach (var state in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var tweak = state.Tweak;
            progress?.Report(new BoostProgress(done, total, $"Aplicando: {tweak.Name}"));
            steps.Add(await ApplyOneAsync(state).ConfigureAwait(false));
            done++;
        }

        var after = await scoreService.EvaluateAsync(registry.All).ConfigureAwait(false);
        progress?.Report(new BoostProgress(total, total, "Concluído."));
        logger.Info($"Boost concluído: pontuação {before.Score} → {after.Score}.");
        return new BoostSummary(before.Score, after.Score, steps, restorePoint, dryRun, ct.IsCancellationRequested);
    }

    private async Task<BoostStepResult> ApplyOneAsync(TweakState state)
    {
        var tweak = state.Tweak;
        if (!state.Applicable)
            return new BoostStepResult(tweak, BoostStepStatus.NotApplicable, "Não aplicável nesta versão do Windows.");
        if (state.Applied)
            return new BoostStepResult(tweak, BoostStepStatus.AlreadyApplied, "Já estava aplicado.");

        try
        {
            await tweak.ApplyAsync().ConfigureAwait(false);
            logger.Info($"Aplicado: {tweak.Name}");
            return new BoostStepResult(tweak, BoostStepStatus.Applied, dryRun ? "Simulado." : "Aplicado.");
        }
        catch (Exception ex)
        {
            logger.Error($"Falha ao aplicar \"{tweak.Name}\"", ex);
            return new BoostStepResult(tweak, BoostStepStatus.Failed, ex.Message);
        }
    }
}
