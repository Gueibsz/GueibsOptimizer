using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GueibsOptimizer.ViewModels;

/// <summary>
/// Item do menu. <paramref name="Tab"/> abre uma aba específica de uma página com abas; <paramref name="OnOpen"/>
/// roda depois de abrir a página (ex.: abrir a janela do teste do mouse).
/// </summary>
public sealed record NavItem(string Title, string Glyph, object Page, string Group = "", int? Tab = null, Action? OnOpen = null);

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Action<bool>? _saveCollapsed;

    public MainViewModel(IReadOnlyList<NavItem> navItems, bool isDryRun, bool navCollapsed = false, Action<bool>? saveCollapsed = null)
    {
        NavItems = navItems;
        IsDryRun = isDryRun;
        _selectedNav = navItems[0];
        _isNavCollapsed = navCollapsed;
        _saveCollapsed = saveCollapsed;
        NavGroups = navItems.GroupBy(i => i.Group).Select(g => new NavGroup(g.Key, g.ToList())).ToList();
    }

    public IReadOnlyList<NavItem> NavItems { get; }
    public IReadOnlyList<NavGroup> NavGroups { get; }
    public bool IsDryRun { get; }

    public static string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(2) ?? "?"; // regra de 2 dígitos: X.0 a X.9
    public string VersionText { get; } = $"Versão {Version}";
    public string ShortVersionText { get; } = $"v{Version}";
    /// <summary>8.11: "Windows 11 23H2 · build 22631.4460" no rodapé do menu.</summary>
    public string OsText { get; } = GueibsOptimizer.Core.OsInfo.Current;

    [ObservableProperty] private NavItem _selectedNav;

    /// <summary>Menu recolhido: só ícones. O estado fica salvo em settings.json.</summary>
    [ObservableProperty] private bool _isNavCollapsed;

    partial void OnIsNavCollapsedChanged(bool value) => _saveCollapsed?.Invoke(value);

    [RelayCommand]
    private void ToggleNav() => IsNavCollapsed = !IsNavCollapsed;

    [RelayCommand]
    private void SelectNav(NavItem? item)
    {
        if (item is null) return;
        if (item.Tab is { } tab && item.Page is ITabbedPage tabbed) tabbed.SelectedTab = tab;
        SelectedNav = item;
        item.OnOpen?.Invoke();
    }
}
