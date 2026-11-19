using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.SelfTest;
using Xunit;

namespace GueibsOptimizer.Tests;

public class SelfTestTests
{
    [Fact]
    public void SelfTestArgumentsAreParsedAndDoNotForceDryRun()
    {
        var o = CommandLineOptions.Parse(["--selftest", "--suite", "Tweaks", "--report", @"C:\x\r.json", "--baseline", @"C:\x\b.json"]);
        Assert.True(o.SelfTest);
        Assert.Equal("tweaks", o.Suite);
        Assert.Equal(@"C:\x\r.json", o.ReportPath);
        Assert.Equal(@"C:\x\b.json", o.BaselinePath);
        Assert.False(o.Report);
        Assert.False(o.DryRun);
    }

    [Fact]
    public void PlainReportStillMeansDryRunReport()
    {
        var o = CommandLineOptions.Parse(["--dry-run", "--report"]);
        Assert.True(o.Report);
        Assert.True(o.DryRun);
        Assert.False(o.SelfTest);
    }

    [Fact]
    public void NetworkRebootAndMassServiceTweaksAreNeverCycled()
    {
        var ctx = new TweakContext(new Logger(null), new BackupStore(Path.Combine(Path.GetTempPath(), $"st-{Guid.NewGuid():N}.json")), dryRun: true);
        var registry = TweakRegistry.CreateDefault(ctx);
        foreach (var id in new[] { "net.prefer-ipv4", "net.disable-ipv6", "gpu.hags", "power.hibernate-off", "pref.detailed-bsod" })
        {
            var t = registry.Find(id);
            Assert.NotNull(t);
            Assert.NotNull(SelfTestRunner.SkipReason(t!));
            Assert.NotNull(SelfTestRunner.SkipReason(t!, forBoost: true));
        }
        Assert.NotNull(SelfTestRunner.SkipReason(registry.Find("power.plan")!));
        Assert.Null(SelfTestRunner.SkipReason(registry.Find("power.plan")!, forBoost: true));
        Assert.Null(SelfTestRunner.SkipReason(registry.Find("gaming.game-mode")!));
    }

    [Fact]
    public void TempCleanupNeverDeletesTheSingleFileExtractionFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cl-{Guid.NewGuid():N}");
        var extracted = Path.Combine(root, ".net", "GueibsOptimizer", "abc");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "native.dll"), "x");
        File.WriteAllText(Path.Combine(root, "lixo.tmp"), "y");
        try
        {
            Assert.True(CleanupService.IsProtected(root, Path.Combine(extracted, "native.dll")));
            Assert.False(CleanupService.IsProtected(root, Path.Combine(root, "lixo.tmp")));
            Assert.False(CleanupService.IsProtected(root, Path.Combine(root, ".netcache", "a.tmp")));
            var r = CleanupService.CleanDirectory(root, dryRun: false, new Logger(null));
            Assert.Equal(1, r.DeletedFiles);
            Assert.True(File.Exists(Path.Combine(extracted, "native.dll")));
            Assert.False(File.Exists(Path.Combine(root, "lixo.tmp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DiffListsChangedAddedAndRemovedAndMarksVolatile()
    {
        var a = new SortedDictionary<string, string> { ["reg:x"] = "DWord:1", ["svcstatus:s"] = "Running", ["reg:gone"] = "DWord:0" };
        var b = new SortedDictionary<string, string> { ["reg:x"] = "DWord:2", ["svcstatus:s"] = "Stopped", ["reg:new"] = "String:a" };
        var d = StateSnapshot.Diff(a, b);
        Assert.Equal(4, d.Count);
        Assert.Contains(d, x => x.Key == "reg:x" && x.Before == "DWord:1" && x.After == "DWord:2" && !x.Volatile);
        Assert.Contains(d, x => x.Key == "svcstatus:s" && x.Volatile);
        Assert.Contains(d, x => x.Key == "reg:gone" && x.After is null);
        Assert.Contains(d, x => x.Key == "reg:new" && x.Before is null);
    }
}
