using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class BoostServiceTests
{
    private static (BoostService Service, FakeRestorePoints Rp) Create(IEnumerable<ITweak> tweaks, bool restoreOk = true)
    {
        var logger = new Logger(null);
        var rp = new FakeRestorePoints(restoreOk);
        var service = new BoostService(new TweakRegistry(tweaks), new ScoreService(logger), rp, logger, dryRun: false);
        return (service, rp);
    }

    [Fact]
    public async Task AppliesRecommendedSkipsHighRiskAndReportsBeforeAfter()
    {
        var low = new FakeTweak("low", 50);
        var medium = new FakeTweak("medium", 30, TweakRisk.Medium);
        var high = new FakeTweak("high", 20, TweakRisk.High);
        var (service, rp) = Create([low, medium, high]);

        var summary = await service.RunAsync(_ => Task.FromResult(false));

        Assert.Equal(1, rp.Calls);
        Assert.Equal(0, summary.ScoreBefore);
        Assert.Equal(80, summary.ScoreAfter);
        Assert.Equal(0, high.ApplyCalls);
        Assert.Equal(2, summary.AppliedCount);
    }

    [Fact]
    public async Task FailureInOneTweakDoesNotStopOthers()
    {
        var broken = new FakeTweak("broken", 50, failOnApply: true);
        var ok = new FakeTweak("ok", 50);
        var (service, _) = Create([broken, ok]);

        var summary = await service.RunAsync(_ => Task.FromResult(true));

        Assert.True(ok.Applied);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(50, summary.ScoreAfter);
    }

    [Fact]
    public async Task RestorePointFailureAsksAndCanAbort()
    {
        var tweak = new FakeTweak("a", 100);
        var (service, _) = Create([tweak], restoreOk: false);
        bool asked = false;

        var summary = await service.RunAsync(_ => { asked = true; return Task.FromResult(false); });

        Assert.True(asked);
        Assert.True(summary.Cancelled);
        Assert.Equal(0, tweak.ApplyCalls);
    }

    [Fact]
    public async Task RestorePointFailureCanContinueWhenUserConfirms()
    {
        var tweak = new FakeTweak("a", 100);
        var (service, _) = Create([tweak], restoreOk: false);

        var summary = await service.RunAsync(_ => Task.FromResult(true));

        Assert.True(tweak.Applied);
        Assert.Equal(100, summary.ScoreAfter);
    }

    [Fact]
    public async Task AlreadyAppliedAndNotApplicableAreNotReapplied()
    {
        var done = new FakeTweak("done", 50, applied: true);
        var na = new FakeTweak("na", 50, applicable: false);
        var (service, _) = Create([done, na]);

        var summary = await service.RunAsync(_ => Task.FromResult(true));

        Assert.Equal(0, done.ApplyCalls);
        Assert.Equal(0, na.ApplyCalls);
        Assert.Contains(summary.Steps, s => s.Status == BoostStepStatus.AlreadyApplied);
        Assert.Contains(summary.Steps, s => s.Status == BoostStepStatus.NotApplicable);
    }
}
