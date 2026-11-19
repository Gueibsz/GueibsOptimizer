using System.ComponentModel;
using System.Diagnostics;

namespace GueibsOptimizer.Core;

public enum StartupDecision
{
    /// <summary>Já está elevado (ou em simulação, que não precisa de admin): segue normalmente.</summary>
    Continue,
    /// <summary>Não está elevado: reinicia a si mesmo com "runas" e encerra esta instância.</summary>
    Relaunch,
    /// <summary>Já tentou elevar (--elevated) e continua sem admin: mostra o erro, sem tentar de novo.</summary>
    ShowError
}

/// <summary>
/// Garante que o app rode como administrador. O manifesto já pede (requireAdministrator), mas há casos em que
/// o processo nasce sem elevação (ex.: iniciado por outro programa sem ShellExecute). O argumento --elevated
/// marca a segunda tentativa e impede um ciclo infinito de reinícios.
/// </summary>
public static class Elevation
{
    public const string ElevatedFlag = "--elevated";

    public static StartupDecision Decide(bool isAdmin, bool dryRun, bool alreadyTriedElevation) =>
        isAdmin || dryRun ? StartupDecision.Continue
        : alreadyTriedElevation ? StartupDecision.ShowError
        : StartupDecision.Relaunch;

    /// <summary>Argumentos da nova instância: os originais (sem --elevated repetido) + --elevated.</summary>
    public static IReadOnlyList<string> RelaunchArguments(IEnumerable<string> original) =>
        [.. original.Where(a => !a.Equals(ElevatedFlag, StringComparison.OrdinalIgnoreCase)), ElevatedFlag];

    /// <summary>
    /// Inicia a si mesmo com o verbo "runas" (mostra o UAC). Retorna null se iniciou,
    /// ou a mensagem de erro (ex.: usuário recusou o UAC).
    /// </summary>
    public static string? RelaunchElevated(IEnumerable<string> originalArgs)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "Não foi possível descobrir o caminho do executável.";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in RelaunchArguments(originalArgs)) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC recusado
        {
            return "A permissão de administrador foi recusada.";
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
    }
}
