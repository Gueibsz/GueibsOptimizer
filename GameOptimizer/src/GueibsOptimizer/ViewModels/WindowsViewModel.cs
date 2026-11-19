using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

public sealed partial class CleanupOptionViewModel(CleanupCategory category) : ObservableObject
{
    public CleanupCategory Category { get; } = category;
    public string Name => Category.Name;
    public string Description => Category.Description;

    [ObservableProperty] private bool _isSelected = category.DefaultSelected;
    [ObservableProperty] private string _sizeText = "—";
    public long Bytes { get; set; }
}

public sealed partial class WindowsViewModel : ObservableObject
{
    private readonly CleanupService _cleanup;
    private readonly StartupService _startup;
    private readonly RepairService _repair;
    private readonly WeeklyReviewService _weekly;
    private readonly IDialogService _dialogs;
    private readonly Logger _logger;
    private bool _loaded;

    private readonly DefenderService _defender;

    public WindowsViewModel(CleanupService cleanup, StartupService startup, RepairService repair,
        WeeklyReviewService weekly, DefenderService defender, IDialogService dialogs, Logger logger)
    {
        _defender = defender;
        _cleanup = cleanup;
        _startup = startup;
        _repair = repair;
        _weekly = weekly;
        _dialogs = dialogs;
        _logger = logger;
        foreach (var c in CleanupService.Categories()) CleanupOptions.Add(new CleanupOptionViewModel(c));
    }

    public ObservableCollection<CleanupOptionViewModel> CleanupOptions { get; } = [];
    public ObservableCollection<StartupItem> StartupItems { get; } = [];

    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _cleanupStatus = "Clique em Analisar para calcular o espaço a liberar.";
    [ObservableProperty] private bool _isLoadingStartup;
    [ObservableProperty] private string _startupStatus = string.Empty;
    [ObservableProperty] private bool _isWeeklyEnabled;
    [ObservableProperty] private bool _isWeeklyBusy;

