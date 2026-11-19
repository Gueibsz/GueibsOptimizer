using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase96Tests
{
    [Fact]
    public void ActionsShowExactCommands()
    {
        var keys = FixesService.Actions.Select(a => a.Key).ToArray();
        Assert.Equal(["network", "windows-update", "store-cache", "icon-cache"], keys);
        var net = FixesService.Actions[0];
        Assert.Equal(["netsh winsock reset", "netsh int ip reset", "ipconfig /flushdns", "ipconfig /release", "ipconfig /renew"], net.Commands);
        Assert.True(net.NeedsReboot);
        Assert.Equal(TweakRisk.Medium, net.Risk);
        Assert.Contains("wsreset", FixesService.Actions[2].Commands);
    }

    [Fact]
    public void WindowsUpdateResetRenamesNeverDeletesAndRestartsServices()
    {
        var wu = FixesService.Actions[1];
        Assert.DoesNotContain(wu.Commands, c => c.StartsWith("del ", StringComparison.OrdinalIgnoreCase) || c.StartsWith("rd ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wu.Commands, c => c.StartsWith("ren ") && c.Contains("SoftwareDistribution"));
        Assert.Contains(wu.Commands, c => c.StartsWith("ren ") && c.Contains("catroot2"));
        Assert.All(FixesService.UpdateServices, s => Assert.Contains($"net start {s}", wu.Commands));
        Assert.Equal("SoftwareDistribution.bak-20261119-0130", FixesService.BackupName("SoftwareDistribution", new DateTime(2026, 11, 19, 1, 30, 0)));
    }

    [Fact]
    public async Task DryRunSimulatesEveryStepAndStillStartsServices()
    {
        using var key = new TestRegistryKey();
        var svc = new FixesService(key.CreateContext(dryRun: true));
        var lines = new List<string>();
        var wu = await svc.RunAsync(FixesService.Actions[1], new SyncProgress(lines), CancellationToken.None);
        Assert.All(wu, r => Assert.True(r.Success));
        Assert.Equal(FixesService.UpdateServices.Length * 2 + 2, wu.Count);
        Assert.EndsWith("start wuauserv", wu[^1].Display);
        var net = await svc.RunAsync(FixesService.Actions[0], new SyncProgress(lines), CancellationToken.None);
        Assert.Equal(5, net.Count);
    }

    [Fact]
    public async Task CancelledWindowsUpdateResetStillRestartsServices()
    {
        using var key = new TestRegistryKey();
        var svc = new FixesService(key.CreateContext(dryRun: true));
        var logged = new List<string>();
        key.Logger.EntryLogged += e => logged.Add(e.Message);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunAsync(FixesService.Actions[1], null, cts.Token));
        Assert.Contains(logged, m => m.Contains("net start wuauserv"));
    }

    [Fact]
    public async Task SummaryAndReport()
    {
        var a = FixesService.Actions[0];
        Assert.Equal("5 de 5 etapa(s) concluídas. Reinicie o PC para concluir.",
            FixesService.FinalSummary(a, Enumerable.Range(0, 5).Select(i => new FixStepResult($"s{i}", true, "")).ToList(), false));
        Assert.StartsWith("Cancelado", FixesService.FinalSummary(a, [], true));
        Assert.Equal("c\nd", FixesService.Summarize("a\n\nb\nc\nd", 2).Replace("\r", string.Empty));
        var w = new System.IO.StringWriter();
        await FixesService.WriteReportAsync(w);
        Assert.Contains("netsh winsock reset", w.ToString());
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
