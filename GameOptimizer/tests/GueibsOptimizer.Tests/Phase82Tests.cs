using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;
using GueibsOptimizer.ViewModels;

namespace GueibsOptimizer.Tests;

public partial class Phase82Tests
{
    [Fact]
    public void NavGroupsMatchTheSpec()
    {
        var groups = NavLayout.Groups.ToDictionary(g => g.Group, g => g.Titles);
        Assert.Equal(["Painel", "Manutenção", "Desempenho", "Meu PC", "Hardware Monitor", "Otimização Completa"], groups[NavLayout.Principal]);
        // 9.3 acrescentou "Preferências" depois de Windows.
        Assert.Equal(["Formatação", "Debloat", "Windows", "Preferências", "Mouse", "Teclado", "GPU", "Perfis", "Jogos"], groups[NavLayout.Otimizar]);
        Assert.Equal(["Overlay", "Teste do mouse", "Teste do monitor", "Teste de velocidade", "Perda de pacotes", "DNS", "Recursos do Windows", "Correções", "Windows Update", "Atalhos", "Defender e IA", "Check-up"], groups[NavLayout.Ferramentas]);
        // Restauração continua acessível.
        Assert.Contains("Restauração", groups[NavLayout.Sistema]);
        var all = NavLayout.Groups.SelectMany(g => g.Titles).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void BuildFailsWhenAPageIsMissing()
    {
        var pages = NavLayout.Groups.SelectMany(g => g.Titles).ToDictionary(t => t, t => new NavItem(t, "x", new object()));
        var nav = NavLayout.Build(pages);
        Assert.Equal(NavLayout.Principal, nav[0].Group);
        Assert.Equal("Painel", nav[0].Title);
        pages.Remove("GPU");
        Assert.Throws<KeyNotFoundException>(() => NavLayout.Build(pages));
    }

    [Fact]
    public void CategoryFiltersSplitTheRegistryWithoutDuplicating()
    {
        using var key = new TestRegistryKey();
        var registry = TweakRegistry.CreateDefault(key.CreateContext(dryRun: true));
        var mouse = registry.All.Where(CategoryTweaksViewModel.IsMouse).Select(t => t.Id).ToList();
        var keyboard = registry.All.Where(CategoryTweaksViewModel.IsKeyboard).Select(t => t.Id).ToList();
        Assert.Equal(["input.mouse-accel-off"], mouse);
        Assert.Contains("input.keyboard-repeat", keyboard);
        Assert.Contains("input.accessibility-shortcuts-off", keyboard);
        Assert.All(registry.All.Where(CategoryTweaksViewModel.IsGpu), t => Assert.Equal(TweakCategory.Gpu, t.Category));
        Assert.All(registry.All.Where(CategoryTweaksViewModel.IsWindows), t => Assert.Equal(TweakCategory.Windows, t.Category));

        // Cada ajuste aparece em no máximo uma página de categoria (nada duplicado entre elas).
        Func<ITweak, bool>[] filters = [CategoryTweaksViewModel.IsMouse, CategoryTweaksViewModel.IsKeyboard, CategoryTweaksViewModel.IsGpu,
            CategoryTweaksViewModel.IsWindows, CategoryTweaksViewModel.IsPerformance, CategoryTweaksViewModel.IsPrivacy, PreferencesViewModel.IsPreference, DnsViewModel.IsNetwork];
        Assert.All(registry.All, t => Assert.Equal(1, filters.Count(f => f(t))));
    }

    [Fact]
    public async Task CategoryPageReadsRealStateFromTheSharedRegistry()
    {
        using var key = new TestRegistryKey();
        var fake = new FakeTweak("gpu.fake", 5, applied: true);
        var registry = new TweakRegistry([fake, new FakeTweak("other", 5)]);
        var vm = new CategoryTweaksViewModel("GPU", "s", "i", registry, new ScoreService(key.Logger), new NoDialogs(), key.Logger,
            t => t.Id == "gpu.fake");
        await vm.RefreshAsync();
        Assert.Single(vm.Items);
        Assert.True(vm.Items[0].IsApplied);
        Assert.Equal("1 de 1 ajustes desta página aplicados", vm.Summary);
    }

    private sealed partial class Tabbed : ObservableObject, ITabbedPage
    {
        [ObservableProperty] private int _selectedTab;
    }

    [Fact]
    public void SelectingNavOpensTabRunsActionAndSavesCollapsed()
    {
        var page = new Tabbed();
        int opened = 0;
        bool? saved = null;
        var items = new List<NavItem>
        {
            new("Painel", "x", new object()),
            new("Check-up", "x", page, Tab: 2),
            new("Teste do mouse", "x", new object(), OnOpen: () => opened++)
        };
        var vm = new MainViewModel(items, false, navCollapsed: false, saveCollapsed: v => saved = v);
        vm.SelectNavCommand.Execute(items[1]);
        Assert.Equal(2, page.SelectedTab);
        Assert.Same(items[1], vm.SelectedNav);
        vm.SelectNavCommand.Execute(items[2]);
        Assert.Equal(1, opened);

        vm.ToggleNavCommand.Execute(null);
        Assert.True(vm.IsNavCollapsed);
        Assert.True(saved);
        Assert.StartsWith("v", vm.ShortVersionText);
    }

    [Fact]
    public void SidebarIsCollapsibleAndShowsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GueibsOptimizer", "Views", "MainWindow.xaml"));
        Assert.Contains("ToggleNavCommand", xaml);
        Assert.Contains("NavGroups", xaml);
        Assert.Contains("VersionText", xaml);
        Assert.Contains("IsNavCollapsed", xaml);
        Assert.True(new AppSettings().NavCollapsed == false);
    }

    private sealed class NoDialogs : IDialogService
    {
        public bool Confirm(string title, string message) => false;
        public void Info(string title, string message) { }
        public void Error(string title, string message) { }
        public string? PickOpenFile(string filter) => null;
        public string? PickSaveFile(string filter, string defaultName) => null;
        public void CopyText(string text) { }
    }
}
