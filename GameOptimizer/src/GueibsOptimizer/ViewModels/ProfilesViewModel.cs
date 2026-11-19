using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class ProfilesViewModel : ObservableObject
{
    private const string Filter = "Perfil do Gueibs Optimizer (*.gopro)|*.gopro";

    private readonly ProfileService _profiles;
    private readonly TweakRegistry _registry;
    private readonly ScoreService _score;
    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;

    public ProfilesViewModel(ProfileService profiles, TweakRegistry registry, ScoreService score,
        IRestorePointService restorePoints, IDialogService dialogs, Logger logger)
    {
        _profiles = profiles;
        _registry = registry;
        _score = score;
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _logger = logger;
        Refresh();
    }

    public ObservableCollection<Profile> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTweakNames), nameof(CanModifySelected))]
    private Profile? _selected;

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newDescription = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    public bool CanModifySelected => Selected is { IsBuiltIn: false };

    public IReadOnlyList<string> SelectedTweakNames =>
        Selected?.Tweaks.Select(id => _registry.Find(id)?.Name ?? id).ToList() ?? [];

    [RelayCommand]
    private void Refresh()
    {
        var keep = Selected?.Name;
        Profiles.Clear();
        foreach (var p in _profiles.List()) Profiles.Add(p);
        Selected = Profiles.FirstOrDefault(p => p.Name == keep) ?? Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Selected is not { } profile || IsBusy) return;
        IsBusy = true;
        try
        {
            var states = await _score.EvaluateAsync(_registry.All);
            var plan = ProfileService.Plan(profile, states.States);
            if (plan.ToApply.Count + plan.ToRevert.Count == 0)
            {
                _dialogs.Info("Perfis", plan.Describe());
                return;
            }
            if (!_dialogs.Confirm($"Aplicar perfil \"{profile.Name}\"",
                    $"{plan.Describe()}\n\nUm ponto de restauração será criado antes. Continuar?"))
                return;

            Status = "Criando ponto de restauração...";
            var rp = await _restorePoints.CreateAsync($"Gueibs Optimizer - perfil {profile.Name}");
            if (!rp.Success && !_dialogs.Confirm("Ponto de restauração", $"{rp.Message}\n\nContinuar mesmo assim?"))
                return;

            var progress = new Progress<string>(s => Status = s);
            var errors = await Task.Run(() => ProfileService.ExecuteAsync(plan, _logger, progress));
            Status = errors.Count == 0 ? $"Perfil \"{profile.Name}\" aplicado." : $"Perfil aplicado com {errors.Count} falha(s).";
            if (errors.Count > 0) _dialogs.Error("Perfis", "Alguns ajustes falharam:\n\n" + string.Join("\n", errors));
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao aplicar perfil", ex);
            _dialogs.Error("Perfis", $"Não foi possível aplicar o perfil: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
    }

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            _dialogs.Info("Perfis", "Digite um nome para o novo perfil.");
            return;
        }
        try
        {
            var states = await _score.EvaluateAsync(_registry.All);
            var p = _profiles.SaveCurrent(NewName, NewDescription, states.States);
            NewName = NewDescription = string.Empty;
            Refresh();
            Selected = Profiles.FirstOrDefault(x => x.FilePath == p.FilePath);
            Status = $"Perfil \"{p.Name}\" salvo com {p.Tweaks.Count} ajustes.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Perfis", ex.Message);
        }
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } p) return;
        try
        {
            var copy = _profiles.Duplicate(p);
            Refresh();
            Selected = Profiles.FirstOrDefault(x => x.FilePath == copy.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.Error("Perfis", ex.Message);
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { IsBuiltIn: false } p) return;
        if (!_dialogs.Confirm("Excluir perfil", $"Excluir o perfil \"{p.Name}\"? Os ajustes aplicados não são alterados.")) return;
        try
        {
            _profiles.Delete(p);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.Error("Perfis", ex.Message);
        }
    }

    [RelayCommand]
    private void Export()
    {
        if (Selected is not { } p) return;
        var path = _dialogs.PickSaveFile(Filter, p.Name + ProfileService.Extension);
        if (path is null) return;
        try
        {
            _profiles.Export(p, path);
            Status = $"Exportado para {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Perfis", $"Não foi possível exportar: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.PickOpenFile(Filter);
        if (path is null) return;

        var validation = _profiles.Validate(path);
        if (!validation.IsValid)
        {
            _dialogs.Error("Importar perfil", "O arquivo foi recusado:\n\n" + string.Join("\n", validation.Errors));
            return;
        }

        var p = validation.Profile!;
        var names = p.Tweaks.Select(id => $"  • {_registry.Find(id)!.Name}");
        string ignored = validation.IgnoredIds.Count == 0 ? string.Empty
            : $"\n\nIds desconhecidos ignorados ({validation.IgnoredIds.Count}): {string.Join(", ", validation.IgnoredIds.Take(10))}";
        if (!_dialogs.Confirm("Importar perfil",
                $"Perfil \"{p.Name}\"\n{p.Description}\n\nAjustes ({p.Tweaks.Count}):\n{string.Join("\n", names)}{ignored}\n\nO perfil será adicionado à lista (nada é aplicado agora). Importar?"))
            return;

        try
        {
            var saved = _profiles.Import(validation);
            Refresh();
            Selected = Profiles.FirstOrDefault(x => x.FilePath == saved.FilePath);
            Status = $"Perfil \"{saved.Name}\" importado.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _dialogs.Error("Importar perfil", ex.Message);
        }
    }
}
