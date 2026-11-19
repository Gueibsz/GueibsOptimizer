using System.IO;
using GueibsOptimizer.Services;
using GueibsOptimizer.ViewModels;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase89Tests
{
    [Fact]
    public void ParsesReadOnlyDefenderStatus()
    {
        var s = DefenderService.ParseStatus("MODE=Normal\nAV=True\nPOLICY=False\nRTP=True\nSIGDATE=2026-11-17 08:30\nSIGVER=1.421.123.0\nTAMPER=True\nEXCL=False");
        Assert.True(s.Available);
        Assert.False(s.ManagedByPolicyOrThirdParty);
        Assert.True(s.RealTimeProtection);
        Assert.Equal(new DateTime(2026, 11, 17, 8, 30, 0), s.SignaturesUpdated);
        Assert.Equal("1.421.123.0", s.SignatureVersion);
        Assert.True(s.TamperProtected);
        Assert.False(s.ExclusionPresent);
    }

    [Fact]
    public void PassiveModeIsReportedAsManaged()
    {
        var s = DefenderService.ParseStatus("MODE=Passive Mode\nAV=True\nPOLICY=False\nRTP=\nSIGDATE=\nEXCL=False");
        Assert.True(s.ManagedByPolicyOrThirdParty);
        Assert.Null(s.RealTimeProtection);
        Assert.Null(s.SignaturesUpdated);
        Assert.Contains(DefenderAiViewModel.BuildRows(s), r => r.Label == "Modo do antivírus" && r.IsProblem);
    }

    [Fact]
    public void RowsFlagRealTimeOffAndOldDefinitions()
    {
        var s = DefenderService.ParseStatus($"MODE=Normal\nAV=True\nPOLICY=False\nRTP=False\nSIGDATE={DateTime.Now.AddDays(-20):yyyy-MM-dd HH:mm}\nEXCL=True");
        var rows = DefenderAiViewModel.BuildRows(s);
        Assert.True(rows.Single(r => r.Label == "Proteção em tempo real").IsProblem);
        Assert.True(rows.Single(r => r.Label == "Definições atualizadas em").IsProblem);
        Assert.Equal("presente", rows.Single(r => r.Label == "Exclusão da pasta do app").Value);
    }

    [Fact]
    public void NoDefenderShowsSingleRow()
    {
        var rows = DefenderAiViewModel.BuildRows(DefenderService.ParseStatus("NODEFENDER"));
        Assert.Single(rows);
        Assert.True(rows[0].IsProblem);
    }

    [Fact]
    public void AppNeverHasCodeToDisableDefender()
    {
        // Regra inegociável 4: nada no app desliga o Defender, o Firewall ou o UAC.
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GueibsOptimizer"));
        Assert.True(Directory.Exists(src), src);
        string[] forbidden = ["DisableRealtimeMonitoring", "DisableAntiSpyware", "Set-NetFirewallProfile", "EnableLUA\", 0", "DisableBehaviorMonitoring"];
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => forbidden.Where(x => File.ReadAllText(f).Contains(x, StringComparison.OrdinalIgnoreCase)).Select(x => $"{Path.GetFileName(f)}: {x}"))
            .ToList();
        Assert.True(hits.Count == 0, string.Join(", ", hits));
    }
}
