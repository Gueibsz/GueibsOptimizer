using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class ScoreTests
{
    [Fact]
    public void SumsAppliedPointsWhenTotalIs100()
    {
        var score = ScoreCalculator.Calculate([(40, true, true), (35, true, false), (25, true, true)]);
        Assert.Equal(65, score);
    }

    [Fact]
    public void NothingAppliedIsZeroAndEverythingIs100()
    {
        Assert.Equal(0, ScoreCalculator.Calculate([(50, true, false), (50, true, false)]));
        Assert.Equal(100, ScoreCalculator.Calculate([(50, true, true), (50, true, true)]));
    }

    [Fact]
    public void NotApplicableTweaksAreExcludedAndScoreIsRescaled()
    {
        // 20 pts não aplicáveis: o total vira 80, e 40 aplicados = 50%.
        var score = ScoreCalculator.Calculate([(40, true, true), (40, true, false), (20, false, false)]);
        Assert.Equal(50, score);
    }

    [Fact]
    public void EmptyListIsZero() => Assert.Equal(0, ScoreCalculator.Calculate([]));

    [Theory]
    [InlineData(0, ScoreBand.Low)]
    [InlineData(39, ScoreBand.Low)]
    [InlineData(40, ScoreBand.Medium)]
    [InlineData(74, ScoreBand.Medium)]
    [InlineData(75, ScoreBand.High)]
    [InlineData(100, ScoreBand.High)]
    public void BandsMatchColorThresholds(int score, ScoreBand expected) =>
        Assert.Equal(expected, ScoreCalculator.BandFor(score));

    [Fact]
    public void ServiceReadsRealStateAndSurvivesExceptions()
    {
        var tweaks = new ITweak[]
        {
            new FakeTweak("a", 60, applied: true),
            new FakeTweak("b", 40),
            new ThrowingTweak()
        };
        var result = new ScoreService(new Logger(null)).Evaluate(tweaks);
        Assert.Equal(60, result.AppliedPoints);
        Assert.Equal(110, result.TotalPoints);
        Assert.Equal(55, result.Score);
    }

    private sealed class ThrowingTweak : ITweak
    {
        public string Id => "throw";
        public string Name => "Quebrado";
        public string Description => "";
        public TweakCategory Category => TweakCategory.Windows;
        public TweakRisk Risk => TweakRisk.Low;
        public int Points => 10;
        public string? Notice => null;
        public bool IsApplicable() => true;
        public bool IsApplied() => throw new UnauthorizedAccessException();
        public Task ApplyAsync() => Task.CompletedTask;
        public Task RevertAsync() => Task.CompletedTask;
    }
}
