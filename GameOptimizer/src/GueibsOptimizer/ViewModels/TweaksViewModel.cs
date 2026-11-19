using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;

    public TweakItemViewModel(ITweak tweak, IDialogService dialogs, Logger logger, int? points = null)
    {
        Tweak = tweak;
        _points = points ?? tweak.Points;
        _dialogs = dialogs;
        _logger = logger;
        // Troca de idioma: todos os textos do cartão são relidos.
        System.ComponentModel.PropertyChangedEventManager.AddHandler(LocalizationService.Instance,
            (_, _) => OnPropertyChanged(string.Empty), nameof(LocalizationService.Version));
    }

    public ITweak Tweak { get; }
    public string Name => LocalizationService.Tr(Tweak.Name);
    public string Description => LocalizationService.Tr(Tweak.Description);
    public TweakRisk Risk => Tweak.Risk;
    public string RiskText => $"{LocalizationService.Tr("Risco")} {LocalizationService.Tr(TweakText.RiskText(Tweak.Risk))}";
    private readonly int _points;
    /// <summary>Pontos normalizados (a soma de todos os ajustes é 100).</summary>
    public string PointsText => $"{_points} {LocalizationService.Tr("pts")}";
    public string? Notice => Tweak.Notice is null ? null : LocalizationService.Tr(Tweak.Notice);

    /// <summary>Seções do botão "i". Lidas pela interface ITweak (o WPF não enxerga membros padrão de interface).</summary>
    public IReadOnlyList<InfoSection> InfoSections => Tweak.Info.Sections;

    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isApplicable = true;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Até o primeiro IsApplied terminar: mostra "Verificando..." e trava os botões (evita "Não aplicado" falso).</summary>
    [ObservableProperty] private bool _isChecking = true;

    public bool CanInteract => !IsBusy && !IsChecking;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanInteract));
    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Aplicado pelo app e depois alterado fora dele (ex.: plano de energia mudado pelo Windows).</summary>
    [ObservableProperty] private bool _isDrifted;

    public string StatusText => LocalizationService.Tr(IsChecking ? "Verificando..." : !IsApplicable ? "Não aplicável" : IsApplied ? "Aplicado" : IsDrifted ? "Desviado" : "Não aplicado");
    public string ApplyText => LocalizationService.Tr(IsDrifted ? "Reaplicar" : "Aplicar");

    partial void OnIsDriftedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ApplyText));
    }

    /// <summary>8.11: motivo quando o ajuste não existe nesta versão do Windows (ex.: "Só existe no Windows 11 · este PC: Windows 10 · build 19045").</summary>
    public string? OsRequirementText => !IsApplicable && OsInfo.RequirementText(Tweak.Id) is { } r
        ? $"{LocalizationService.Tr(r)} · {LocalizationService.Tr("este PC")}: {OsInfo.Current}"
        : null;

    partial void OnIsAppliedChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    partial void OnIsApplicableChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OsRequirementText));
    }

    public void Update(TweakState state)
    {
        IsApplicable = state.Applicable;
        IsApplied = state.Applied;
        IsChecking = false;
    }

    [RelayCommand]
    private Task ApplyAsync()
    {
        // Risco Alto exige confirmação dupla (regra da Fase 8.4).
        if (Tweak.Risk == TweakRisk.High &&
            (!_dialogs.Confirm("Ajuste de risco alto", $"\"{Name}\" tem risco Alto.\n\n{Description}\n\nDeseja aplicar?") ||
             !_dialogs.Confirm("Confirmar de novo", $"Confirma mesmo aplicar \"{Name}\"? Ele pode ser desfeito na aba Restauração.")))
            return Task.CompletedTask;
        return RunAsync(Tweak.ApplyAsync, "aplicar");
    }

    [RelayCommand]
    private Task RevertAsync() => RunAsync(Tweak.RevertAsync, "desfazer");

    private async Task RunAsync(Func<Task> action, string verb)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao {verb} \"{Name}\"", ex);
            _dialogs.Error("Otimizações", $"Não foi possível {verb} \"{Name}\": {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
        }
    }
}

public sealed record TweakGroup(string Title, IReadOnlyList<TweakItemViewModel> Items);

public sealed partial class TweaksViewModel : ObservableObject
{
    private readonly TweakRegistry _registry;
    private readonly ScoreService _scoreService;
    private readonly MemoryCleanerService _memory;
    private readonly GameBoosterService _booster;
    private readonly AppSettings _settings;
    private readonly Logger _logger;
    private readonly List<TweakItemViewModel> _items;

    public TweaksViewModel(TweakRegistry registry, ScoreService scoreService, IDialogService dialogs, Logger logger,
        MemoryCleanerService memory, GameBoosterService booster, AppSettings settings)
    {
        _registry = registry;
        _scoreService = scoreService;
        _memory = memory;
        _booster = booster;
        _settings = settings;
        _logger = logger;
        _isBoosterEnabled = booster.IsRunning;
        _items = registry.All.Select(t => new TweakItemViewModel(t, dialogs, logger, registry.PointsOf(t))).ToList();
        Groups = _items
            .GroupBy(i => i.Tweak.Category)
            .OrderBy(g => g.Key)
            .Select(g => new TweakGroup(TweakText.CategoryText(g.Key), g.ToList()))
            .ToList();

        WeakReferenceMessenger.Default.Register<TweaksViewModel, TweaksChangedMessage>(this,
            static (vm, msg) => _ = vm.RefreshAsync());
    }

    public IReadOnlyList<TweakGroup> Groups { get; }

    [ObservableProperty] private bool _isCleaningRam;
    [ObservableProperty] private string _ramStatus = "Esvazia a memória de trabalho dos processos e mede quanto foi liberado de verdade.";
    [ObservableProperty] private bool _isBoosterEnabled;

    [RelayCommand]
    private async Task CleanRamAsync()
    {
        if (IsCleaningRam) return;
        IsCleaningRam = true;
        RamStatus = "Liberando...";
        try
        {
            var r = await _memory.CleanAsync();
            RamStatus = r.Simulated
                ? $"Simulação: nada foi alterado. Disponível agora: {MemoryCleanResult.FormatBytes(r.AvailableBefore)} de {MemoryCleanResult.FormatBytes(r.Total)}."
                : $"Liberados {MemoryCleanResult.FormatBytes(r.Freed)} ({r.ProcessesTrimmed} processos). Disponível: {MemoryCleanResult.FormatBytes(r.AvailableBefore)} → {MemoryCleanResult.FormatBytes(r.AvailableAfter)}.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao liberar RAM", ex);
            RamStatus = "Não foi possível liberar a RAM.";
        }
        finally
        {
            IsCleaningRam = false;
        }
    }

    partial void OnIsBoosterEnabledChanged(bool value)
    {
        if (value) _booster.Start(); else _booster.Stop();
        _settings.GameBoosterEnabled = value;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenGamesList()
    {
        try
        {
            _booster.LoadList(); // garante que o arquivo exista
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{_booster.ListPath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error("Não foi possível abrir a lista de jogos", ex);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var result = await _scoreService.EvaluateAsync(_registry.All);
        foreach (var state in result.States)
            _items.FirstOrDefault(i => i.Tweak.Id == state.Tweak.Id)?.Update(state);
    }
}
