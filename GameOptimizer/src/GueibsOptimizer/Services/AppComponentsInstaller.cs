using System.IO;
using System.Text.Json;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>
/// Registro (em %ProgramData%, vale para a máquina toda) de quais componentes foram instalados pelo próprio app.
/// Serve para a desinstalação remover só o que o app trouxe: um PawnIO que já existia antes (de outro programa,
/// como Fan Control ou Libre Hardware Monitor) continua instalado.
/// </summary>
public sealed class ComponentsState
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GueibsOptimizer", "components.json");

    public bool PawnIOInstalledByApp { get; set; }

    public static ComponentsState Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<ComponentsState>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }

    /// <summary>Chamado quando o app (instalador ou tela de Hardware) instalou o PawnIO de verdade.</summary>
    public static void MarkPawnIOInstalledByApp(string? path = null)
    {
        path ??= DefaultPath;
        try { new ComponentsState { PawnIOInstalledByApp = true }.Save(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Versão 2.5: componentes que o próprio app usa, sempre instalados pelo instalador (sem opção para desmarcar) ou depois,
/// dentro de cada tela. Só fontes oficiais: PawnIO pelo winget (namazso.PawnIO) e PresentMon da página de releases
/// da Intel com assinatura verificada. Falha em um componente nunca interrompe a instalação do app.
/// Na desinstalação do app os dois saem junto (o PawnIO só se foi o app que o instalou).
/// </summary>
public sealed class AppComponentsInstaller(
    WingetService winget, PresentMonDownloader presentMon, Func<string?> locatePresentMon, Logger logger,
    bool dryRun = false, string? stateFile = null, string? presentMonDirectory = null)
{
    public const string PawnIO = "pawnio";
    public const string PresentMon = "presentmon";
    public const string PawnIOWingetId = "namazso.PawnIO";
    public static readonly string[] Known = [PawnIO, PresentMon];

    private readonly string _stateFile = stateFile ?? ComponentsState.DefaultPath;
    private readonly string _presentMonDir = presentMonDirectory ?? PresentMonDownloader.ToolsDirectory;

    /// <summary>"pawnio,presentmon" → lista sem repetição; nomes desconhecidos são ignorados (nunca executados).</summary>
    public static IReadOnlyList<string> ParseList(string? value) =>
        (value ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToLowerInvariant())
            .Where(v => Known.Contains(v))
            .Distinct()
            .ToList();

    /// <summary>Instala cada componente pedido. Retorna 0 se todos ficaram disponíveis; 1 se algum falhou (detalhes no log).</summary>
    public async Task<int> InstallAsync(IEnumerable<string> components, CancellationToken ct = default)
    {
        int exit = 0;
        foreach (var c in components)
        {
            bool ok = c switch
            {
                PawnIO => await InstallPawnIOAsync(ct).ConfigureAwait(false),
                PresentMon => await InstallPresentMonAsync(ct).ConfigureAwait(false),
                _ => true
            };
            if (!ok) exit = 1;
        }
        return exit;
    }

    /// <summary>
    /// Desinstalação do app: apaga o PresentMon baixado pelo app e desinstala o PawnIO se foi o app que o instalou.
    /// Retorna 0 se tudo saiu; 1 se algo ficou (detalhes no log). Nunca impede a desinstalação.
    /// </summary>
    public async Task<int> UninstallAsync(CancellationToken ct = default)
    {
        int exit = 0;
        if (!RemovePresentMon()) exit = 1;
        if (!await UninstallPawnIOAsync(ct).ConfigureAwait(false)) exit = 1;
        return exit;
    }

    private async Task<bool> InstallPawnIOAsync(CancellationToken ct)
    {
        try
        {
            if (!await winget.IsAvailableAsync().ConfigureAwait(false))
            {
                logger.Warn("PawnIO não instalado: winget não encontrado. Dá para instalar depois em Hardware Monitor.");
                return false;
            }
            var r = await winget.InstallAsync(PawnIOWingetId, ct).ConfigureAwait(false);
            logger.Info($"Componente PawnIO: {r.OutcomeText}.");
            // Só marca como "do app" se ele instalou agora. Se já existia, a marca de uma instalação anterior do app é mantida.
            if (r.Outcome is InstallOutcome.Installed or InstallOutcome.RebootRequired)
                ComponentsState.MarkPawnIOInstalledByApp(_stateFile);
            return r.Outcome != InstallOutcome.Failed;
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao instalar o PawnIO", ex);
            return false;
        }
    }

    private async Task<bool> InstallPresentMonAsync(CancellationToken ct)
    {
        try
        {
            if (locatePresentMon() is { } existing)
            {
                logger.Info($"Componente PresentMon: já disponível em {existing}.");
                return true;
            }
            var path = await presentMon.DownloadAsync(null, ct).ConfigureAwait(false);
            logger.Info($"Componente PresentMon: baixado ({path}).");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao baixar o PresentMon (dá para baixar depois em Overlay & Testes)", ex);
            return false;
        }
    }

    /// <summary>Apaga só a pasta onde o app baixa o PresentMon (um PresentMon instalado pela Intel ou pelo usuário fica).</summary>
    private bool RemovePresentMon()
    {
        try
        {
            if (!Directory.Exists(_presentMonDir))
            {
                logger.Info("Componente PresentMon: nada a remover.");
                return true;
            }
            if (dryRun)
            {
                logger.Simulated($"Apagar a pasta do PresentMon baixado pelo app: {_presentMonDir}");
                return true;
            }
            Directory.Delete(_presentMonDir, recursive: true);
            var tools = Path.GetDirectoryName(_presentMonDir);
            if (tools is not null && Directory.Exists(tools) && !Directory.EnumerateFileSystemEntries(tools).Any())
                Directory.Delete(tools);
            logger.Info($"Componente PresentMon: removido ({_presentMonDir}).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("Não foi possível apagar o PresentMon baixado pelo app", ex);
            return false;
        }
    }

    private async Task<bool> UninstallPawnIOAsync(CancellationToken ct)
    {
        try
        {
            var state = ComponentsState.Load(_stateFile);
            if (!state.PawnIOInstalledByApp)
            {
                logger.Info("Componente PawnIO: mantido (não foi instalado pelo Gueibs Optimizer; outro programa pode usá-lo).");
                return true;
            }
            if (!await winget.IsAvailableAsync().ConfigureAwait(false))
            {
                logger.Warn("PawnIO não removido: winget não encontrado. Desinstale-o em Configurações > Aplicativos.");
                return false;
            }
            var r = await winget.UninstallAsync(PawnIOWingetId, ct).ConfigureAwait(false);
            if (r.Simulated) return true;
            // 0x8A150014 = nenhum pacote instalado com esse id (já tinha sido removido): também conta como concluído.
            bool ok = r.Success || unchecked((uint)r.ExitCode) == 0x8A150014;
            if (ok)
            {
                try { File.Delete(_stateFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                logger.Info("Componente PawnIO: desinstalado.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            logger.Error("Falha ao desinstalar o PawnIO", ex);
            return false;
        }
    }
}