    /// <summary>Carrega listas na primeira vez que a página é aberta.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshWeeklyAsync();
        await RefreshDefenderAsync();
        await RefreshStartupAsync();
    }

    [ObservableProperty] private string _defenderStatus = "Verificando...";
    [ObservableProperty] private bool _hasDefenderExclusion;

    public string AppFolder => DefenderService.AppFolder;

    [RelayCommand]
    private async Task RefreshDefenderAsync()
    {
        var s = await _defender.GetStatusAsync();
        DefenderStatus = s.Message;
        HasDefenderExclusion = s.ExclusionPresent;
    }

    [RelayCommand]
    private async Task AddDefenderExclusionAsync()
    {
        if (!_dialogs.Confirm("Exclusão do Defender",
                $"Adicionar SOMENTE a pasta do Gueibs Optimizer às exclusões do Microsoft Defender?\n\n{AppFolder}\n\nIsso evita que o antivírus atrase ou bloqueie as leituras de sensores e as ações do app. A proteção do Windows continua ligada para todo o resto. Pode ser desfeito aqui ou na aba Restauração."))
            return;
        var error = await _defender.AddExclusionAsync();
        if (error is not null) _dialogs.Error("Defender", error);
        await RefreshDefenderAsync();
        WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
    }

    [RelayCommand]
    private async Task RemoveDefenderExclusionAsync()
    {
        var error = await _defender.RemoveExclusionAsync();
        if (error is not null) _dialogs.Error("Defender", error);
        await RefreshDefenderAsync();
        WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsCleaning) return;
        IsCleaning = true;
        CleanupStatus = "Calculando...";
        try
        {
            var estimates = await _cleanup.EstimateAsync(CleanupOptions.Select(o => o.Category));
            foreach (var e in estimates)
            {
                var option = CleanupOptions.First(o => o.Category.Kind == e.Category.Kind);
                option.Bytes = e.Bytes;
                option.SizeText = $"{MemoryCleanResult.FormatBytes((ulong)e.Bytes)} ({e.Files} itens)";
            }
            long selected = CleanupOptions.Where(o => o.IsSelected).Sum(o => o.Bytes);
            CleanupStatus = $"Selecionado: {MemoryCleanResult.FormatBytes((ulong)selected)} a liberar.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao analisar a limpeza", ex);
            CleanupStatus = "Não foi possível calcular o tamanho.";
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsCleaning) return;
        var selected = CleanupOptions.Where(o => o.IsSelected).ToList();
        if (selected.Count == 0) { _dialogs.Info("Limpeza", "Selecione ao menos uma categoria."); return; }

        await AnalyzeAsync();
        long total = selected.Sum(o => o.Bytes);
        string list = string.Join("\n", selected.Select(o => $"• {o.Name}: {o.SizeText}"));
        if (!_dialogs.Confirm("Confirmar limpeza",
                $"Serão apagados aproximadamente {MemoryCleanResult.FormatBytes((ulong)total)}:\n\n{list}\n\nArquivos em uso serão ignorados. Continuar?"))
            return;

        IsCleaning = true;
        CleanupStatus = "Limpando...";
        try
        {
            var r = await _cleanup.CleanAsync(selected.Select(o => o.Category));
            CleanupStatus = r.Simulated
                ? $"Simulação: {MemoryCleanResult.FormatBytes((ulong)r.FreedBytes)} seriam liberados. Nada foi apagado."
                : $"Liberados {MemoryCleanResult.FormatBytes((ulong)r.FreedBytes)} ({r.DeletedFiles} arquivos). {r.SkippedFiles} em uso foram ignorados.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha na limpeza", ex);
            CleanupStatus = $"A limpeza foi interrompida: {ex.Message}";
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private async Task RefreshStartupAsync()
    {
        IsLoadingStartup = true;
        StartupStatus = "Carregando...";
        try
        {
            var items = await _startup.ListAsync();
            StartupItems.Clear();
            foreach (var i in items) StartupItems.Add(i);
            StartupStatus = $"{items.Count} itens, {items.Count(i => !i.Enabled)} desativados.";
        }
        catch (Exception ex)
        {
            _logger.Error("Falha ao listar a inicialização", ex);
            StartupStatus = "Não foi possível listar os programas de inicialização.";
        }
        finally
        {
            IsLoadingStartup = false;
        }
    }

    [RelayCommand]
    private async Task ToggleStartupAsync(StartupItem? item)
    {
        if (item is null) return;
        try
        {
            if (item.Enabled) await Task.Run(() => _startup.DisableAsync(item));
            else await Task.Run(() => _startup.EnableAsync(item));
        }
        catch (Exception ex)
        {
            _logger.Error($"Falha ao alterar {item.Name}", ex);
            _dialogs.Error("Inicialização", $"Não foi possível alterar \"{item.Name}\": {ex.Message}");
        }
        await RefreshStartupAsync();
        WeakReferenceMessenger.Default.Send(new TweaksChangedMessage());
    }

    [RelayCommand]
    private void Repair()
    {
        if (!_dialogs.Confirm("Reparar o Windows",
                "Será aberta uma janela de console que executa:\n\n1. DISM /Online /Cleanup-Image /RestoreHealth (usa a internet, 5 a 20 min)\n2. sfc /scannow\n\nNão feche a janela até terminar. Continuar?"))
            return;
        if (!_repair.Start()) _dialogs.Error("Reparo", "Não foi possível abrir o console de reparo.");
    }

    private bool _refreshingWeekly;

    /// <summary>Reage à chave (clique, teclado ou leitor de tela); depois relê o estado real da tarefa.</summary>
    partial void OnIsWeeklyEnabledChanged(bool value)
    {
        if (!_refreshingWeekly) _ = SetWeeklyAsync(value);
    }

    private async Task SetWeeklyAsync(bool want)
    {
        if (IsWeeklyBusy) return;
        IsWeeklyBusy = true;
        try
        {
            bool ok = want ? await _weekly.EnableAsync() : await _weekly.DisableAsync();
            if (!ok) _dialogs.Error("Revisão semanal", "Não foi possível alterar a tarefa agendada. Veja o log em Atividade.");
            await RefreshWeeklyAsync();
        }
        finally
        {
            IsWeeklyBusy = false;
        }
    }

    private async Task RefreshWeeklyAsync()
    {
        bool real = await _weekly.IsEnabledAsync();
        _refreshingWeekly = true;
        IsWeeklyEnabled = real;
        _refreshingWeekly = false;
    }
}
