using System.Windows;

namespace GueibsOptimizer.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
    string? PickOpenFile(string filter);
    string? PickFolder(string title) => null;
    string? PickSaveFile(string filter, string defaultName);
    void CopyText(string text);
}

/// <summary>Diálogos simples, sempre na thread da interface.</summary>
public sealed class DialogService : IDialogService
{
    private static string Tr(string s) => GueibsOptimizer.Core.LocalizationService.Tr(s);

    public bool Confirm(string title, string message) =>
        OnUi(() => MessageBox.Show(Owner(), Tr(message), Tr(title), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public void Info(string title, string message) =>
        OnUi(() => MessageBox.Show(Owner(), Tr(message), Tr(title), MessageBoxButton.OK, MessageBoxImage.Information));

    public void Error(string title, string message) =>
        OnUi(() => MessageBox.Show(Owner(), Tr(message), Tr(title), MessageBoxButton.OK, MessageBoxImage.Warning));

    public string? PickOpenFile(string filter) => OnUi(() =>
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Owner()) == true ? dlg.FileName : null;
    });

    public string? PickFolder(string title) => OnUi(() =>
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog(Owner()) == true ? dlg.FolderName : null;
    });

    public string? PickSaveFile(string filter, string defaultName) => OnUi(() =>
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dlg.ShowDialog(Owner()) == true ? dlg.FileName : null;
    });

    public void CopyText(string text) => OnUi(() =>
    {
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    });

    private static Window Owner() => Application.Current.MainWindow;

    private static T OnUi<T>(Func<T> func)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.CheckAccess() ? func() : dispatcher.Invoke(func);
    }
}
