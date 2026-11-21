using System.Diagnostics;
using System.Globalization;
using System.Text;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public enum IntegrityTool
{
    /// <summary>
    /// CHKDSK /scan: verifica e repara o NTFS com o Windows em uso (reparo online, sem reiniciar). O que só pode ser
    /// reparado com o volume livre fica para a próxima inicialização (fsutil dirty set).
    /// </summary>
    ChkdskScan,
    Dism,
    Sfc
}

public enum IntegrityOutcome
{
    Ok,
    Repaired,
    ErrorsFound,
    Failed,
    Cancelled,
    Simulated
}

/// <summary>Comando exato de uma ferramenta. <see cref="Display"/> é o que a interface mostra e deixa copiar.</summary>
public sealed record IntegrityCommand(IntegrityTool Tool, string Title, string FileName, IReadOnlyList<string> Arguments, bool UnicodeOutput)
{
    public string Display => FileName.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase) + " " + string.Join(" ", Arguments);
}

public sealed record IntegrityResult(IntegrityTool Tool, IntegrityOutcome Outcome, int ExitCode, string Summary, bool NeedsReboot, string Output)
{
    public bool OfferChkdskRepair => Tool == IntegrityTool.ChkdskScan && Outcome == IntegrityOutcome.ErrorsFound;
}

/// <summary>Comandos e interpretação (lógica pura, testada) de CHKDSK, SFC e DISM.</summary>
public static class IntegrityCommands
{
    public static string SystemDrive => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

