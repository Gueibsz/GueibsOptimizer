using System.Text.RegularExpressions;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public enum InstallOutcome
{
    Installed,
    AlreadyInstalled,
    RebootRequired,
    Failed,
    Simulated
}

public sealed record InstallResult(string Id, InstallOutcome Outcome, int ExitCode, string Message)
{
    public string OutcomeText => Outcome switch
    {
        InstallOutcome.Installed => "Instalado",
        InstallOutcome.AlreadyInstalled => "Já instalado",
        InstallOutcome.RebootRequired => "Instalado (reinício necessário)",
        InstallOutcome.Simulated => "Simulado",
        _ => "Falhou"
    };
}

public sealed record UpgradeCandidate(string Name, string Id, string Version, string Available);

/// <summary>
/// Instalação e atualização via winget. Ids são validados antes de virar argumento e passados
/// por ArgumentList. Códigos de saída do winget (HRESULT) são traduzidos para mensagens claras.
/// </summary>
public sealed class WingetService(Shell shell, Logger logger)
{
    public const string AppInstallerStoreLink = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    // Códigos documentados do winget (AppInstallerErrors).
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int NoApplicationsFound = unchecked((int)0x8A150014);
    private const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    private const int InstallPackageInUse = unchecked((int)0x8A150101);
    private const int InstallInProgress = unchecked((int)0x8A150102);
    private const int InstallFileInUse = unchecked((int)0x8A150103);
    private const int InstallMissingDependency = unchecked((int)0x8A150104);
    private const int InstallDiskFull = unchecked((int)0x8A150105);
    private const int InstallInsufficientMemory = unchecked((int)0x8A150106);
    private const int InstallNoNetwork = unchecked((int)0x8A150107);
    private const int InstallRebootRequiredToFinish = unchecked((int)0x8A150109);
    private const int InstallRebootRequiredToInstall = unchecked((int)0x8A15010A);
    private const int InstallRebootInitiated = unchecked((int)0x8A15010B);
    private const int InstallCancelledByUser = unchecked((int)0x8A15010C);
    private const int InstallAlreadyInstalled = unchecked((int)0x8A15010D);
    private const int InstallDowngrade = unchecked((int)0x8A15010E);
    private const int InstallBlockedByPolicy = unchecked((int)0x8A15010F);
    private const int MsiRebootRequired = 3010;

    public static InstallResult Classify(string id, int exitCode, string output)
    {
        string text = output ?? string.Empty;
        (InstallOutcome outcome, string message) = exitCode switch
        {
            0 when Regex.IsMatch(text, "already installed|j[aá] est[aá] instalad", RegexOptions.IgnoreCase) =>
                (InstallOutcome.AlreadyInstalled, "O programa já estava instalado."),
            0 => (InstallOutcome.Installed, "Instalado com sucesso."),
            PackageAlreadyInstalled or InstallAlreadyInstalled or UpdateNotApplicable =>
                (InstallOutcome.AlreadyInstalled, "O programa já estava instalado (sem atualização disponível)."),
            InstallRebootRequiredToFinish or InstallRebootRequiredToInstall or InstallRebootInitiated or MsiRebootRequired =>
                (InstallOutcome.RebootRequired, "Instalado; reinicie o PC para concluir."),
            NoApplicationsFound => (InstallOutcome.Failed, "Pacote não encontrado no winget."),
            InstallPackageInUse or InstallFileInUse => (InstallOutcome.Failed, "O programa está aberto. Feche-o e tente de novo."),
            InstallInProgress => (InstallOutcome.Failed, "Outra instalação está em andamento. Tente de novo em instantes."),
            InstallMissingDependency => (InstallOutcome.Failed, "Falta uma dependência do programa."),
            InstallDiskFull => (InstallOutcome.Failed, "Espaço em disco insuficiente."),
            InstallInsufficientMemory => (InstallOutcome.Failed, "Memória insuficiente para instalar."),
            InstallNoNetwork => (InstallOutcome.Failed, "Sem conexão com a internet."),
            InstallCancelledByUser => (InstallOutcome.Failed, "Instalação cancelada."),
            InstallDowngrade => (InstallOutcome.AlreadyInstalled, "Uma versão mais nova já está instalada."),
            InstallBlockedByPolicy => (InstallOutcome.Failed, "Bloqueado por política da organização."),
            _ => (InstallOutcome.Failed, $"O instalador terminou com o código 0x{exitCode:X8}.")
        };
        return new InstallResult(id, outcome, exitCode, message);
    }

