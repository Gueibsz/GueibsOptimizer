namespace GueibsOptimizer.Services;

public sealed record FpsSnapshot(double AverageFps, double Low1Fps, double Low01Fps, double LastFrameMs, int Frames)
{
    public static readonly FpsSnapshot Empty = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Estatísticas de FPS em janela deslizante por tempo (padrão: últimos 10 s de frames).
/// FPS médio = nº de frames / soma dos frame times. "1% low" = FPS médio dos 1% quadros mais lentos
/// (captura travadas mesmo quando são exatamente 1% dos quadros); "0.1% low" = idem para 0,1%.
/// </summary>
public sealed class FrameStats(double windowSeconds = 10)
{
    private readonly Queue<double> _frames = new();
    private double _sumMs;
    private double _last;
    private readonly object _sync = new();

    public void Add(double frameTimeMs)
    {
        if (double.IsNaN(frameTimeMs) || frameTimeMs <= 0 || frameTimeMs > 5000) return;
        lock (_sync)
        {
            _frames.Enqueue(frameTimeMs);
            _last = frameTimeMs;
            _sumMs += frameTimeMs;
            while (_frames.Count > 1 && _sumMs > windowSeconds * 1000)
                _sumMs -= _frames.Dequeue();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _frames.Clear();
            _sumMs = 0;
        }
    }

    public FpsSnapshot Snapshot()
    {
        double[] frames;
        double sum, last;
        lock (_sync)
        {
            frames = _frames.ToArray();
            sum = _sumMs;
            last = _last;
        }
        if (frames.Length == 0 || sum <= 0) return FpsSnapshot.Empty;

        Array.Sort(frames);
        return new FpsSnapshot(
            frames.Length * 1000.0 / sum,
            WorstFractionFps(frames, 0.01),
            WorstFractionFps(frames, 0.001),
            last,
            frames.Length);
    }

    /// <summary>FPS médio dos quadros mais lentos (fração do total, no mínimo 1 quadro). Recebe os tempos já ordenados.</summary>
    public static double WorstFractionFps(double[] sortedAscending, double fraction)
    {
        int count = Math.Max(1, (int)Math.Ceiling(sortedAscending.Length * fraction));
        double sum = 0;
        for (int i = sortedAscending.Length - count; i < sortedAscending.Length; i++) sum += sortedAscending[i];
        return sum > 0 ? count * 1000.0 / sum : 0;
    }

    /// <summary>Percentil com interpolação linear sobre valores ordenados.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double rank = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