    public static IntegrityCommand For(IntegrityTool tool, string? drive = null) => tool switch
    {
        IntegrityTool.ChkdskScan => new(tool, "Verificar e reparar disco (CHKDSK)", "chkdsk.exe", [drive ?? SystemDrive, "/scan"], false),
        IntegrityTool.Dism => new(tool, "DISM (reparar a imagem do Windows)", "DISM.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth"], false),
        _ => new(tool, "SFC (verificar arquivos de sistema)", "sfc.exe", ["/scannow"], true)
    };

    /// <summary>Revisão semanal: CHKDSK /scan (com reparo) → DISM → SFC. DISM antes do SFC para o SFC usar a imagem já reparada.</summary>
    public static readonly IntegrityTool[] WeeklyReview = [IntegrityTool.ChkdskScan, IntegrityTool.Dism, IntegrityTool.Sfc];

    /// <summary>Reparo completo: DISM → SFC.</summary>
    public static readonly IntegrityTool[] FullRepair = [IntegrityTool.Dism, IntegrityTool.Sfc];

    /// <summary>Comando que agenda o reparo do disco no próximo boot (marca o volume para o autochk).</summary>
    public static IReadOnlyList<string> ScheduleRepairArguments(string drive) => ["dirty", "set", drive];

    /// <summary>Remove os NUL e "\r" de progresso (a saída do SFC chega em UTF-16 com linhas sobrescritas).</summary>
    public static string Normalize(string text) => text.Replace("\0", string.Empty).ToLowerInvariant();

    public static IntegrityResult Interpret(IntegrityTool tool, int exitCode, string output)
    {
        var t = Normalize(output);
        return tool switch
        {
            IntegrityTool.ChkdskScan => InterpretChkdsk(exitCode, t, output),
            IntegrityTool.Dism => InterpretDism(exitCode, t, output),
            _ => InterpretSfc(exitCode, t, output)
        };
    }

    private static bool Has(string text, params string[] any) => any.Any(text.Contains);

    private static IntegrityResult InterpretChkdsk(int code, string t, string raw)
    {
        // Códigos do chkdsk: 0 = sem erros; 1 = erros corrigidos; 2 = só limpeza; 3 = não pôde verificar ou há erros que só
        // podem ser reparados com o volume livre (o /scan pede /spotfix).
        if (Has(t, "access denied", "acesso negado", "acceso denegado"))
            return new(IntegrityTool.ChkdskScan, IntegrityOutcome.Failed, code, "O CHKDSK precisa de permissão de administrador.", false, raw);
        bool noProblems = Has(t, "found no problems", "não encontrou problemas", "no encontró problemas", "no encontró ningún problema");
        bool needsOffline = !noProblems && Has(t, "found problems", "encontrou problemas", "encontró problemas", "spotfix");
        bool corrected = Has(t, "made corrections", "fez correções", "realizó correcciones", "ha realizado correcciones");
        if (code == 3 || needsOffline)
            return new(IntegrityTool.ChkdskScan, IntegrityOutcome.ErrorsFound, code,
                "O CHKDSK reparou o que dava com o Windows em uso; o restante precisa ser reparado na próxima inicialização.", false, raw);
        if (code == 1 || corrected)
            return new(IntegrityTool.ChkdskScan, IntegrityOutcome.Repaired, code, "Disco verificado: o CHKDSK corrigiu problemas no sistema de arquivos.", false, raw);
        if (code == 0)
            return new(IntegrityTool.ChkdskScan, IntegrityOutcome.Ok, code, "Disco verificado: nenhum problema encontrado.", false, raw);
        if (code == 2)
            return new(IntegrityTool.ChkdskScan, IntegrityOutcome.Ok, code, "Disco verificado: só itens de manutenção menores, sem erros.", false, raw);
        return new(IntegrityTool.ChkdskScan, IntegrityOutcome.Failed, code, $"O CHKDSK não pôde concluir (código {code}).", false, raw);
    }

    private static IntegrityResult InterpretSfc(int code, string t, string raw)
    {
        if (Has(t, "did not find any integrity violations", "não encontrou nenhuma violação de integridade", "no encontró ninguna infracción de integridad"))
            return new(IntegrityTool.Sfc, IntegrityOutcome.Ok, code, "SFC: nenhuma violação de integridade encontrada.", false, raw);
        // sfc /verifyonly: só verifica; "encontrou violações" não é OK.
        if (Has(t, "found integrity violations", "encontrou violações de integridade", "encontró infracciones de integridad"))
            return new(IntegrityTool.Sfc, IntegrityOutcome.ErrorsFound, code,
                "SFC: encontrou violações de integridade (só verificação). Rode o sfc /scannow para reparar.", false, raw);
        if (Has(t, "successfully repaired", "reparou-os com êxito", "os reparou com êxito", "repará correctamente", "los reparó correctamente"))
            return new(IntegrityTool.Sfc, IntegrityOutcome.Repaired, code, "SFC: arquivos corrompidos foram reparados. Reinicie o PC.", true, raw);
        if (Has(t, "unable to fix", "não conseguiu corrigir", "no pudo corregir", "não pôde corrigir"))
            return new(IntegrityTool.Sfc, IntegrityOutcome.ErrorsFound, code,
                "SFC: encontrou arquivos corrompidos que não conseguiu reparar. Rode o DISM e depois o SFC de novo.", false, raw);
        if (Has(t, "pending system repair", "reparo do sistema pendente", "reparación del sistema pendiente"))
            return new(IntegrityTool.Sfc, IntegrityOutcome.Failed, code, "SFC: há um reparo pendente. Reinicie o PC e rode de novo.", true, raw);
        return new(IntegrityTool.Sfc, code == 0 ? IntegrityOutcome.Ok : IntegrityOutcome.Failed, code,
            code == 0 ? "SFC concluído." : $"O SFC não pôde executar a operação (código {code}).", false, raw);
    }

    private static IntegrityResult InterpretDism(int code, string t, string raw)
    {
        const int RebootRequired = 3010;
        if (code == RebootRequired)
            return new(IntegrityTool.Dism, IntegrityOutcome.Repaired, code, "DISM: reparo concluído; reinicie o PC para terminar.", true, raw);
        // DISM /CheckHealth ou /ScanHealth: só diagnóstico.
        if (Has(t, "not repairable", "não é reparável", "não pode ser reparado", "no se puede reparar"))
            return new(IntegrityTool.Dism, IntegrityOutcome.Failed, code, "DISM: a imagem do Windows tem problemas que não podem ser reparados por aqui.", false, raw);
        if (Has(t, "is repairable", "é reparável", "pode ser reparado", "se puede reparar"))
            return new(IntegrityTool.Dism, IntegrityOutcome.ErrorsFound, code, "DISM: a imagem do Windows tem problemas que podem ser reparados. Rode o DISM /RestoreHealth.", false, raw);
        if (code == 0)
        {
            bool repaired = Has(t, "corruption was repaired", "corrupção do repositório de componentes foi reparada", "corrupción del almacén de componentes se reparó", "foi reparada");
            return new(IntegrityTool.Dism, repaired ? IntegrityOutcome.Repaired : IntegrityOutcome.Ok, code,
                repaired ? "DISM: a imagem do Windows foi reparada." : "DISM: a imagem do Windows está íntegra.", false, raw);
        }
        string hint = unchecked((uint)code) switch
        {
            0x800F081F or 0x800F0906 or 0x800F0907 => "Os arquivos de origem não foram encontrados: verifique a internet e o Windows Update.",
            0x800F0954 => "O Windows Update está bloqueado por política (WSUS).",
            _ => string.Empty
        };
        return new(IntegrityTool.Dism, IntegrityOutcome.Failed, code,
            hint.Length == 0 ? $"O DISM falhou (código 0x{unchecked((uint)code):X8})." : $"O DISM falhou (código 0x{unchecked((uint)code):X8}). {hint}", false, raw);
    }

    public static string OutcomeText(IntegrityOutcome o) => o switch
    {
        IntegrityOutcome.Ok => "OK",
        IntegrityOutcome.Repaired => "Reparos feitos",
        IntegrityOutcome.ErrorsFound => "Problemas encontrados",
        IntegrityOutcome.Failed => "Falhou",
        IntegrityOutcome.Cancelled => "Cancelado",
        _ => "Simulado"
    };

    /// <summary>Resumo final: OK / reparos feitos / reiniciar.</summary>
    public static string FinalSummary(IReadOnlyList<IntegrityResult> results)
    {
        if (results.Count == 0) return "Nada foi executado.";
        if (results.All(r => r.Outcome == IntegrityOutcome.Simulated)) return "Simulação: nenhuma ferramenta foi executada.";
        if (results.Any(r => r.Outcome == IntegrityOutcome.Cancelled)) return "Cancelado antes de terminar.";
        bool reboot = results.Any(r => r.NeedsReboot);
        if (results.Any(r => r.Outcome is IntegrityOutcome.Failed or IntegrityOutcome.ErrorsFound))
            return "Concluído com problemas: veja cada etapa." + (reboot ? " Reinicie o PC." : string.Empty);
        if (results.Any(r => r.Outcome == IntegrityOutcome.Repaired))
            return "Concluído: reparos feitos." + (reboot ? " Reinicie o PC." : string.Empty);
        return "Concluído: tudo OK.";
    }

    /// <summary>Dias desde a última revisão; null quando nunca foi feita.</summary>
    public static int? DaysSince(DateTime? last, DateTime now) => last is { } d ? Math.Max(0, (int)(now.Date - d.Date).TotalDays) : null;

    public static bool IsReviewDue(DateTime? last, DateTime now) => DaysSince(last, now) is not { } days || days > 7;
}

