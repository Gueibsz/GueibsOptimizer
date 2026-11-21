namespace GueibsOptimizer.Core;

/// <summary>Ações sem janela chamadas pelo instalador/desinstalador.</summary>
public enum SetupCommand
{
    None,
    AutoStartOff,      // --autostart-off     apaga a tarefa de início com o Windows deixada por versões antigas
    DefenderAdd,       // --defender-add      exclui SOMENTE a pasta do app no Defender
    UninstallCleanup,  // --uninstall-cleanup apaga a tarefa de início, a revisão semanal, a exclusão do Defender e os componentes do app
    MigrateLegacy,     // --migrate-legacy    leva tarefas e plano da versão antiga (GameOptimizer) para os nomes novos
    InstallComponents  // --install-components pawnio,presentmon  baixa componentes do app de fontes oficiais
}

/// <summary>
/// Argumentos aceitos:
///   --dry-run           abre o app em modo simulação (nada é alterado no sistema)
///   --dry-run --report  sem janela: grava em log (e no console, se houver) o que o Boost alteraria
///   --weekly            revisão semanal silenciosa (Fase 2)
///   --autostart         aberto pela tarefa de logon: inicia minimizado
///   --autostart-off / --defender-add / --uninstall-cleanup / --migrate-legacy / --install-components &lt;lista&gt;  (instalador)
/// </summary>
public sealed record CommandLineOptions(bool DryRun, bool Report, bool Weekly, IReadOnlyList<string> Unknown)
{
    public bool AutoStart { get; init; }
    public SetupCommand Setup { get; init; }

    /// <summary>Esta instância já é a tentativa de elevação (evita reiniciar em ciclo).</summary>
    public bool Elevated { get; init; }

    /// <summary>--screenshots &lt;pasta&gt;: abre em simulação, salva um PNG de cada página (temas e idiomas) e fecha.</summary>
    public string? ScreenshotsDir { get; init; }

    /// <summary>
    /// --selftest --suite &lt;nome&gt; --report &lt;arquivo.json&gt; [--baseline &lt;arquivo.json&gt;]: bateria de testes sem janela.
    /// Aplica e reverte ajustes REAIS (só quando chamado explicitamente) e grava o resultado em JSON.
    /// </summary>
    public bool SelfTest { get; init; }
    public string? Suite { get; init; }
    public string? ReportPath { get; init; }
    public string? BaselinePath { get; init; }

    /// <summary>--install-components: só nomes conhecidos (pawnio, presentmon); o resto é ignorado.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        bool dryRun = false, report = false, weekly = false, autoStart = false, elevated = false, selfTest = false;
        string? screenshots = null, suite = null, reportPath = null, baseline = null, components = null;
        var setup = SetupCommand.None;
        var unknown = new List<string>();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var raw = list[i];
            var arg = raw.Trim().ToLowerInvariant();
            bool HasValue() => i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
            switch (arg)
            {
                case "--selftest": selfTest = true; break;
                case "--suite":
                    if (HasValue()) suite = list[++i].Trim().ToLowerInvariant();
                    else unknown.Add(raw);
                    break;
                case "--baseline":
                    if (HasValue()) baseline = list[++i].Trim();
                    else unknown.Add(raw);
                    break;
                case "--screenshots":
                    if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) screenshots = list[++i].Trim();
                    else unknown.Add(raw);
                    break;
                case "--dry-run" or "/dry-run" or "--simular": dryRun = true; break;
                case "--report" or "/report":
                    report = true;
                    if (HasValue()) reportPath = list[++i].Trim();
                    break;
                case "--weekly" or "/weekly": weekly = true; break;
                case "--autostart": autoStart = true; break;
                case "--elevated": elevated = true; break;
                case "--autostart-off": setup = SetupCommand.AutoStartOff; break;
                case "--defender-add": setup = SetupCommand.DefenderAdd; break;
                case "--uninstall-cleanup": setup = SetupCommand.UninstallCleanup; break;
                case "--migrate-legacy": setup = SetupCommand.MigrateLegacy; break;
                case "--install-components":
                    setup = SetupCommand.InstallComponents;
                    if (HasValue()) components = list[++i];
                    break;
                case "": break;
                default: unknown.Add(raw); break;
            }
        }
        // O --report do autoteste é só o caminho do JSON: não é o relatório de simulação.
        if (selfTest) report = false;
        // Relatório sem janela só existe em simulação: nunca aplica nada sem interface.
        if (report) dryRun = true;
        // Capturas de tela também: a interface inteira roda sem poder alterar o sistema.
        if (screenshots is not null) dryRun = true;
        return new CommandLineOptions(dryRun, report, weekly, unknown)
        {
            AutoStart = autoStart, Setup = setup, Elevated = elevated, ScreenshotsDir = screenshots,
            SelfTest = selfTest, Suite = suite, ReportPath = reportPath, BaselinePath = baseline,
            Components = Services.AppComponentsInstaller.ParseList(components)
        };
    }
}
