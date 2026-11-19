using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class AppItemViewModel(AppItem item) : ObservableObject
{
    public AppItem Item { get; } = item;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string Category => Item.Category;
    public string WingetId => Item.WingetId;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isWorking;
}

public sealed partial class InstallAppsViewModel : ObservableObject, ITabbedPage
{
    [ObservableProperty] private int _selectedTab;

    private readonly WingetService _winget;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;
    private CancellationTokenSource? _cts;

    public InstallAppsViewModel(WingetService winget, IDialogService dialogs, Logger logger, WizardViewModel wizard)
    {
        _winget = winget;
        _dialogs = dialogs;
        _logger = logger;
        Wizard = wizard;
        foreach (var a in CatalogService.Apps) Apps.Add(new AppItemViewModel(a));
        foreach (var a in Apps) a.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppItemViewModel.IsSelected)) OnPropertyChanged(nameof(SelectedCount)); };
        Categories = ["Todas", .. CatalogParser.Categories.Where(c => Apps.Any(a => a.Category == c))];
        View = CollectionViewSource.GetDefaultView(Apps);
        View.Filter = o => o is AppItemViewModel a && Matches(a);
        wizard.Catalog = this;
    }

    public WizardViewModel Wizard { get; }
    public ObservableCollection<AppItemViewModel> Apps { get; } = [];
    public ICollectionView View { get; }
    public IReadOnlyList<string> Categories { get; }
    public ObservableCollection<UpgradeCandidate> Upgrades { get; } = [];

    public int SelectedCount => Apps.Count(a => a.IsSelected);
    public string CatalogInfo => $"{Apps.Count} programas no catálogo, todos verificados no winget.";

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCategory = "Todas";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _queueProgress;
    [ObservableProperty] private string _queueStatus = "Selecione os programas e clique em Instalar.";
    [ObservableProperty] private string _report = string.Empty;
    [ObservableProperty] private string _updatesStatus = "Clique em Verificar para listar o que pode ser atualizado.";

    partial void OnSearchTextChanged(string value) => View.Refresh();
    partial void OnSelectedCategoryChanged(string value) => View.Refresh();

    private bool Matches(AppItemViewModel a)
    {
        if (SelectedCategory != "Todas" && a.Category != SelectedCategory) return false;
        var q = SearchText.Trim();
        return q.Length == 0 ||
               a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               a.WingetId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               a.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private void SelectEssentials()
    {
        foreach (var a in Apps)
            if (CatalogService.GamingEssentials.Contains(a.WingetId, StringComparer.OrdinalIgnoreCase)) a.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var a in Apps) a.IsSelected = false;
    }

    public async Task<bool> EnsureWingetAsync()
    {
        if (await _winget.IsAvailableAsync()) return true;
        if (_dialogs.Confirm("winget não encontrado",
                "O instalador de pacotes do Windows (winget) não foi encontrado.\n\nEle vem no \"Instalador de Aplicativo\" da Microsoft Store. Abrir a página na Store agora?"))
        {
            try { Process.Start(new ProcessStartInfo(WingetService.AppInstallerStoreLink) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { _logger.Error("Não foi possível abrir a Microsoft Store", ex); }
        }
        return false;
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        if (IsBusy) return;
        var selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) { _dialogs.Info("Instalar", "Selecione ao menos um programa."); return; }
        if (!await EnsureWingetAsync()) return;
        if (!_dialogs.Confirm("Instalar programas",
                $"Instalar {selected.Count} programa(s) pelo winget, sem janelas?\n\n{string.Join("\n", selected.Take(25).Select(a => "• " + a.Name))}{(selected.Count > 25 ? "\n…" : "")}"))
            return;

        var results = await InstallQueueAsync(selected.Select(a => a.WingetId).ToList(), null);
        Report = BuildReport(results);
    }

    /// <summary>Fila sequencial (o winget não instala em paralelo). Usada também pelo assistente.</summary>
    public async Task<IReadOnlyList<InstallResult>> InstallQueueAsync(IReadOnlyList<string> ids, Action<InstallResult>? afterEach)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var results = new List<InstallResult>();
        foreach (var a in Apps.Where(a => ids.Contains(a.WingetId))) a.Status = "Na fila";
        try
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (_cts.IsCancellationRequested) break;
                var vm = Apps.FirstOrDefault(a => a.WingetId == ids[i]);
                QueueStatus = $"Instalando {vm?.Name ?? ids[i]} ({i + 1} de {ids.Count})...";
                QueueProgress = i * 100.0 / ids.Count;
                if (vm is not null) { vm.Status = "Instalando..."; vm.IsWorking = true; }

                InstallResult result;
                try { result = await _winget.InstallAsync(ids[i], _cts.Token); }
                catch (OperationCanceledException) { result = new InstallResult(ids[i], InstallOutcome.Failed, -1, "Cancelado."); }

                results.Add(result);
                if (vm is not null) { vm.Status = result.OutcomeText; vm.IsWorking = false; }
                afterEach?.Invoke(result);
            }
            QueueProgress = 100;
            QueueStatus = _cts.IsCancellationRequested ? "Fila cancelada." : "Fila concluída.";
        }
        finally
        {
            IsBusy = false;
            foreach (var a in Apps.Where(a => a.Status == "Na fila")) a.Status = string.Empty;
        }
        return results;
    }

    public static string BuildReport(IReadOnlyList<InstallResult> results)
    {
        if (results.Count == 0) return string.Empty;
        if (results.All(r => r.Outcome == InstallOutcome.Simulated))
            return $"Simulação: {results.Count} programa(s) seriam instalados. Nada foi instalado.";
        var lines = new List<string>
        {
            $"Relatório: {results.Count(r => r.Outcome == InstallOutcome.Installed)} instalados, " +
            $"{results.Count(r => r.Outcome == InstallOutcome.AlreadyInstalled)} já existiam, " +
            $"{results.Count(r => r.Outcome == InstallOutcome.RebootRequired)} pedem reinício, " +
            $"{results.Count(r => r.Outcome == InstallOutcome.Failed)} falharam."
        };
        lines.AddRange(results.Where(r => r.Outcome is InstallOutcome.Failed or InstallOutcome.RebootRequired)
            .Select(r => $"• {r.Id}: {r.Message}"));
        return string.Join("\n", lines);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy) return;
        if (!await EnsureWingetAsync()) return;
        IsBusy = true;
        UpdatesStatus = "Consultando o winget...";
        try
        {
            var list = await _winget.ListUpgradesAsync();
            Upgrades.Clear();
            foreach (var u in list) Upgrades.Add(u);
            UpdatesStatus = list.Count == 0 ? "Tudo atualizado (ou nenhum programa gerenciado pelo winget)." : $"{list.Count} atualização(ões) disponível(is).";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao listar atualizações", ex);
            UpdatesStatus = "Não foi possível consultar as atualizações.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpgradeAllAsync()
    {
        if (IsBusy) return;
        if (Upgrades.Count == 0) await CheckUpdatesAsync();
        if (Upgrades.Count == 0) return;
        if (!_dialogs.Confirm("Atualizar tudo",
                $"Atualizar {Upgrades.Count} programa(s)?\n\n{string.Join("\n", Upgrades.Take(25).Select(u => $"• {u.Name}: {u.Version} → {u.Available}"))}\n\nFeche os programas que serão atualizados."))
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        UpdatesStatus = "Atualizando (pode levar vários minutos)...";
        try
        {
            var r = await _winget.UpgradeAllAsync(_cts.Token);
            UpdatesStatus = r.Simulated ? "Simulação: nada foi atualizado."
                : r.Success ? "Atualização concluída." : $"Atualização terminou com avisos (código 0x{r.ExitCode:X8}). Veja os detalhes no log.";
        }
        catch (OperationCanceledException)
        {
            UpdatesStatus = "Atualização cancelada.";
        }
        finally
        {
            IsBusy = false;
        }
        if (!UpdatesStatus.StartsWith("Simulação", StringComparison.Ordinal)) await CheckUpdatesAsync();
    }
}
