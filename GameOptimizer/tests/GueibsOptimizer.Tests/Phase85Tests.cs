using GueibsOptimizer.Services;
using Xunit;

namespace GueibsOptimizer.Tests;

public class Phase85Tests
{
    [Fact]
    public void CommandsAreExactAndChkdskIsReadOnly()
    {
        Assert.Equal("chkdsk C:", IntegrityCommands.For(IntegrityTool.ChkdskScan, "C:").Display);
        Assert.DoesNotContain(IntegrityCommands.For(IntegrityTool.ChkdskScan).Arguments, a => a.StartsWith("/f", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/r", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("sfc /scannow", IntegrityCommands.For(IntegrityTool.Sfc).Display);
        Assert.Equal("DISM /Online /Cleanup-Image /RestoreHealth", IntegrityCommands.For(IntegrityTool.Dism).Display);
        Assert.True(IntegrityCommands.For(IntegrityTool.Sfc).UnicodeOutput);
        Assert.Equal([IntegrityTool.ChkdskScan, IntegrityTool.Dism, IntegrityTool.Sfc], IntegrityCommands.WeeklyReview);
        Assert.Equal(["dirty", "set", "C:"], IntegrityCommands.ScheduleRepairArguments("C:"));
    }

    [Theory]
    [InlineData(IntegrityTool.Sfc, 0, "A Proteção de Recursos do Windows não encontrou nenhuma violação de integridade.", IntegrityOutcome.Ok, false)]
    [InlineData(IntegrityTool.Sfc, 0, "Windows Resource Protection found corrupt files and successfully repaired them.", IntegrityOutcome.Repaired, true)]
    [InlineData(IntegrityTool.Sfc, 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.", IntegrityOutcome.ErrorsFound, false)]
    [InlineData(IntegrityTool.Sfc, 1, "Windows Resource Protection could not perform the requested operation.", IntegrityOutcome.Failed, false)]
    [InlineData(IntegrityTool.Dism, 0, "The restore operation completed successfully.", IntegrityOutcome.Ok, false)]
    [InlineData(IntegrityTool.Dism, 0, "The component store corruption was repaired.", IntegrityOutcome.Repaired, false)]
    [InlineData(IntegrityTool.Dism, 3010, "", IntegrityOutcome.Repaired, true)]
    [InlineData(IntegrityTool.Dism, unchecked((int)0x800F081F), "Error: 0x800f081f", IntegrityOutcome.Failed, false)]
    [InlineData(IntegrityTool.ChkdskScan, 0, "O Windows verificou o sistema de arquivos e não encontrou problemas.", IntegrityOutcome.Ok, false)]
    [InlineData(IntegrityTool.ChkdskScan, 3, "Windows has scanned the file system and found problems.", IntegrityOutcome.ErrorsFound, false)]
    [InlineData(IntegrityTool.ChkdskScan, 3, "Acesso negado porque você não tem privilégios suficientes.", IntegrityOutcome.Failed, false)]
    public void InterpretsExitCodesAndOutput(IntegrityTool tool, int code, string output, IntegrityOutcome expected, bool reboot)
    {
        var r = IntegrityCommands.Interpret(tool, code, output);
        Assert.Equal(expected, r.Outcome);
        Assert.Equal(reboot, r.NeedsReboot);
        Assert.Equal(tool == IntegrityTool.ChkdskScan && expected == IntegrityOutcome.ErrorsFound, r.OfferChkdskRepair);
    }

    [Theory]
    [InlineData("en", "Etapa 2 de 3: DISM (reparar a imagem do Windows)", "Step 2 of 3: DISM (repair the Windows image)")]
    [InlineData("es", "Última revisão: há 9 dias.", "Última revisión: hace 9 días.")]
    [InlineData("en", "O DISM falhou (código 0x800F081F). Os arquivos de origem não foram encontrados: verifique a internet e o Windows Update.",
        "DISM failed (code 0x800F081F). The source files were not found: check the internet connection and Windows Update.")]
    [InlineData("es", "Concluído: reparos feitos. Reinicie o PC.", "Terminado: reparaciones hechas. Reinicia el PC.")]
    public void MaintenanceMessagesAreTranslated(string lang, string pt, string expected)
    {
        var l = GueibsOptimizer.Core.LocalizationService.Instance;
        try
        {
            l.SetLanguage(lang);
            Assert.Equal(expected, GueibsOptimizer.Core.LocalizationService.Tr(pt));
        }
        finally
        {
            l.SetLanguage(GueibsOptimizer.Core.LocalizationService.Portuguese);
        }
    }

    [Theory]
    [InlineData(GueibsOptimizer.Core.LocalizationService.English)]
    [InlineData(GueibsOptimizer.Core.LocalizationService.Spanish)]
    public void ToolTitlesOutcomesAndSummariesHaveTranslations(string language)
    {
        var table = GueibsOptimizer.Core.LocalizationService.LoadEmbedded(language);
        var keys = Enum.GetValues<IntegrityTool>().Select(t => IntegrityCommands.For(t).Title)
            .Concat(Enum.GetValues<IntegrityOutcome>().Where(o => o != IntegrityOutcome.Ok).Select(IntegrityCommands.OutcomeText))
            .Concat(new[]
            {
                (IntegrityTool.Sfc, 0, "did not find any integrity violations"), (IntegrityTool.Sfc, 0, "successfully repaired"),
                (IntegrityTool.Sfc, 0, "unable to fix"), (IntegrityTool.Sfc, 0, "pending system repair"), (IntegrityTool.Sfc, 0, ""),
                (IntegrityTool.Dism, 0, ""), (IntegrityTool.Dism, 0, "corruption was repaired"), (IntegrityTool.Dism, 3010, ""),
                (IntegrityTool.ChkdskScan, 0, ""), (IntegrityTool.ChkdskScan, 3, "found problems"), (IntegrityTool.ChkdskScan, 2, ""),
                (IntegrityTool.ChkdskScan, 3, "access denied")
            }.Select(x => IntegrityCommands.Interpret(x.Item1, x.Item2, x.Item3).Summary));
        var missing = keys.Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"{language}: " + string.Join(" | ", missing));
    }

    [Fact]
    public void SfcUtf16OutputWithNulsIsNormalized()
    {
        var spaced = string.Join("\0", "did not find any integrity violations".ToCharArray());
        Assert.Equal(IntegrityOutcome.Ok, IntegrityCommands.Interpret(IntegrityTool.Sfc, 0, spaced).Outcome);
    }

    [Fact]
    public void FinalSummaryReflectsWorstStep()
    {
        IntegrityResult R(IntegrityOutcome o, bool reboot = false) => new(IntegrityTool.Sfc, o, 0, "", reboot, "");
        Assert.Equal("Concluído: tudo OK.", IntegrityCommands.FinalSummary([R(IntegrityOutcome.Ok), R(IntegrityOutcome.Ok)]));
        Assert.Equal("Concluído: reparos feitos. Reinicie o PC.", IntegrityCommands.FinalSummary([R(IntegrityOutcome.Ok), R(IntegrityOutcome.Repaired, true)]));
        Assert.StartsWith("Concluído com problemas", IntegrityCommands.FinalSummary([R(IntegrityOutcome.Repaired), R(IntegrityOutcome.Failed)]));
        Assert.Equal("Cancelado antes de terminar.", IntegrityCommands.FinalSummary([R(IntegrityOutcome.Ok), R(IntegrityOutcome.Cancelled)]));
        Assert.Equal("Simulação: nenhuma ferramenta foi executada.", IntegrityCommands.FinalSummary([R(IntegrityOutcome.Simulated)]));
    }

    [Fact]
    public void ReviewIsDueAfterSevenDaysOrNever()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0);
        Assert.True(IntegrityCommands.IsReviewDue(null, now));
        Assert.False(IntegrityCommands.IsReviewDue(now.AddDays(-7), now));
        Assert.True(IntegrityCommands.IsReviewDue(now.AddDays(-8), now));
        Assert.Equal(3, IntegrityCommands.DaysSince(now.AddDays(-3).AddHours(5), now));
    }

    [Fact]
    public async Task DryRunNeverStartsTheTool()
    {
        var runner = new IntegrityToolRunner(new GueibsOptimizer.Core.Logger(null), dryRun: true);
        var lines = new List<string>();
        var r = await runner.RunAsync(IntegrityCommands.For(IntegrityTool.Sfc), new SyncProgress(lines), CancellationToken.None);
        Assert.Equal(IntegrityOutcome.Simulated, r.Outcome);
        Assert.Contains(lines, l => l.Contains("sfc /scannow"));
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    [Fact]
    public async Task ReportListsCommandsWithoutRunning()
    {
        var w = new System.IO.StringWriter();
        await MaintenanceReport.WriteAsync(w, null);
        var text = w.ToString();
        Assert.Contains("nunca feita", text);
        Assert.Contains("sfc /scannow", text);
        Assert.Contains("fsutil dirty set", text);
    }
    [Fact]
    public void BlockedToolHasClearMessage()
    {
        var cmd = IntegrityCommands.For(IntegrityTool.ChkdskScan, "C:");
        Assert.Contains("bloqueou a execução do chkdsk.exe", IntegrityToolRunner.BlockedMessage(cmd, 5));
        Assert.Contains("não existe", IntegrityToolRunner.BlockedMessage(cmd, 2));
        var l = GueibsOptimizer.Core.LocalizationService.Instance;
        try
        {
            l.SetLanguage("en");
            Assert.StartsWith("Windows blocked running chkdsk.exe", GueibsOptimizer.Core.LocalizationService.Tr(IntegrityToolRunner.BlockedMessage(cmd, 5)));
        }
        finally { l.SetLanguage(GueibsOptimizer.Core.LocalizationService.Portuguese); }
    }
}