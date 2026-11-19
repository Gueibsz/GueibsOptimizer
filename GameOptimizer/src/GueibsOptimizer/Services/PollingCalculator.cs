namespace GueibsOptimizer.Services;

/// <summary>
/// Calcula a taxa de polling a partir dos timestamps (Stopwatch) de cada WM_INPUT do mouse.
/// Só conta intervalos "em movimento" (abaixo de 50 ms), ignorando pausas do usuário.
/// </summary>
public sealed class PollingCalculator
{
    public static readonly int[] StandardRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private const double MaxMovingIntervalMs = 50;

    private readonly double _ticksPerMs;
    private readonly Queue<long> _recent = new();
    private long _last;
    private double _sumIntervalsMs;
    private long _intervals;

    public PollingCalculator(long ticksPerSecond) => _ticksPerMs = ticksPerSecond / 1000.0;

    public double CurrentHz { get; private set; }
    public double AverageHz => _intervals == 0 ? 0 : 1000.0 / (_sumIntervalsMs / _intervals);
    public double MaxHz { get; private set; }
    public long Events { get; private set; }

    public void Add(long timestamp)
    {
        Events++;
        if (_last != 0)
        {
            double ms = (timestamp - _last) / _ticksPerMs;
            if (ms > 0 && ms < MaxMovingIntervalMs)
            {
                _sumIntervalsMs += ms;
                _intervals++;
            }
        }
        _last = timestamp;

        // Taxa atual: eventos nos últimos 100 ms (janela curta para reagir rápido).
        _recent.Enqueue(timestamp);
        long windowTicks = (long)(100 * _ticksPerMs);
        while (_recent.Count > 0 && timestamp - _recent.Peek() > windowTicks) _recent.Dequeue();
        if (_recent.Count >= 3)
        {
            double spanMs = (timestamp - _recent.Peek()) / _ticksPerMs;
            if (spanMs > 0)
            {
                CurrentHz = (_recent.Count - 1) * 1000.0 / spanMs;
                if (_intervals > 10) MaxHz = Math.Max(MaxHz, CurrentHz);
            }
        }
    }

    public void Reset()
    {
        _recent.Clear();
        _last = 0;
        _sumIntervalsMs = 0;
        _intervals = 0;
        CurrentHz = MaxHz = 0;
        Events = 0;
    }

    /// <summary>Taxa padrão mais próxima (125/500/1000/2000/4000/8000 Hz...).</summary>
    public static int NearestStandard(double hz) =>
        hz <= 0 ? 0 : StandardRates.OrderBy(r => Math.Abs(Math.Log(r / hz))).First();
}
