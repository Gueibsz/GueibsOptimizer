using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class DebloatItemViewModel(DebloatItem item) : ObservableObject
{
    public DebloatItem Item { get; } = item;
    public string Name => Item.DisplayName;
    public string PackageName => Item.Package.Name;
    public string CategoryText => Item.CategoryText;
    public string SizeText => Item.SizeText;
    public bool IsRemovable => Item.IsRemovable;
    public string InfoText => $"{Item.Explanation}\n\nPacote: {Item.Package.Name}\nTamanho aproximado: {Item.SizeText}" +
                              (Item.IsRemovable ? $"\n\n{Item.ReinstallText}\n\nRemover um app da Store não é 100% reversível por registro (risco Médio): o desfazer reinstala o app, mas configurações dele podem se perder." : string.Empty);

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = string.Empty;
}

/// <summary>Tela Debloat: remover apps indesejados com segurança, mais extras de privacidade (ITweak reversíveis).</summary>
public sealed partial class DebloatViewModel : ObservableObject
{
    public static readonly string[] ExtraTweakIds =
    [
        "privacy.start-lockscreen-ads-off", "windows.widgets-off", "windows.bing-search-off",
        "windows.onedrive-startup-off", "debloat.onedrive-sync-off", "windows.classic-context-menu", "windows.visual-effects-performance"
    ];

    private readonly DebloatService _debloat;
    private readonly IRestorePointService _restorePoints;
    private readonly ScoreService _score;
    private readonly TweakRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;
    private bool _loaded;

    public DebloatViewModel(DebloatService debloat, IRestorePointService restorePoints, TweakRegistry registry,
        ScoreService score, IDialogService dialogs, Logger logger)
    {
        _debloat = debloat;
        _restorePoints = restorePoints;
        _registry = registry;
        _score = score;
        _dialogs = dialogs;
        _logger = logger;
        foreach (var id in ExtraTweakIds)
            if (registry.Find(id) is { } t) Extras.Add(new TweakItemViewModel(t, dialogs, logger, registry.PointsOf(t)));
        WeakReferenceMessenger.Default.Register<DebloatViewModel, TweaksChangedMessage>(this, static (vm, msg) => _ = vm.RefreshExtrasAsync());
    }

    public ObservableCollection<DebloatItemViewModel> SafeItems { get; } = [];
    public ObservableCollection<DebloatItemViewModel> OptionalItems { get; } = [];
    public ObservableCollection<DebloatItemViewModel> ProtectedItems { get; } = [];
    public ObservableCollection<DebloatItemViewModel> UnclassifiedItems { get; } = [];
    public ObservableCollection<TweakItemViewModel> Extras { get; } = [];

    // --- 9.9: OneDrive (desinstalar) e O&O ShutUp10++ — só por clique, nunca no Boost ---
    public ExtraActionsService? ExtraActions { get; set; }
    [ObservableProperty] private string _extraStatus = string.Empty;

    [RelayCommand]
    private async Task UninstallOneDriveAsync()
    {
        if (ExtraActions is null) return;
        if (!_dialogs.Confirm("Desinstalar o OneDrive",
                "O OneDrive será desinstalado pelo winget (winget uninstall --id Microsoft.OneDrive).\n\nAtenção: arquivos que estão só na nuvem (ícone de nuvem) não ficam no PC; baixe o que precisar antes. Os arquivos da pasta local não são apagados.\n\nO desfazer (aba Restauração) reinstala o OneDrive. Continuar?"))
            return;
        if (!_dialogs.Confirm("Confirmar de novo", "Confirma mesmo desinstalar o OneDrive?")) return;
        var err = await Task.Run(ExtraActions.UninstallOneDriveAsync);
        ExtraStatus = err ?? "OneDrive desinstalado. Para reinstalar, use a aba Restauração.";
    }

    [RelayCommand]
    private void OpenShutUpPage() => ExtraActions?.OpenShutUpPage();