/// <summary>Seção do --report: comandos da manutenção que seriam executados (nenhum é executado).</summary>
public static class MaintenanceReport
{
    public static Task WriteAsync(System.IO.StringWriter w, DateTime? lastReview)
    {
        w.WriteLine();
        w.WriteLine("== Manutenção (revisão de integridade) ==");
        var days = IntegrityCommands.DaysSince(lastReview, DateTime.Now);
        w.WriteLine(days is { } d ? $"Última revisão: há {d} dia(s)." : "Última revisão: nunca feita.");
        w.WriteLine(IntegrityCommands.IsReviewDue(lastReview, DateTime.Now) ? "O Painel mostra o aviso de revisão (mais de 7 dias)." : "Revisão em dia.");
        w.WriteLine("Revisão semanal executaria (CHKDSK /scan verifica e repara com o Windows em uso):");
        foreach (var tool in IntegrityCommands.WeeklyReview) w.WriteLine($"  - {IntegrityCommands.For(tool).Display}");
        w.WriteLine("Reparo completo executaria:");
        foreach (var tool in IntegrityCommands.FullRepair) w.WriteLine($"  - {IntegrityCommands.For(tool).Display}");
        w.WriteLine($"Se sobrar reparo que exige o disco livre, a revisão agenda para a próxima inicialização: fsutil {string.Join(" ", IntegrityCommands.ScheduleRepairArguments(IntegrityCommands.SystemDrive))}");
        return Task.CompletedTask;
    }
}

