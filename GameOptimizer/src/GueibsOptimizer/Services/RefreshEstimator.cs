namespace GueibsOptimizer.Services;

/// <summary>Estima a taxa de quadros real da composição a partir dos intervalos entre frames (mediana, robusta a engasgos).</summary>
public sealed class RefreshEstimator(int capacity = 240)
{
    private readonly Queue<double> _intervals = new();

    public void AddInterval(double ms)
    {
        if (ms <= 0.5 || ms > 200) return;
        _intervals.Enqueue(ms);
        while (_intervals.Count > capacity) _intervals.Dequeue();
    }

    public double EstimatedHz
    {
        get
        {
            if (_intervals.Count < 10) return 0;
            var sorted = _intervals.OrderBy(x => x).ToList();
            double median = FrameStats.Percentile(sorted, 0.5);
            return median > 0 ? 1000.0 / median : 0;
        }
    }
}