    [RelayCommand]
    private async Task InstallShutUpAsync()
    {
        if (ExtraActions is null) return;
        if (!_dialogs.Confirm("O&O ShutUp10++", "Instalar o O&O ShutUp10++ (programa gratuito de outra empresa) pelo winget (OO-Software.ShutUp10)? Ele não é executado automaticamente; você o abre quando quiser."))
            return;
        var err = await Task.Run(ExtraActions.InstallShutUpAsync);
        ExtraStatus = err ?? "O&O ShutUp10++ instalado. Abra pelo Menu Iniciar quando quiser.";
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Clique em Atualizar lista para ver os apps instalados.";
    [ObservableProperty] private bool _includeProvisioned;
    [ObservableProperty] private string _provisionedNote = string.Empty;
    [ObservableProperty] private string _report = string.Empty;

    private IEnumerable<DebloatItemViewModel> Removable => SafeItems.Concat(OptionalItems);

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshExtrasAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Lendo apps instalados (pode levar alguns segundos)...";
        try
        {
            var items = await _debloat.ListAsync();
            Fill(SafeItems, items, DebloatCategory.Safe);
            Fill(OptionalItems, items, DebloatCategory.Optional);
            Fill(ProtectedItems, items, DebloatCategory.Protected);
            Fill(UnclassifiedItems, items, DebloatCategory.Unclassified);
            ProvisionedNote = _debloat.ProvisionedError is null
                ? string.Empty
                : "Não foi possível ler os apps provisionados (para novos usuários) neste PC; a opção abaixo não terá efeito.";
            Status = $"{SafeItems.Count} seguros, {OptionalItems.Count} opcionais, {ProtectedItems.Count} protegidos e {UnclassifiedItems.Count} não classificados.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao listar apps", ex);
            Status = $"Não foi possível listar os apps: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Fill(ObservableCollection<DebloatItemViewModel> target, IEnumerable<DebloatItem> items, DebloatCategory category)
    {
        target.Clear();
        foreach (var i in items.Where(i => i.Category == category)) target.Add(new DebloatItemViewModel(i));
    }

    [RelayCommand]
    private void ApplyPreset(string? preset)
    {
        if (!Enum.TryParse<DebloatPreset>(preset, out var p)) return;
        if (p == DebloatPreset.Agressivo && !_dialogs.Confirm("Predefinição Agressiva (avançado)",
                "A predefinição Agressiva marca também apps OPCIONAIS, como Xbox, Game Bar, Phone Link, Teams e Widgets.\n\n" +
                "Alguns deles são usados por jogos (Game Pass) e por recursos do Windows. Revise a lista antes de remover. Marcar mesmo assim?"))
            return;
        var selected = DebloatRules.PresetSelection(p, Removable.Select(v => v.Item)).ToHashSet();
        foreach (var vm in Removable) vm.IsSelected = selected.Contains(vm.Item);
        Status = $"Predefinição {p}: {selected.Count} app(s) marcados. Nada foi removido ainda.";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var vm in Removable) vm.IsSelected = false;
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (IsBusy) return;
        var plan = DebloatRules.BuildPlan(Removable.Where(v => v.IsSelected).Select(v => v.Item), IncludeProvisioned);
        if (plan.ToRemove.Count == 0)
        {
            _dialogs.Info("Debloat", "Marque ao menos um app (só itens Seguros ou Opcionais podem ser removidos).");
            return;
        }

        string list = string.Join("\n", plan.ToRemove.Select(i => $"• {i.DisplayName} ({i.CategoryText})"));
        if (!_dialogs.Confirm("Remover apps",
                $"Serão removidos {plan.ToRemove.Count} app(s):\n\n{list}\n\n" +
                (plan.IncludeProvisioned ? "Também serão removidos para NOVOS usuários deste PC.\n\n" : string.Empty) +
                "Um ponto de restauração será criado antes. Atenção: remover apps não é 100% reversível; o desfazer reinstala pela Microsoft Store. Continuar?"))
            return;
        if (plan.HasOptional && !_dialogs.Confirm("Confirmação adicional",
                "A seleção inclui apps OPCIONAIS (podem ser usados por jogos, pelo Game Pass ou por recursos do Windows).\n\nTem certeza de que quer removê-los?"))
            return;

        IsBusy = true;
        Report = string.Empty;
        try
        {
            Status = "Criando ponto de restauração...";
            var rp = await _restorePoints.CreateAsync("Gueibs Optimizer - antes do debloat");
            if (!rp.Success && !_dialogs.Confirm("Ponto de restauração", $"{rp.Message}\n\nContinuar sem ponto de restauração?"))
            {
                Status = "Remoção cancelada: nada foi alterado.";
                return;
            }

            var progress = new Progress<string>(s => Status = s);
            var results = await Task.Run(() => _debloat.RemoveAsync(plan, progress));
            foreach (var r in results)
            {
                var vm = Removable.FirstOrDefault(v => v.Item == r.Item);
                if (vm is not null) vm.Status = r.Message;
            }
            int ok = results.Count(r => r.Success), fail = results.Count - ok;
            Report = $"{ok} removido(s), {fail} falha(s)." +
                     (fail > 0 ? "\n" + string.Join("\n", results.Where(r => !r.Success).Select(r => $"• {r.Item.DisplayName}: {r.Message}")) : string.Empty) +
                     "\nPara reinstalar, use a aba Restauração.";
            Status = "Concluído.";
        }
        catch (Exception ex)
        {
            _logger.Error("Erro no debloat", ex);
            Status = $"Erro: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
        await LoadAsync();
    }

    public async Task RefreshExtrasAsync()
    {
        var result = await _score.EvaluateAsync(Extras.Select(e => e.Tweak));
        foreach (var state in result.States)
            Extras.FirstOrDefault(e => e.Tweak.Id == state.Tweak.Id)?.Update(state);
    }
}
