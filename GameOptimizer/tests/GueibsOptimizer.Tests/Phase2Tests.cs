using System.Diagnostics;
using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.Services.Tweaks;

namespace GueibsOptimizer.Tests;

public class TweakCatalogTests
{
    [Fact]
    public void DefaultTweakPointsSumTo100()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        Assert.Equal(100, registry.All.Sum(registry.PointsOf));
    }

    [Fact]
    public void EveryCategoryHasPortugueseTextAndNoTweakIsHighRiskByAccident()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        // "Windows" é igual nos dois idiomas; as demais categorias precisam de texto em português.
        Assert.All(registry.All.Where(t => t.Category != TweakCategory.Windows), t => Assert.NotEqual(t.Category.ToString(), TweakText.CategoryText(t.Category)));
        Assert.DoesNotContain(registry.All, t => t.Risk == TweakRisk.High);
    }

    [Fact]
    public async Task FullDryRunReportWithRealTweaksWritesNothing()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var report = await new DryRunReporter(TweakRegistry.CreateDefault(ctx), ctx).BuildReportAsync();

        Assert.Contains("Pontuação atual", report);
        Assert.Contains("power.plan", report);
        Assert.Empty(key.Store.All);
        Assert.False(File.Exists(key.BackupFile));
    }

    [Fact]
    public async Task ReportShowsTheSameNormalizedPointsAsTheScreen()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext(dryRun: true);
        var registry = TweakRegistry.CreateDefault(ctx);
        var report = await new DryRunReporter(registry, ctx).BuildReportAsync();

        foreach (var t in registry.All)
            Assert.Contains($"({t.Id}) — risco {TweakText.RiskText(t.Risk)}, {registry.PointsOf(t)} pts", report);
    }
}

public class PowerCfgParserTests
{
    private const string English = """
        Existing Power Schemes (* Active)
        -----------------------------------
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
        Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)
        Power Scheme GUID: A1B2C3D4-0000-1111-2222-333344445555  (Gueibs Optimizer Performance)
        """;

    private const string Portuguese = """
        Esquemas de Energia Existentes (* Ativo)
        -----------------------------------
        GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado)
        GUID do Esquema de Energia: e9a42b02-d5df-448d-aa00-03f14749eb61  (Desempenho Máximo) *
        """;

    [Fact]
    public void ParsesEnglishList()
    {
        var list = PowerCfgParser.ParseList(English);
        Assert.Equal(3, list.Count);
        Assert.True(list[0].Active);
        Assert.Equal("Balanced", list[0].Name);
        Assert.Equal("a1b2c3d4-0000-1111-2222-333344445555", list[2].Guid);
        Assert.False(list[2].Active);
    }

    [Fact]
    public void ParsesPortugueseList()
    {
        var active = PowerCfgParser.ParseList(Portuguese).Single(s => s.Active);
        Assert.Equal(PowerPlanTweak.UltimateGuid, active.Guid);
        Assert.Equal("Desempenho Máximo", active.Name);
    }

    [Fact]
    public void ParsesDuplicateOutput() =>
        Assert.Equal("0f1e2d3c-0000-1111-2222-333344445555",
            PowerCfgParser.FirstGuid("Power Scheme GUID: 0f1e2d3c-0000-1111-2222-333344445555  (Ultimate Performance)"));

    [Fact]
    public void EmptyOutputHasNoSchemes() => Assert.Empty(PowerCfgParser.ParseList("Erro"));
}

public class StartupTests
{
    [Fact]
    public void StartupApprovedBytesRoundTrip()
    {
        Assert.False(StartupApproved.IsDisabled(null));
        Assert.False(StartupApproved.IsDisabled(StartupApproved.Enabled()));
        Assert.False(StartupApproved.IsDisabled([0x06, 0, 0, 0]));
        var disabled = StartupApproved.Disabled(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, disabled.Length);
        Assert.True(StartupApproved.IsDisabled(disabled));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), BitConverter.ToInt64(disabled, 4));
    }

    [Fact]
    public void StartupIdsRoundTrip()
    {
        var id = StartupIds.Make(StartupSource.ScheduledTask, @"\Pasta\Minha|Tarefa");
        Assert.True(StartupIds.TryParse(id, out var source, out var key));
        Assert.Equal(StartupSource.ScheduledTask, source);
        Assert.Equal(@"\Pasta\Minha|Tarefa", key);
        Assert.False(StartupIds.TryParse("power.plan", out _, out _));
        Assert.False(StartupIds.TryParse("startup:Inexistente|x", out _, out _));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --min", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe /silent", "C:\\Tools\\tool.exe")]
    [InlineData("C:\\Program Files\\Sem Aspas\\x.EXE -a", "C:\\Program Files\\Sem Aspas\\x.EXE")]
    [InlineData("rundll32 shell32.dll,Control_RunDLL", "rundll32")]
    public void ExtractsExecutable(string command, string expected) =>
        Assert.Equal(expected, CommandLineTools.ExtractExecutable(command));

    [Fact]
    public void ExpandsEnvironmentVariables()
    {
        var exe = CommandLineTools.ExtractExecutable("%SystemRoot%\\System32\\notepad.exe arquivo.txt");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), exe, ignoreCase: true);
    }

    [Fact]
    public void SignedWindowsBinaryHasPublisherOrNullWithoutThrowing()
    {
        // notepad.exe é assinado por catálogo em algumas versões: só exigimos que não lance exceção.
        _ = StartupService.PublisherOf(@"C:\Windows\System32\notepad.exe");
        Assert.Null(StartupService.PublisherOf(@"C:\nao\existe.exe"));
    }

    [Fact]
    public async Task RestorationDelegatesUnknownIdsToProviders()
    {
        using var key = new TestRegistryKey();
        var ctx = key.CreateContext();
        ctx.SaveCustomBackup("startup:RunUser|Teste", "x", "y", "desc");
        var provider = new FakeUndoProvider();
        var service = new RestorationService(new TweakRegistry([]), key.Store, ctx.Registry, ctx.Logger, [provider]);

        Assert.Equal("Inicialização falsa", Assert.Single(service.GetHistory()).TweakName);
        Assert.Null(await service.UndoTweakAsync("startup:RunUser|Teste"));
        Assert.Equal("startup:RunUser|Teste", provider.Undone);
    }

    private sealed class FakeUndoProvider : IUndoProvider
    {
        public string? Undone { get; private set; }
        public bool CanUndo(string id) => id.StartsWith("startup:");
        public string DisplayName(string id) => "Inicialização falsa";
        public Task UndoAsync(string id) { Undone = id; return Task.CompletedTask; }
    }
}

