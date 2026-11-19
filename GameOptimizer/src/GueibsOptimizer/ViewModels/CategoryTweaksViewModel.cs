using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.ViewModels;

/// <summary>
/// Página de uma categoria (Windows, Mouse, Teclado, GPU, Desempenho): só os ITweaks que passam no filtro,
/// vindos do mesmo TweakRegistry usado pelo Boost, Perfis e Pontuação (sem duplicar lógica).
/// </summary>
public sealed partial class CategoryTweaksViewModel : ObservableObject
{
    private readonly TweakRegistry _registry;
    private readonly ScoreService _score;

    public CategoryTweaksViewModel(string title, string subtitle, string infoText, TweakRegistry registry, ScoreService score,
        IDialogService dialogs, Logger logger, Func<ITweak, bool> filter)
    {
        Title = title;
        Subtitle = subtitle;
        InfoText = infoText;
        _registry = registry;
        _score = score;
        Items = registry.All.Where(filter).Select(t => new TweakItemViewModel(t, dialogs, logger, registry.PointsOf(t))).ToList();
        WeakReferenceMessenger.Default.Register<CategoryTweaksViewModel, TweaksChangedMessage>(this, static (vm, msg) => _ = vm.RefreshAsync());
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string InfoText { get; }
    public IReadOnlyList<TweakItemViewModel> Items { get; }
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Filtros usados pelo menu (públicos para os testes).</summary>
    public static bool IsMouse(ITweak t) => t.Category == TweakCategory.Input && t.Id.StartsWith("input.mouse", StringComparison.Ordinal);
    public static bool IsKeyboard(ITweak t) => t.Category == TweakCategory.Input && !IsMouse(t);
    public static bool IsGpu(ITweak t) => t.Category == TweakCategory.Gpu;
    public static bool IsWindows(ITweak t) => t.Category == TweakCategory.Windows && !PreferencesViewModel.IsPreference(t) && !DnsViewModel.IsNetwork(t);
    public static bool IsPerformance(ITweak t) => t.Category is TweakCategory.Power or TweakCategory.Gaming;
    public static bool IsPrivacy(ITweak t) => t.Category == TweakCategory.Privacy;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var result = await _score.EvaluateAsync(_registry.All);
        foreach (var state in result.States)
            Items.FirstOrDefault(i => i.Tweak.Id == state.Tweak.Id)?.Update(state);
        int applicable = Items.Count(i => i.IsApplicable);
        Summary = $"{Items.Count(i => i.IsApplied)} de {applicable} ajustes desta página aplicados";
    }
}
