using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>
/// 9.3: Preferências do Windows (ajustes "pref.*" do tweaks.json) com estado real, mais "Reiniciar o Explorer agora"
/// (nunca automático, sempre por clique e com confirmação).
/// </summary>
public sealed partial class PreferencesViewModel(CategoryTweaksViewModel items, Shell shell, IDialogService dialogs, Logger logger) : ObservableObject
{
    public const string Prefix = "pref.";

    public static bool IsPreference(ITweak t) => t.Id.StartsWith(Prefix, StringComparison.Ordinal);

    public CategoryTweaksViewModel Items { get; } = items;

    [ObservableProperty] private string _explorerStatus = string.Empty;

    public static readonly string[] KillExplorerArgs = ["/f", "/im", "explorer.exe"];

    public Task RefreshAsync() => Items.RefreshAsync();

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!dialogs.Confirm("Reiniciar o Explorer",
                "A barra de tarefas e as janelas do Explorador vão fechar e abrir de novo em alguns segundos. Programas abertos não são fechados. Continuar?"))
            return;
        var r = await shell.RunMutatingAsync("Reiniciar o Explorer", "taskkill.exe", KillExplorerArgs);
        if (r.Simulated)
        {
            ExplorerStatus = "Simulação: o Explorer não foi reiniciado.";
            return;
        }
        try
        {
            await Task.Delay(1500);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
            ExplorerStatus = "Explorer reiniciado.";
            logger.Info("Explorer reiniciado pelo usuário.");
        }
        catch (Exception ex)
        {
            logger.Error("Não foi possível reabrir o Explorer", ex);
            dialogs.Error("Explorer", "Não foi possível reabrir o Explorer. Pressione Ctrl+Shift+Esc, Arquivo > Executar nova tarefa e digite explorer.");
        }
    }
}
