using System.IO;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class FrameStatsTests
{
    [Fact]
    public void ConstantFrameRateGivesSameAverageAndLows()
    {
        var stats = new FrameStats();
        for (int i = 0; i < 600; i++) stats.Add(1000.0 / 60);
        var s = stats.Snapshot();
        Assert.Equal(60, s.AverageFps, 3);
        Assert.Equal(60, s.Low1Fps, 3);
        Assert.Equal(60, s.Low01Fps, 3);
    }

    [Fact]
    public void StuttersPullDownLowsButBarelyTheAverage()
    {
        var stats = new FrameStats(windowSeconds: 20); // cabe a sequência inteira (10,4 s)
        // 990 quadros a 10 ms (100 FPS) + 10 travadas de 50 ms (1%).
        for (int i = 0; i < 1000; i++) stats.Add(i % 100 == 0 ? 50 : 10);
        var s = stats.Snapshot();
        Assert.InRange(s.AverageFps, 95, 97);           // 1000 / 10,4 s
        Assert.Equal(20, s.Low1Fps, 1);                 // os 1% piores são exatamente as travadas de 50 ms
        Assert.Equal(20, s.Low01Fps, 1);
    }

    [Fact]
    public void LowsAreAverageOfWorstFrames()
    {
        // 1000 quadros: 980 a 10 ms, 10 a 20 ms, 10 a 40 ms → 1% piores = 10 quadros de 40 ms (25 FPS).
        var frames = Enumerable.Repeat(10.0, 980).Concat(Enumerable.Repeat(20.0, 10)).Concat(Enumerable.Repeat(40.0, 10)).ToArray();
        Array.Sort(frames);
        Assert.Equal(25, FrameStats.WorstFractionFps(frames, 0.01), 3);
        Assert.Equal(25, FrameStats.WorstFractionFps(frames, 0.001), 3); // no mínimo 1 quadro
    }

    [Fact]
    public void WindowSlidesByTime()
    {
        var stats = new FrameStats(windowSeconds: 1);
        for (int i = 0; i < 200; i++) stats.Add(10);   // 2 s de quadros a 100 FPS
        for (int i = 0; i < 50; i++) stats.Add(20);    // 1 s a 50 FPS
        var s = stats.Snapshot();
        Assert.Equal(50, s.AverageFps, 1);
        Assert.Equal(50, s.Frames);
    }

    [Fact]
    public void IgnoresInvalidFrames()
    {
        var stats = new FrameStats();
        stats.Add(double.NaN);
        stats.Add(-1);
        stats.Add(0);
        stats.Add(99999);
        Assert.Equal(FpsSnapshot.Empty, stats.Snapshot());
    }

    [Fact]
    public void PercentileInterpolates()
    {
        Assert.Equal(15, FrameStats.Percentile([10, 20], 0.5));
        Assert.Equal(20, FrameStats.Percentile([10, 20], 1));
        Assert.Equal(0, FrameStats.Percentile([], 0.5));
    }
}

public class PresentMonTests
{
    [Fact]
    public void DetectsV1Column()
    {
        var csv = new PresentMonCsv();
        Assert.Null(csv.ParseLine("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange"));
        Assert.Equal("MsBetweenPresents", csv.FrameColumn);
        Assert.Equal(16.667, csv.ParseLine("game.exe,1234,0x0,DXGI,0,0,0,1.5,16.667,16.667"));
    }

    [Fact]
    public void DetectsV2FrameTimeColumnAndFiltersProcess()
    {
        var csv = new PresentMonCsv();
        csv.ParseLine("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy");
        Assert.Equal("FrameTime", csv.FrameColumn);
        Assert.Equal(6.9, csv.ParseLine("game.exe,42,0x1,DXGI,0,0,1,Hardware: Independent Flip,Application,10.0,6.9,3.1", 42));
        Assert.Null(csv.ParseLine("outro.exe,7,0x1,DXGI,0,0,1,Hardware: Independent Flip,Application,10.0,6.9,3.1", 42));
    }

    [Fact]
    public void IgnoresMalformedAndNonNumericRows()
    {
        var csv = new PresentMonCsv();
        csv.ParseLine("Application,ProcessID,MsBetweenPresents");
        Assert.Null(csv.ParseLine("curta"));
        Assert.Null(csv.ParseLine("game.exe,1,NA"));
        Assert.Null(csv.ParseLine(""));
    }

    /// <summary>Captura real do PresentMon 2.6.0 (dwm.exe, 3 s) feita com os mesmos argumentos do app.</summary>
    [Fact]
    public void ParsesRealPresentMon26Capture()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Data", "presentmon-2.6-dwm.csv"));
        int pid = int.Parse(lines[1].Split(',')[1]);
        var csv = new PresentMonCsv();
        var stats = new FrameStats();
        var frames = new List<double>();
        foreach (var line in lines)
            if (csv.ParseLine(line, pid) is { } ms) { frames.Add(ms); stats.Add(ms); }

