using System.IO;
using System.Text;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void ParsesKnownArguments()
    {
        var o = CommandLineOptions.Parse(["--dry-run", "--WEEKLY", "--xyz"]);
        Assert.True(o.DryRun);
        Assert.True(o.Weekly);
        Assert.False(o.Report);
        Assert.Equal(["--xyz"], o.Unknown);
    }

    [Fact]
    public void ReportAlwaysImpliesDryRun()
    {
        var o = CommandLineOptions.Parse(["--report"]);
        Assert.True(o.Report);
        Assert.True(o.DryRun);
    }

    [Fact]
    public void ScreenshotsTakesFolderAndImpliesDryRun()
    {
        var o = CommandLineOptions.Parse(["--screenshots", @"C:\shots dir"]);
        Assert.Equal(@"C:\shots dir", o.ScreenshotsDir);
        Assert.True(o.DryRun);
        Assert.Empty(o.Unknown);
    }

    [Fact]
    public void ScreenshotsWithoutFolderIsUnknownAndStaysNormal()
    {
        var o = CommandLineOptions.Parse(["--screenshots", "--weekly"]);
        Assert.Null(o.ScreenshotsDir);
        Assert.False(o.DryRun);
        Assert.True(o.Weekly);
        Assert.Contains("--screenshots", o.Unknown);
    }

    [Fact]
    public void ScreenshotFileNamesAreAsciiSlugs() =>
        Assert.Equal("en-claro-03-otimizacao-completa.png",
            GueibsOptimizer.Views.ScreenshotRunner.FileName("en", AppTheme.Light, 3, "Otimização Completa"));

    [Fact]
    public void NoArgumentsMeansNormalMode()
    {
        var o = CommandLineOptions.Parse([]);
        Assert.False(o.DryRun);
        Assert.False(o.Weekly);
    }
}

public class ShellTests
{
    [Fact]
    public void EncodedCommandIsBase64OfUtf16()
    {
        var encoded = Shell.EncodePowerShell("Write-Output 'olá'");
        Assert.Equal("Write-Output 'olá'", Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public void PsQuoteEscapesSingleQuotes() => Assert.Equal("'d''água'", Shell.PsQuote("d'água"));

    [Fact]
    public async Task RunsProcessAndCapturesOutput()
    {
        var shell = new Shell(new Logger(null), dryRun: false);
        var result = await shell.RunAsync("cmd.exe", ["/c", "echo", "teste"]);
        Assert.True(result.Success);
        Assert.Contains("teste", result.StdOut);
    }

    [Fact]
    public async Task PowerShellOutputIsUtf8()
    {
        var shell = new Shell(new Logger(null), dryRun: false);
        var result = await shell.RunPowerShellAsync("Write-Output \"ação `\"aspas`\" $([char]0x00E9)\"");
        Assert.True(result.Success, result.StdErr);
        Assert.Contains("ação \"aspas\" é", result.StdOut);
    }

    [Fact]
    public async Task MissingExecutableReturnsErrorInsteadOfThrowing()
    {
        var shell = new Shell(new Logger(null), dryRun: false);
        var result = await shell.RunAsync("nao-existe-gueibsoptimizer.exe", []);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task MutatingCommandIsNotExecutedInDryRun()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"go-dry-{Guid.NewGuid():N}.txt");
        var shell = new Shell(new Logger(null), dryRun: true);
        var result = await shell.RunMutatingAsync("teste", "cmd.exe", ["/c", "echo x>" + marker]);
        Assert.True(result.Simulated);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task TimeoutKillsProcess()
    {
        var shell = new Shell(new Logger(null), dryRun: false);
        var result = await shell.RunAsync("powershell.exe", Shell.PowerShellArguments("Start-Sleep -Seconds 20"),
            TimeSpan.FromSeconds(1));
        Assert.True(result.TimedOut);
    }
}

public class TweakRegistryTests
{
    [Fact]
    public void DuplicateIdsAreRejected() =>
        Assert.Throws<InvalidOperationException>(() => new TweakRegistry([new FakeTweak("x", 1), new FakeTweak("X", 1)]));

    [Fact]
    public void DefaultTweaksHaveUniqueIdsAndPositivePoints()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        Assert.NotEmpty(registry.All);
        Assert.All(registry.All, t =>
        {
            // Ajustes de preferência (fora do Boost) valem 0; os demais precisam de pontos.
            Assert.True(t.InBoost ? t.Points > 0 : t.Points == 0, t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });
        Assert.Equal(registry.All.Count, registry.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public class DryRunTests
{
    [Fact]
    public async Task ReportListsTweaksAndWritesNothing()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var registry = new TweakRegistry([new TestKeyTweak(ctx, key.Combine("Dry"))]);

        var report = await new DryRunReporter(registry, ctx).BuildReportAsync();

        Assert.Contains("Ajuste de teste", report);
        Assert.Contains(@"Dry\Flag", report);
        Assert.False(key.KeyExists("Dry"));
        Assert.Empty(key.Store.All);
    }

    [Fact]
    public async Task ReportRefusesRealMode()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DryRunReporter(new TweakRegistry([]), ctx).BuildReportAsync());
    }

    [Fact]
    public async Task BoostInDryRunChangesNothing()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var registry = new TweakRegistry([new TestKeyTweak(ctx, key.Combine("Boost"))]);
        var restorePoints = new RestorePointService(ctx.Shell, ctx.Logger);
        var boost = new BoostService(registry, new ScoreService(ctx.Logger), restorePoints, ctx.Logger, dryRun: true);

        var summary = await boost.RunAsync(_ => Task.FromResult(false));

        Assert.True(summary.Simulated);
        Assert.True(summary.RestorePoint!.Simulated);
        Assert.Equal(0, summary.ScoreAfter);
        Assert.False(key.KeyExists("Boost"));
    }
}

public class BackupStoreTests
{
    [Fact]
    public void CorruptFileStartsEmptyAndKeepsCopy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-corrupt-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ não é json");
        try
        {
            var store = new BackupStore(path);
            Assert.Empty(store.All);
            Assert.True(File.Exists(path + ".corrompido"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".corrompido");
        }
    }

    [Fact]
    public void CustomBackupKeepsOriginalValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-custom-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BackupStore(path);
            var ctx = new TweakContext(new Logger(null), store, dryRun: false);
            ctx.SaveCustomBackup("power", "previous", "guid-1", "plano");
            ctx.SaveCustomBackup("power", "previous", "guid-2", "plano");

            Assert.Equal("guid-1", new BackupStore(path).GetCustom("power", "previous"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
