using System.Diagnostics;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>
/// 9.9: ações explícitas (nunca no Boost): desinstalar o OneDrive pelo winget (desfazer = reinstalar) e
/// abrir/instalar o O&amp;O ShutUp10++ (ID validado com "winget show" em 2026-11-19). Nada roda sozinho.
/// </summary>
public sealed class ExtraActionsService(TweakContext ctx) : IUndoProvider
{
    public const string OneDriveId = "Microsoft.OneDrive";
    public const string ShutUpId = "OO-Software.ShutUp10";
    public const string ShutUpUrl = "https://www.oo-software.com/en/shutup10";
    public const string OneDriveUndoId = "onedrive:uninstalled";

    public static IReadOnlyList<string> UninstallArgs(string id) => ["uninstall", "--id", id, "-e", "--silent", "--accept-source-agreements"];
    public static IReadOnlyList<string> InstallArgs(string id) => ["install", "--id", id, "-e", "--silent", "--accept-package-agreements", "--accept-source-agreements"];

    /// <summary>winget: 0 = OK. Retorna null se deu certo, senão a mensagem.</summary>
    public async Task<string?> UninstallOneDriveAsync()
    {
        var r = await ctx.Shell.RunMutatingAsync("Desinstalar o OneDrive", "winget.exe", UninstallArgs(OneDriveId), TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (!r.Success) return $"O winget não conseguiu desinstalar o OneDrive (código {r.ExitCode}).";
        if (!r.Simulated) ctx.SaveCustomBackup(OneDriveUndoId, "winget-id", OneDriveId, "OneDrive desinstalado pelo app");
        return null;
    }

    public async Task<string?> InstallShutUpAsync()
    {
        var r = await ctx.Shell.RunMutatingAsync("Instalar O&O ShutUp10++", "winget.exe", InstallArgs(ShutUpId), TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        return r.Success ? null : $"O winget não conseguiu instalar (código {r.ExitCode}).";
    }

    public void OpenShutUpPage()
    {
        try { Process.Start(new ProcessStartInfo(ShutUpUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { ctx.Logger.Error("Não foi possível abrir a página do O&O ShutUp10++", ex); }
    }

    public bool CanUndo(string id) => id == OneDriveUndoId;
    public string DisplayName(string id) => "OneDrive desinstalado (desfazer = reinstalar pelo winget)";

    public async Task UndoAsync(string id)
    {
        var r = await ctx.Shell.RunMutatingAsync("Reinstalar o OneDrive", "winget.exe", InstallArgs(OneDriveId), TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (r.Success && !ctx.DryRun) ctx.Backup.RemoveTweak(id);
    }
}
