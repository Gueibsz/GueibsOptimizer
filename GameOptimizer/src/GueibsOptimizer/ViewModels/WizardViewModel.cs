using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>
/// Formatação guiada: (1) perfil e apps, (2) ponto de restauração, (3) instalar, (4) otimizar e privacidade,
/// (5) resumo e reinício. O estado é salvo a cada passo e pode ser retomado.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly TweakRegistry _registry;
    private readonly ScoreService _score;
    private readonly IRestorePointService _restorePoints;
    private readonly Shell _shell;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;
    private WizardState? _state;

    public WizardViewModel(ProfileService profiles, TweakRegistry registry, ScoreService score,
        IRestorePointService restorePoints, Shell shell, IDialogService dialogs, Logger logger)
    {
        _profiles = profiles;
        _registry = registry;
        _score = score;
        _restorePoints = restorePoints;
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;
        ProfileNames = profiles.List().Select(p => p.Name).ToList();
        _selectedProfile = ProfileNames.FirstOrDefault() ?? "Competitivo";
        _state = WizardState.Load();
        if (_state is { Finished: false }) CanResume = true;
        UpdateHeader();
    }

    /// <summary>Catálogo compartilhado (seleção de apps e fila de instalação).</summary>
    public InstallAppsViewModel? Catalog { get; set; }

    public IReadOnlyList<string> ProfileNames { get; }

    // --- 8.10 ---
    /// <summary>Usado no passo de Debloat; sem ele o Debloat é pulado.</summary>
    public DebloatService? Debloat { get; set; }
    public IReadOnlyList<string> AppPresetNames { get; } = WizardPresets.AppPresets.Keys.ToList();
    public IReadOnlyList<string> DebloatChoices { get; } = WizardPresets.DebloatChoices;
    [ObservableProperty] private string _selectedDebloat = "Leve";
    [ObservableProperty] private bool _installRuntimes = true;
    [ObservableProperty] private bool _applyPowerPlan = true;
    [ObservableProperty] private string _presetStatus = string.Empty;

    /// <summary>Marca no Catálogo os programas de uma predefinição (soma à seleção atual).</summary>
    [RelayCommand]
    private void ApplyAppPreset(string? name)
    {
        if (Catalog is null || name is null || !WizardPresets.AppPresets.TryGetValue(name, out var ids)) return;
        int marked = 0;
        foreach (var a in Catalog.Apps.Where(a => ids.Contains(a.WingetId, StringComparer.OrdinalIgnoreCase)))
        {
            if (!a.IsSelected) marked++;
            a.IsSelected = true;
        }
        PresetStatus = $"{Catalog.Apps.Count(a => a.IsSelected)} programa(s) selecionado(s) ({marked} da predefinição {name}).";
    }

    /// <summary>Escolha por categoria: abre a aba Catálogo, onde há o filtro de categorias.</summary>
    [RelayCommand]
    private void OpenCatalog()
    {
        if (Catalog is not null) Catalog.SelectedTab = 0;
    }
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private string _selectedProfile;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string _header = string.Empty;

    private void UpdateHeader() => Header = _state is null
        ? "Pronto para começar. Escolha o perfil e marque os programas na aba Catálogo."
        : _state.Finished ? "Formatação guiada concluída."
        : $"Etapa atual: {_state.StepTitle}";

    private void Add(string line)
    {
        Log.Add(line);
        if (_state is null) return;
        _state.Log.Add(line);
        _state.Save();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || Catalog is null) return;
        var apps = Catalog.Apps.Where(a => a.IsSelected).Select(a => a.WingetId).ToList();
        if (!_dialogs.Confirm("Formatação guiada",
                $"Perfil: {SelectedProfile}\nProgramas: {apps.Count}\nDebloat: {SelectedDebloat}\nRuntimes: {(InstallRuntimes ? "sim" : "não")}\nPlano de energia: {(ApplyPowerPlan ? "sim" : "não")}\n\nO assistente vai criar um ponto de restauração, instalar os programas, aplicar o perfil, a privacidade e o Debloat escolhido, e depois os extras. Se o app for fechado, você pode retomar depois. Começar?"))
            return;

        _state = new WizardState
        {
            ProfileName = SelectedProfile, Apps = apps, Step = WizardState.StepRestorePoint,
            DebloatPreset = SelectedDebloat, InstallRuntimes = InstallRuntimes, ApplyPowerPlan = ApplyPowerPlan
        };
        _state.Save();
        Log.Clear();
        Add($"Iniciado com o perfil \"{SelectedProfile}\" e {apps.Count} programa(s).");
        await RunAsync();
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        _state ??= WizardState.Load();
        if (_state is null || IsRunning) return;
        Log.Clear();
        foreach (var l in _state.Log) Log.Add(l);
        Add($"Retomando em: {_state.StepTitle}");
        await RunAsync();
    }

    [RelayCommand]
    private void Discard()
    {
        if (IsRunning) return;
        WizardState.Delete();
        _state = null;
        CanResume = IsFinished = false;
        Log.Clear();
        UpdateHeader();
    }

    private async Task RunAsync()
    {
        if (_state is null) return;
        IsRunning = true;
        CanResume = false;
        try
        {
            if (_state.Step == WizardState.StepRestorePoint && !await StepRestorePointAsync()) return;
            if (_state.Step == WizardState.StepInstall) await StepInstallAsync();
            if (_state.Step == WizardState.StepOptimize) await StepOptimizeAsync();
            if (_state.Step == WizardState.StepExtras) await StepExtrasAsync();
            if (_state.Step == WizardState.StepSummary) StepSummary();
        }
        catch (Exception ex)
        {
            _logger.Error("Erro na formatação guiada", ex);
            Add($"Erro: {ex.Message}. Você pode retomar depois.");
            CanResume = true;
        }
        finally
        {
            IsRunning = false;
            UpdateHeader();
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
    }

    private void Advance(int step)
    {
        _state!.Step = step;
        _state.Save();
        UpdateHeader();
    }

    private async Task<bool> StepRestorePointAsync()
    {
        Add("Etapa 2: criando ponto de restauração...");
        var rp = await _restorePoints.CreateAsync("Gueibs Optimizer - formatação guiada");
        Add(rp.Message);
        if (!rp.Success && !_dialogs.Confirm("Ponto de restauração", $"{rp.Message}\n\nContinuar sem ponto de restauração?"))
        {
            Add("Pausado pelo usuário. Use Retomar para continuar.");
            CanResume = true;
            return false;
        }
        Advance(WizardState.StepInstall);
        return true;
    }

    private async Task StepInstallAsync()
    {
        var pending = _state!.PendingApps;
        Add($"Etapa 3: instalando {pending.Count} programa(s)...");
        if (pending.Count > 0 && Catalog is not null)
        {
            if (!await Catalog.EnsureWingetAsync())
            {
                Add("winget indisponível: instalação pulada.");
            }
            else
            {
                var results = await Catalog.InstallQueueAsync(pending, r =>
                {
                    if (r.Outcome != InstallOutcome.Failed) _state.InstalledApps.Add(r.Id);
                    Add($"  {r.Id}: {r.OutcomeText}");
                });
                var report = InstallAppsViewModel.BuildReport(results);
                if (report.Length > 0) Add(report.Split('\n')[0]);
            }
        }
        Advance(WizardState.StepOptimize);
    }

    private async Task StepOptimizeAsync()
    {
        Add("Etapa 4: aplicando perfil, privacidade e Debloat...");
        var profile = _profiles.List().FirstOrDefault(p => p.Name == _state!.ProfileName) ?? _profiles.BuiltIn()[0];

        // Perfil + todos os ajustes de privacidade recomendados (debloat), sem duplicar.
        var wanted = profile.Tweaks
            .Concat(_registry.All.Where(t => t.Category == TweakCategory.Privacy && TweakText.IsRecommended(t)).Select(t => t.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var states = await _score.EvaluateAsync(_registry.All);
        var target = new Profile { Name = profile.Name, Tweaks = wanted };
        var plan = ProfileService.Plan(target, states.States);
        // O assistente só adiciona ajustes; não desfaz o que o usuário já tinha.
        var applyOnly = new ProfilePlan(plan.ToApply, [], plan.Unchanged);
        var errors = await Task.Run(() => ProfileService.ExecuteAsync(applyOnly, _logger));
        Add($"  {applyOnly.ToApply.Count} ajuste(s) aplicados, {errors.Count} falha(s).");
        foreach (var e in errors) Add($"  ! {e}");
        await StepDebloatAsync();
        Advance(WizardState.StepExtras);
    }

    private async Task StepDebloatAsync()
    {
        if (_state!.DebloatDone) return;
        var preset = WizardPresets.ParseDebloat(_state.DebloatPreset);
        if (preset is null || Debloat is null)
        {
            Add("  Debloat: não escolhido.");
        }
        else
        {
            var items = await Debloat.ListAsync();
            var plan = DebloatRules.BuildPlan(DebloatRules.PresetSelection(preset.Value, items), includeProvisioned: false);
            Add($"  Debloat ({_state.DebloatPreset}): {plan.ToRemove.Count} app(s) a remover.");
            var results = await Debloat.RemoveAsync(plan);
            foreach (var r in results) Add($"    {r.Item.DisplayName}: {r.Message}");
        }
        _state.DebloatDone = true;
        _state.Save();
    }

    private async Task StepExtrasAsync()
    {
        Add("Etapa 5: extras...");
        if (_state!.InstallRuntimes && Catalog is not null)
        {
            var pending = WizardPresets.Runtimes.Where(r => !_state.InstalledApps.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (pending.Count == 0) Add("  Runtimes: já instalados na etapa 3.");
            else if (await Catalog.EnsureWingetAsync())
            {
                Add($"  Instalando {pending.Count} runtime(s): VC++ 2015-2022, .NET Desktop Runtime 8 e DirectX...");
                await Catalog.InstallQueueAsync(pending, r =>
                {
                    if (r.Outcome != InstallOutcome.Failed) _state.InstalledApps.Add(r.Id);
                    Add($"  {r.Id}: {r.OutcomeText}");
                });
            }
        }
        else Add("  Runtimes: não escolhidos.");

        if (_state.ApplyPowerPlan && _registry.All.FirstOrDefault(t => t.Id == "power.plan") is { } plan)
        {
            try
            {
                if (plan.IsApplied()) Add("  Plano de energia: já estava aplicado.");
                else
                {
                    await Task.Run(plan.ApplyAsync);
                    Add("  Plano de energia de alto desempenho aplicado.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Falha no plano de energia do assistente", ex);
                Add($"  ! Plano de energia: {ex.Message}");
            }
        }
        else if (!_state.ApplyPowerPlan) Add("  Plano de energia: não escolhido.");
        Advance(WizardState.StepSummary);
    }

    private void StepSummary()
    {
        _state!.Finished = true;
        _state.Save();
        IsFinished = true;
        Add("Etapa 6: concluído. Reinicie o PC para que todos os ajustes tenham efeito.");
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (!_dialogs.Confirm("Reiniciar", "Reiniciar o computador em 15 segundos? Salve seus arquivos abertos.")) return;
        var r = await _shell.RunMutatingAsync("Reiniciar o computador", "shutdown.exe", ["/r", "/t", "15", "/c", "Gueibs Optimizer: reinício após a formatação guiada"]);
        Add(r.Simulated ? "Reinício simulado." : r.Success ? "Reinício agendado em 15 s." : "Não foi possível agendar o reinício.");
        if (r.Success && !r.Simulated) WizardState.Delete();
    }
}
