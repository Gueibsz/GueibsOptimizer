using System.Diagnostics;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public sealed record RestorePointResult(bool Success, string Message, bool Simulated = false);

public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default);
}

/// <summary>
/// Cria ponto de restauração com Checkpoint-Computer. Se a Proteção do Sistema estiver desligada,
/// tenta habilitar com Enable-ComputerRestore antes.
/// </summary>
public sealed class RestorePointService(Shell shell, Logger logger) : IRestorePointService
{
    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try { Enable-ComputerRestore -Drive "$env:SystemDrive\" } catch { Write-Output ("ENABLE_FAIL: " + $_.Exception.Message) }
            try {
                $w = $null
                Checkpoint-Computer -Description {{Shell.PsQuote(description)}} -RestorePointType 'MODIFY_SETTINGS' -WarningVariable w -WarningAction SilentlyContinue
                if ($w) { Write-Output ("WARN: " + ($w -join ' ')) } else { Write-Output 'OK' }
            } catch {
                Write-Output ("FAIL: " + $_.Exception.Message)
                exit 1
            }
            """;

        var result = await shell.RunPowerShellMutatingAsync("Criar ponto de restauração", script,
            TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        if (result.Simulated)
            return new RestorePointResult(true, "Ponto de restauração simulado (modo simulação).", Simulated: true);

        var output = result.StdOut.Trim();
        if (result.Success && output.Contains("OK", StringComparison.Ordinal) && !output.Contains("WARN:", StringComparison.Ordinal))
        {
            logger.Info($"Ponto de restauração criado: {description}");
            return new RestorePointResult(true, "Ponto de restauração criado.");
        }

        string reason;
        if (output.Contains("WARN:", StringComparison.Ordinal))
            reason = "O Windows recusou criar um novo ponto (ele permite apenas um a cada 24 horas por padrão).";
        else if (result.TimedOut)
            reason = "A criação do ponto de restauração demorou demais.";
        else
            reason = "Não foi possível criar o ponto de restauração. Verifique se a Proteção do Sistema está ativa.";

        logger.Warn($"{reason} Detalhes: {output} {result.StdErr.Trim()}");
        return new RestorePointResult(false, reason);
    }

    /// <summary>Abre o Restaurar Sistema do Windows.</summary>
    public static bool OpenSystemRestore(Logger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Não foi possível abrir o Restaurar Sistema", ex);
            return false;
        }
    }
}

/// <summary>
/// Ponto de restauração opcional (Configurações > "Criar ponto de restauração antes de mudanças").
/// Desligado, devolve "pulado" com sucesso, e o fluxo segue sem perguntar.
/// </summary>
public sealed class OptionalRestorePointService(IRestorePointService inner, Func<bool> enabled, Logger logger) : IRestorePointService
{
    public const string SkippedMessage = "Ponto de restauração não criado: a opção está desligada em Configurações.";

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        if (enabled()) return inner.CreateAsync(description, ct);
        logger.Info($"{SkippedMessage} ({description})");
        return Task.FromResult(new RestorePointResult(true, SkippedMessage));
    }
}