/// <summary>Executa uma ferramenta capturando a saída linha a linha, com cancelamento. Em --dry-run nada é executado.</summary>
public sealed class IntegrityToolRunner(Logger logger, bool dryRun)
{
    public bool DryRun => dryRun;

    public async Task<IntegrityResult> RunAsync(IntegrityCommand command, IProgress<string>? lines, CancellationToken ct)
    {
        if (dryRun)
        {
            logger.Simulated($"Executar: {command.Display}");
            lines?.Report($"[simulação] {command.Display}");
            return new IntegrityResult(command.Tool, IntegrityOutcome.Simulated, 0, $"Simulação: \"{command.Display}\" não foi executado.", false, string.Empty);
        }

        var output = new StringBuilder();
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = command.UnicodeOutput ? Encoding.Unicode : Oem(),
            StandardErrorEncoding = command.UnicodeOutput ? Encoding.Unicode : Oem()
        };
        foreach (var a in command.Arguments) psi.ArgumentList.Add(a);

        logger.Info($"Executando: {command.Display}");
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void OnData(object? _, DataReceivedEventArgs e)
            {
                if (e.Data is null) return;
                var line = e.Data.Replace("\0", string.Empty).TrimEnd();
                // O SFC/DISM reescrevem a linha de progresso com "\r"; mostra só o último trecho.
                int cr = line.LastIndexOf('\r');
                if (cr >= 0) line = line[(cr + 1)..];
                lock (output) output.AppendLine(line);
                if (line.Length > 0) lines?.Report(line);
            }
            process.OutputDataReceived += OnData;
            process.ErrorDataReceived += OnData;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                logger.Warn($"Cancelado pelo usuário: {command.Display}");
                return new IntegrityResult(command.Tool, IntegrityOutcome.Cancelled, -1, "Cancelado pelo usuário.", false, output.ToString());
            }
            process.WaitForExit(); // garante que os últimos eventos de saída chegaram
            var result = IntegrityCommands.Interpret(command.Tool, process.ExitCode, output.ToString());
            logger.Info($"{command.Display}: código {process.ExitCode} — {result.Summary}");
            return result;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 5)
        {
            // 5 = acesso negado ao próprio programa; 2 = programa ausente. Comum em versões modificadas do Windows.
            logger.Warn($"{command.Display}: o Windows não permitiu iniciar {command.FileName} (código {ex.NativeErrorCode}).");
            return new IntegrityResult(command.Tool, IntegrityOutcome.Failed, -1, BlockedMessage(command, ex.NativeErrorCode), false, output.ToString());
        }
        catch (Exception ex)
        {
            logger.Error($"Falha ao executar {command.Display}", ex);
            return new IntegrityResult(command.Tool, IntegrityOutcome.Failed, -1, $"Não foi possível executar: {ex.Message}", false, output.ToString());
        }
    }

    /// <summary>Mensagem clara quando o Windows bloqueia ou não tem a ferramenta (o app não contorna isso).</summary>
    public static string BlockedMessage(IntegrityCommand command, int nativeError) => nativeError == 2
        ? $"O {command.FileName} não existe nesta instalação do Windows (algumas versões modificadas removem essa ferramenta)."
        : $"O Windows bloqueou a execução do {command.FileName} (acesso negado ao programa, mesmo como administrador). Algumas versões modificadas do Windows bloqueiam essa ferramenta; o app não contorna esse bloqueio.";

    private static Encoding Oem()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.UTF8; }
    }
}