    public async Task<bool> IsAvailableAsync()
    {
        var r = await shell.RunAsync("winget.exe", ["--version"], TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return r.Success && r.StdOut.TrimStart().StartsWith('v');
    }

    public async Task<InstallResult> InstallAsync(string id, CancellationToken ct = default)
    {
        if (!CatalogParser.IsValidWingetId(id))
            return new InstallResult(id, InstallOutcome.Failed, -1, "Id de pacote inválido.");

        var r = await shell.RunMutatingAsync($"Instalar {id}", "winget.exe",
        [
            "install", "--id", id, "-e", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        ], TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);

        if (r.Simulated) return new InstallResult(id, InstallOutcome.Simulated, 0, "Simulado (nada foi instalado).");
        if (r.TimedOut) return new InstallResult(id, InstallOutcome.Failed, -1, "A instalação demorou demais (30 min).");

        var result = Classify(id, r.ExitCode, r.StdOut + r.StdErr);
        logger.Write(result.Outcome == InstallOutcome.Failed ? LogLevel.Warning : LogLevel.Info, $"winget {id}: {result.OutcomeText}. {result.Message}");
        return result;
    }

    public async Task<ShellResult> UninstallAsync(string id, CancellationToken ct = default)
    {
        if (!CatalogParser.IsValidWingetId(id))
            return new ShellResult(-1, string.Empty, "Id de pacote inválido.", false);
        var r = await shell.RunMutatingAsync($"Desinstalar {id}", "winget.exe",
        [
            "uninstall", "--id", id, "-e", "--silent", "--accept-source-agreements", "--disable-interactivity"
        ], TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        logger.Write(r.Success ? LogLevel.Info : LogLevel.Warning, $"winget uninstall {id}: código {r.ExitCode}.");
        return r;
    }

    public async Task<IReadOnlyList<UpgradeCandidate>> ListUpgradesAsync()
    {
        var r = await shell.RunAsync("winget.exe",
            ["upgrade", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(2), new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
        return ParseUpgradeTable(r.StdOut);
    }

    public async Task<ShellResult> UpgradeAllAsync(CancellationToken ct = default)
    {
        var r = await shell.RunMutatingAsync("Atualizar todos os programas", "winget.exe",
        [
            "upgrade", "--all", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        ], TimeSpan.FromMinutes(90), ct).ConfigureAwait(false);
        logger.Info(r.Simulated ? "Atualização simulada." : $"winget upgrade --all terminou com código {r.ExitCode}.");
        return r;
    }

    /// <summary>
    /// Lê a tabela do "winget upgrade" (cabeçalho localizado): as colunas são localizadas pelas posições
    /// dos títulos na linha acima do separador "----".
    /// </summary>
    public static IReadOnlyList<UpgradeCandidate> ParseUpgradeTable(string output)
    {
        var lines = output.Replace("\r", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();
        int sep = lines.FindIndex(l => l.StartsWith("---", StringComparison.Ordinal));
        if (sep <= 0) return [];

        var header = lines[sep - 1];
        var starts = Regex.Matches(header, @"\S+").Select(m => m.Index).ToList();
        if (starts.Count < 4) return [];

        var result = new List<UpgradeCandidate>();
        foreach (var line in lines.Skip(sep + 1))
        {
            if (line.Length <= starts[3]) continue; // rodapé ("x atualizações disponíveis")
            string Col(int i) => i + 1 < starts.Count
                ? Slice(line, starts[i], starts[i + 1])
                : Slice(line, starts[i], line.Length);
            var id = Col(1);
            if (!CatalogParser.IsValidWingetId(id)) continue;
            result.Add(new UpgradeCandidate(Col(0), id, Col(2), Col(3)));
        }
        return result;
    }

    private static string Slice(string s, int from, int to) =>
        from >= s.Length ? string.Empty : s[from..Math.Min(to, s.Length)].Trim();
}