public class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"go-clean-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"go-outside-{Guid.NewGuid():N}");

    public CleanupTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deep"));
        File.WriteAllText(Path.Combine(_root, "a.tmp"), new string('x', 1000));
        File.WriteAllText(Path.Combine(_root, "sub", "deep", "b.tmp"), new string('y', 500));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "importante.txt"), "não apagar");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _outside })
            try { Directory.Delete(d, true); } catch { }
    }

    [Fact]
    public void EstimateCountsBytes()
    {
        var category = new CleanupCategory(CleanupKind.UserTemp, "x", "x", [_root], true);
        var estimate = CleanupService.Estimate(category);
        Assert.Equal(1500, estimate.Bytes);
        Assert.Equal(2, estimate.Files);
    }

    [Fact]
    public void DryRunDeletesNothing()
    {
        var r = CleanupService.CleanDirectory(_root, dryRun: true, new Logger(null));
        Assert.Equal(1500, r.FreedBytes);
        Assert.True(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [Fact]
    public void LockedFilesAreSkippedAndRestIsDeleted()
    {
        var locked = Path.Combine(_root, "locked.tmp");
        File.WriteAllText(locked, "em uso");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = CleanupService.CleanDirectory(_root, dryRun: false, new Logger(null));
            Assert.Equal(1, r.SkippedFiles);
            Assert.Equal(2, r.DeletedFiles);
        }
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(_root, "a.tmp")));
        Assert.False(Directory.Exists(Path.Combine(_root, "sub")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void JunctionsAreNotFollowed()
    {
        var junction = Path.Combine(_root, "link");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, _outside }, CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(junction));

        CleanupService.CleanDirectory(_root, dryRun: false, new Logger(null));

        Assert.True(File.Exists(Path.Combine(_outside, "importante.txt")));
    }

    [Fact]
    public void CategoriesNeverIncludePrefetch() =>
        Assert.DoesNotContain(CleanupService.Categories().SelectMany(c => c.Paths),
            p => p.Contains("Prefetch", StringComparison.OrdinalIgnoreCase));
}

public class BoosterAndMemoryTests
{
    [Fact]
    public void NormalizesGameList()
    {
        var set = GameBoosterService.NormalizeList([" CS2.exe", "valorant", "", "cs2.EXE"]);
        Assert.Equal(2, set.Count);
        Assert.Contains("cs2.exe", set);
        Assert.Contains("valorant.exe", set);
    }

    [Theory]
    [InlineData(ProcessPriorityClass.Normal, true)]
    [InlineData(ProcessPriorityClass.BelowNormal, true)]
    [InlineData(ProcessPriorityClass.High, false)]
    [InlineData(ProcessPriorityClass.RealTime, false)]
    public void OnlyRaisesLowerPriorities(ProcessPriorityClass current, bool expected) =>
        Assert.Equal(expected, GameBoosterService.ShouldBoost(current));

    [Fact]
    public void InvalidGameListFallsBackToDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-games-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ quebrado");
        try
        {
            var list = new GameBoosterService(new Logger(null), true, path).LoadList();
            Assert.Contains("cs2.exe", list);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsRealMemoryAndDryRunChangesNothing()
    {
        var (available, total) = MemoryCleanerService.ReadMemory();
        Assert.True(total > 0 && available > 0 && available <= total);

        var r = new MemoryCleanerService(new Logger(null), dryRun: true).Clean();
        Assert.True(r.Simulated);
        Assert.Equal(0UL, r.Freed);
    }

    [Fact]
    public void WeeklyTaskQuotesExecutablePath()
    {
        var args = WeeklyReviewService.CreateArguments(@"C:\Program Files\Game Optimizer\GueibsOptimizer.exe");
        int tr = args.ToList().IndexOf("/TR");
        Assert.Equal("\"C:\\Program Files\\Game Optimizer\\GueibsOptimizer.exe\" --weekly", args[tr + 1]);
        Assert.Contains("HIGHEST", args);
    }
}
