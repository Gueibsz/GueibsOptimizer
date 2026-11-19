using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase95Tests
{
    [Fact]
    public void KnownFeaturesMatchTheSpec()
    {
        Assert.Equal(["NetFx3", "Microsoft-Hyper-V-All", "VirtualMachinePlatform", "Microsoft-Windows-Subsystem-Linux",
            "Containers-DisposableClientVM", "Recall", "WindowsMediaPlayer", "TelnetClient"], WindowsFeatureService.Known.Select(k => k.FeatureName));
        // Recursos que ligam o hipervisor avisam sobre jogos e anti-cheat.
        foreach (var id in new[] { "Microsoft-Hyper-V-All", "VirtualMachinePlatform", "Containers-DisposableClientVM" })
            Assert.Contains("anti-cheat", WindowsFeatureService.Known.Single(k => k.FeatureName == id).Warning);
    }

    [Theory]
    [InlineData(1, FeatureState.Enabled)]
    [InlineData(2, FeatureState.Disabled)]
    [InlineData(3, FeatureState.Unavailable)]
    [InlineData(null, FeatureState.Unavailable)]
    [InlineData(9, FeatureState.Unknown)]
    public void InstallStateMapping(int? wmi, FeatureState expected) => Assert.Equal(expected, WindowsFeatureService.MapInstallState(wmi));

    [Fact]
    public void ExactDismCommands()
    {
        Assert.Equal("DISM /Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart", WindowsFeatureService.CommandText("NetFx3", true));
        Assert.Equal("DISM /Online /Disable-Feature /FeatureName:TelnetClient /NoRestart", WindowsFeatureService.CommandText("TelnetClient", false));
    }

    [Theory]
    [InlineData(0, false, true, false)]
    [InlineData(3010, false, true, true)]
    [InlineData(87, false, false, false)]
    [InlineData(5, true, true, false)]
    public void ExitCodes(int code, bool simulated, bool success, bool reboot)
    {
        var r = WindowsFeatureService.Interpret(code, simulated, enable: true);
        Assert.Equal(success, r.Success);
        Assert.Equal(reboot, r.NeedsReboot);
        Assert.Equal(simulated, r.Simulated);
    }

    [Fact]
    public async Task UnknownFeatureIsRefusedAndDryRunChangesNothing()
    {
        using var key = new TestRegistryKey();
        var svc = new WindowsFeatureService(key.CreateContext(dryRun: true));
        var r = await svc.SetAsync(new FeatureDefinition("Qualquer; rm", "x", "x", ""), true);
        Assert.False(r.Success);
        var sim = await svc.SetAsync(WindowsFeatureService.Known[7], true);
        Assert.True(sim.Simulated);
        Assert.Empty(key.Store.ForTweak(WindowsFeatureService.UndoPrefix + "TelnetClient"));
    }

    [Fact]
    public void ReadingStatesNeverThrows()
    {
        using var key = new TestRegistryKey();
        var states = new WindowsFeatureService(key.CreateContext(dryRun: true)).ReadStates();
        Assert.Equal(WindowsFeatureService.Known.Count, states.Count);
    }
}
