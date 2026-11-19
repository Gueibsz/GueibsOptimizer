using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

public enum ScoreBand
{
    Low,
    Medium,
    High
}

public sealed record TweakState(ITweak Tweak, bool Applicable, bool Applied);

public sealed record ScoreResult(int Score, int AppliedPoints, int TotalPoints, IReadOnlyList<TweakState> States)
{
    public ScoreBand Band => ScoreCalculator.BandFor(Score);
}

/// <summary>Cálculo puro da pontuação (testado em xUnit).</summary>
public static class ScoreCalculator
{
    /// <summary>
    /// Soma dos pontos dos ajustes aplicados. Os pesos somam 100; ajustes "não aplicáveis"
    /// nesta versão do Windows saem do total, e o resultado é reescalado para 0–100.
    /// </summary>
    public static int Calculate(IEnumerable<(int Points, bool Applicable, bool Applied)> items)
    {
        int total = 0, applied = 0;
        foreach (var (points, applicable, isApplied) in items)
        {
            if (!applicable || points <= 0) continue;
            total += points;
            if (isApplied) applied += points;
        }
        if (total == 0) return 0;
        return (int)Math.Round(applied * 100.0 / total, MidpointRounding.AwayFromZero);
    }

    public static ScoreBand BandFor(int score) => score switch
    {
        < 40 => ScoreBand.Low,
        < 75 => ScoreBand.Medium,
        _ => ScoreBand.High
    };
}

public sealed class ScoreService(Logger logger)
{
    /// <summary>Lê o estado real de cada ajuste fora da thread da UI.</summary>
    public Task<ScoreResult> EvaluateAsync(IEnumerable<ITweak> tweaks) => Task.Run(() => Evaluate(tweaks));

    public ScoreResult Evaluate(IEnumerable<ITweak> tweaks)
    {
        var states = tweaks.Select(ReadState).ToList();
        int score = ScoreCalculator.Calculate(states.Select(s => (s.Tweak.Points, s.Applicable, s.Applied)));
        int total = states.Where(s => s.Applicable).Sum(s => s.Tweak.Points);
        int applied = states.Where(s => s.Applicable && s.Applied).Sum(s => s.Tweak.Points);
        return new ScoreResult(score, applied, total, states);
    }

    private TweakState ReadState(ITweak tweak)
    {
        try
        {
            bool applicable = tweak.IsApplicable();
            return new TweakState(tweak, applicable, applicable && tweak.IsApplied());
        }
        catch (Exception ex)
        {
            logger.Error($"Não foi possível ler o estado de \"{tweak.Name}\"", ex);
            return new TweakState(tweak, true, false);
        }
    }
}
