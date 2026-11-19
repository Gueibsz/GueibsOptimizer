using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase97Tests
{
    private static readonly DateTime Now = new(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SecurityOnlyDefersOnlyFeatureUpdates()
    {
        var v = WindowsUpdateModeService.ValuesFor(UpdateMode.SecurityOnly, Now, 7);
        Assert.Equal(["DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays"], v.Select(x => x.Name));
        Assert.Equal(365, v[1].Value.Number);
        Assert.All(v, x => Assert.Equal(WindowsUpdateModeService.PolicyKey, x.Key));
        // Nunca desativa atualizações nem adia as de qualidade/segurança.
        Assert.DoesNotContain(v, x => x.Name is "NoAutoUpdate" or "DeferQualityUpdates" or "DeferQualityUpdatesPeriodInDays");
    }

    [Theory]
    [InlineData(7, "2026-11-26T03:00:00Z")]
    [InlineData(35, "2026-12-24T03:00:00Z")]
    [InlineData(90, "2026-12-24T03:00:00Z")] // limite de 35 dias
    [InlineData(0, "2026-11-20T03:00:00Z")]
    public void PauseIsLimitedTo35Days(int days, string expectedEnd)
    {
        var v = WindowsUpdateModeService.ValuesFor(UpdateMode.Paused, Now, days);
        Assert.Equal(expectedEnd, v.Single(x => x.Name == "PauseUpdatesExpiryTime").Value.Text);
        Assert.Equal("2026-11-19T03:00:00Z", v.Single(x => x.Name == "PauseQualityUpdatesStartTime").Value.Text);
        Assert.Equal(5, v.Count);
    }

    [Fact]
    public void DefaultWritesNothing() => Assert.Empty(WindowsUpdateModeService.ValuesFor(UpdateMode.Default, Now, 7));

    [Fact]
    public void ParsesDates()
    {
        Assert.Equal(new DateTime(2026, 12, 24, 3, 0, 0), WindowsUpdateModeService.ParseUtc("2026-12-24T03:00:00Z"));
        Assert.Null(WindowsUpdateModeService.ParseUtc("lixo"));
        Assert.Equal(new DateTime(2026, 11, 3), WindowsUpdateModeService.ParseHotfixDate("11/3/2026"));
        Assert.Null(WindowsUpdateModeService.ParseHotfixDate(""));
    }

    [Fact]
    public void StateTextShowsEndDate()
    {
        var s = new UpdateModeState(UpdateMode.Paused, new DateTime(2026, 12, 24, 3, 0, 0, DateTimeKind.Utc), 0);
        Assert.StartsWith("Pausado até ", s.Text);
        Assert.Contains("fora do app", new UpdateModeState(UpdateMode.Paused, new DateTime(3000, 3, 27, 0, 0, 0, DateTimeKind.Utc), 0).Text);
        Assert.DoesNotContain("fora do app", new UpdateModeState(UpdateMode.Paused, DateTime.UtcNow.AddDays(10), 0).Text);
        Assert.Equal("Somente segurança (atualizações de recursos adiadas 365 dias)", new UpdateModeState(UpdateMode.SecurityOnly, null, 365).Text);
    }

    [Fact]
    public void DryRunApplyChangesNothingAndReadsRealState()
    {
        using var key = new TestRegistryKey();
        var svc = new WindowsUpdateModeService(key.CreateContext(dryRun: true));
        var before = svc.ReadState();
        svc.Apply(UpdateMode.Paused, 35);
        Assert.Equal(before, svc.ReadState());
        Assert.Empty(key.Store.ForTweak(WindowsUpdateModeService.BackupId));
    }
}