        Assert.Equal("MsBetweenPresents", csv.FrameColumn);
        Assert.Equal(lines.Length - 1, frames.Count);
        var s = stats.Snapshot();
        Assert.Equal(frames.Count * 1000.0 / frames.Sum(), s.AverageFps, 6);
        Assert.InRange(s.AverageFps, 20, 300);
        Assert.True(s.Low1Fps <= s.AverageFps);
    }

    [Fact]
    public void FollowsOnlyTheMainSwapChain()
    {
        var csv = new PresentMonCsv();
        csv.ParseLine("Application,ProcessID,SwapChainAddress,MsBetweenPresents");
        var accepted = new List<double>();
        // Jogo (0xA) a ~7 ms; uma janela auxiliar (0xB) a 100 ms aparece primeiro.
        if (csv.ParseLine("game.exe,1,0xB,100", 1) is { } first) accepted.Add(first);
        for (int i = 0; i < 20; i++)
        {
            if (csv.ParseLine("game.exe,1,0xA,7", 1) is { } a) accepted.Add(a);
            if (i % 5 == 0 && csv.ParseLine("game.exe,1,0xB,100", 1) is { } b) accepted.Add(b);
        }
        // 0xA assume a liderança no 3º quadro; a partir daí só os quadros dele entram.
        Assert.Equal(18, accepted.Count(x => x == 7));
        Assert.True(accepted.Count(x => x == 100) <= 2);
    }

    [Theory]
    [InlineData("PresentMon 2.3.1", 2)]
    [InlineData("PresentMon64-1.10.0 usage...", 1)]
    public void ParsesVersion(string text, int major) => Assert.Equal(major, PresentMonCsv.ParseVersion(text)!.Major);

    [Fact]
    public void ArgumentStyleDependsOnVersion()
    {
        var v2 = PresentMonCsv.CaptureArguments(new Version(2, 3, 0), "Game");
        Assert.Contains("--process_name", v2);
        Assert.Contains("Game.exe", v2);
        Assert.Contains("--output_stdout", v2);
        var v1 = PresentMonCsv.CaptureArguments(new Version(1, 10, 0), "game.exe");
        Assert.Contains("-process_name", v1);
        Assert.DoesNotContain("--process_name", v1);
        Assert.Contains("game.exe", v1);
    }
}

public class NetworkTests
{
    [Fact]
    public void PingStatsComputeJitterAndLoss()
    {
        var s = PingStats.From("1.1.1.1", [10, 14, null, 12, null]);
        Assert.Equal(5, s.Sent);
        Assert.Equal(3, s.Received);
        Assert.Equal(40, s.LossPercent);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(14, s.MaxMs);
        Assert.Equal(12, s.AvgMs);
        Assert.Equal(3, s.JitterMs); // |14-10| e |12-14| → média 3
    }

    [Fact]
    public void AllLostIsFullLoss()
    {
        var s = PingStats.From("x", [null, null]);
        Assert.Equal(100, s.LossPercent);
        Assert.Contains("100%", s.Summary);
    }

    [Fact]
    public void TargetsAreSanitized()
    {
        var t = NetworkTestService.ParseTargets("1.1.1.1, 8.8.8.8; google.com 1.1.1.1 \"x&calc\" ::1");
        Assert.Equal(["1.1.1.1", "8.8.8.8", "google.com", "::1"], t);
    }

    [Fact]
    public void MbpsMath() => Assert.Equal(80, NetworkTestService.Mbps(10_000_000, TimeSpan.FromSeconds(1)), 3);
}

public class PollingTests
{
    [Fact]
    public void ThousandHzIsDetected()
    {
        const long freq = 10_000_000; // 100 ns por tick
        var calc = new PollingCalculator(freq);
        long t = 1;
        for (int i = 0; i < 2000; i++) { calc.Add(t); t += freq / 1000; }
        Assert.Equal(1000, calc.AverageHz, 0);
        Assert.InRange(calc.CurrentHz, 990, 1010);
        Assert.Equal(1000, PollingCalculator.NearestStandard(calc.AverageHz));
    }

    [Fact]
    public void PausesDoNotLowerTheAverage()
    {
        const long freq = 10_000_000;
        var calc = new PollingCalculator(freq);
        long t = 1;
        for (int i = 0; i < 500; i++) { calc.Add(t); t += freq / 500; }
        t += freq * 3; // usuário parou o mouse por 3 s
        for (int i = 0; i < 500; i++) { calc.Add(t); t += freq / 500; }
        Assert.Equal(500, calc.AverageHz, 0);
    }

    [Theory]
    [InlineData(118, 125)]
    [InlineData(940, 1000)]
    [InlineData(3700, 4000)]
    [InlineData(7600, 8000)]
    [InlineData(0, 0)]
    public void NearestStandardRate(double hz, int expected) => Assert.Equal(expected, PollingCalculator.NearestStandard(hz));

    [Fact]
    public void RefreshEstimatorUsesMedian()
    {
        var est = new RefreshEstimator();
        for (int i = 0; i < 100; i++) est.AddInterval(1000.0 / 144);
        est.AddInterval(100); // engasgo isolado
        Assert.Equal(144, est.EstimatedHz, 0);
    }
}
