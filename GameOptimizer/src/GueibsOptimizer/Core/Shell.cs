using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GueibsOptimizer.Core;

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Simulated = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    public static ShellResult SimulatedOk() => new(0, string.Empty, string.Empty, false, true);
}

/// <summary>
/// Executa processos com timeout e captura de saída. Argumentos sempre via ArgumentList
/// (sem concatenação) e PowerShell sempre via -EncodedCommand.
/// </summary>
public sealed class Shell
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly Logger _logger;

    static Shell()
    {
        // Necessário para decodificar a saída de ferramentas de console na página OEM (ex.: 850).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Shell(Logger logger, bool dryRun)
    {
        _logger = logger;
        DryRun = dryRun;
    }

    public bool DryRun { get; }

    /// <summary>Executa sempre (use apenas para comandos de leitura).</summary>
    public async Task<ShellResult> RunAsync(string fileName, IEnumerable<string> arguments,
        TimeSpan? timeout = null, Encoding? outputEncoding = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? OemEncoding(),
            StandardErrorEncoding = outputEncoding ?? OemEncoding()
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return new ShellResult(-1, string.Empty, $"Não foi possível iniciar {fileName}.", false);

            var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdOut = await stdOutTask.ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);
            return new ShellResult(process.ExitCode, stdOut, stdErr, false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested) throw;
            _logger.Warn($"Tempo esgotado executando {fileName}.");
            return new ShellResult(-1, string.Empty, "Tempo esgotado.", true);
        }
        catch (Win32Exception ex)
        {
            return new ShellResult(-1, string.Empty, $"Não foi possível executar {fileName}: {ex.Message}", false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new ShellResult(-1, string.Empty, ex.Message, false);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Comando que altera o sistema: no modo simulação só é registrado no log.</summary>
    public Task<ShellResult> RunMutatingAsync(string description, string fileName, IEnumerable<string> arguments,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var args = arguments.ToList();
        if (DryRun)
        {
            _logger.Simulated($"{description}: {fileName} {string.Join(' ', args)}");
            return Task.FromResult(ShellResult.SimulatedOk());
        }
        return RunAsync(fileName, args, timeout, null, ct);
    }

    public Task<ShellResult> RunPowerShellAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default) =>
        RunAsync("powershell.exe", PowerShellArguments(script), timeout, new UTF8Encoding(false), ct);

    public Task<ShellResult> RunPowerShellMutatingAsync(string description, string script,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (DryRun)
        {
            _logger.Simulated($"{description} (PowerShell): {script.ReplaceLineEndings(" ")}");
            return Task.FromResult(ShellResult.SimulatedOk());
        }
        return RunPowerShellAsync(script, timeout, ct);
    }

    public static IReadOnlyList<string> PowerShellArguments(string script) =>
    [
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-EncodedCommand", EncodePowerShell("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" + script)
    ];

    /// <summary>Base64 de UTF-16LE, formato exigido por -EncodedCommand.</summary>
    public static string EncodePowerShell(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Literal de string PowerShell com aspas simples (sem expansão de variáveis).</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.UTF8; }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // processo já encerrou
        }
    }
}
