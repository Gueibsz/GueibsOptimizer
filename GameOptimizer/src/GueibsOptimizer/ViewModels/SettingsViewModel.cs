using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;
using GueibsOptimizer.Views;

namespace GueibsOptimizer.ViewModels;

public sealed partial class ThemeOption(AppTheme value, string label, string description) : ObservableObject
{
    public AppTheme Value { get; } = value;
    public string Label { get; } = label;
    public string Description { get; } = description;

    [ObservableProperty] private bool _isSelected;

    /// <summary>Disparado quando a opção é marcada (clique, teclado ou automação de UI).</summary>
    public event Action<ThemeOption>? Chosen;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) Chosen?.Invoke(this);
    }
}

public sealed partial class AccentSwatch(string name, string hex) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public Brush Brush { get; } = CreateBrush(hex);

    /// <summary>Cor do "✓" sobre a bolinha: preto ou branco, o que for legível nessa cor.</summary>
    public Brush CheckBrush { get; } = CreateBrush(ColorMath.ToHex(ColorMath.BestTextOn(Parse(hex))));

    private static Color Parse(string hex)
    {
        ColorMath.TryParseHex(hex, out var c);
        return c;
    }

    [ObservableProperty] private bool _isSelected;

    private static Brush CreateBrush(string hex)
    {
        ColorMath.TryParseHex(hex, out var c);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Tela Configurações: aparência (tema e cor de destaque). Tudo é aplicado na hora e salvo em settings.json.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private bool _loading;

    private readonly IconService? _icons;

    public SettingsViewModel(SettingsService settings, ThemeService theme, IconService? icons = null)
    {
        _settings = settings;
        _theme = theme;
        _icons = icons;
        _loading = true;
        foreach (var o in ThemeService.Palette)
            Swatches.Add(new AccentSwatch(o.Name, o.Hex));
        foreach (var t in ThemeOptions) t.Chosen += option => SelectedTheme = option;
        SelectedTheme = ThemeOptions.First(t => t.Value == settings.Current.Theme);
        _customHex = settings.Current.AccentColor;
        _displayName = settings.Current.DisplayName ?? string.Empty;
        _loading = false;
        MarkSelected();
        _theme.Applied += UpdateContrast;
        UpdateContrast();
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(AppTheme.Dark, "Escuro", "Fundo grafite, ideal para jogar à noite."),
        new(AppTheme.Light, "Claro", "Fundo claro, melhor em ambientes iluminados."),
        new(AppTheme.System, "Seguir o Windows", "Usa o modo de cor dos apps do Windows e muda junto com ele.")
    ];

    public ObservableCollection<AccentSwatch> Swatches { get; } = [];

    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private string _customHex;
    [ObservableProperty] private string _hexError = string.Empty;
    [ObservableProperty] private string _contrastText = string.Empty;

    public string CurrentAccent => _settings.Current.AccentColor;

    // --- Idioma ---
    public sealed record LanguageOption(string Code, string Name);

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        LocalizationService.Languages.Select(l => new LanguageOption(l, LocalizationService.DisplayName(l))).ToList();

    /// <summary>Ponto de restauração antes de mudanças (opcional; padrão ligado).</summary>
    public bool CreateRestorePoints
    {
        get => _settings.Current.CreateRestorePoints;
        set
        {
            if (_settings.Current.CreateRestorePoints == value) return;
            _settings.Current.CreateRestorePoints = value;
            _settings.Save();
            OnPropertyChanged();
            RestorePointOptionChanged?.Invoke();
        }
    }

    public void RefreshRestoreOption() => OnPropertyChanged(nameof(CreateRestorePoints));

    /// <summary>Avisa o Painel para atualizar a caixa do Boost.</summary>
    public event Action? RestorePointOptionChanged;

    [ObservableProperty] private string _selectedLanguage = LocalizationService.Instance.Language;

    /// <summary>Troca ao vivo e salva em settings.json.</summary>
    partial void OnSelectedLanguageChanged(string value)
    {
        var lang = LocalizationService.Normalize(value);
        LocalizationService.Instance.SetLanguage(lang);
        _settings.Current.Language = lang;
        _settings.Save();
    }

    // --- Saudação do Painel ---
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _displayNameStatus = string.Empty;

    /// <summary>Disparado ao salvar o nome (o Painel atualiza a saudação na hora).</summary>
    public event Action? DisplayNameChanged;

    [RelayCommand]
    private void SaveDisplayName()
    {
        var name = DisplayName.Trim();
        _settings.Current.DisplayName = name.Length == 0 ? null : name;
        _settings.Save();
        DisplayNameStatus = name.Length == 0 ? $"Usando o nome do Windows ({Environment.UserName})." : "Salvo.";
        DisplayNameChanged?.Invoke();
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        foreach (var t in ThemeOptions) t.IsSelected = ReferenceEquals(t, value);
        if (_loading || value is null) return;
        _settings.Current.Theme = value.Value;
        ApplyAndSave();
    }

    [RelayCommand]
    private void ChooseAccent(AccentSwatch? swatch)
    {
        if (swatch is null) return;
        _settings.Current.AccentChosenByUser = true;
        SetAccent(swatch.Hex);
    }

    [RelayCommand]
    private void ApplyCustomHex()
    {
        var normalized = SettingsService.NormalizeAccent(CustomHex);
        if (normalized is null)
        {
            HexError = "Cor inválida. Use o formato #RRGGBB (ex.: #C52613) ou #RGB.";
            return;
        }
        _settings.Current.AccentChosenByUser = true;
        SetAccent(normalized);
    }

    [RelayCommand]
    private void ResetAppearance()
    {
        _loading = true;
        SelectedTheme = ThemeOptions[0];
        _loading = false;
        _settings.Current.Theme = AppTheme.Dark;
        _settings.Current.AccentChosenByUser = false;
        SetAccent(SettingsService.DefaultAccent);
    }

    private void SetAccent(string hex)
    {
        HexError = string.Empty;
        _settings.Current.AccentColor = SettingsService.NormalizeAccent(hex) ?? SettingsService.DefaultAccent;
        CustomHex = _settings.Current.AccentColor;
        ApplyAndSave();
        MarkSelected();
        OnPropertyChanged(nameof(CurrentAccent));
    }

    private void ApplyAndSave()
    {
        _theme.Apply(_settings.Current.Theme, _settings.Current.AccentColor);
        // O ícone (janela, barra de tarefas, bandeja) sempre acompanha a cor de destaque.
        _icons?.Apply(_settings.Current.AccentColor);
        _settings.Save();
    }

    private void MarkSelected()
    {
        foreach (var s in Swatches)
            s.IsSelected = string.Equals(s.Hex, _settings.Current.AccentColor, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateContrast()
    {
        if (_theme.CurrentAccent is not { } p) return;
        double text = p.TextContrast(_theme.Background);
        bool adjusted = p.AccentText != p.Accent;
        ContrastText = string.Format(CultureInfo.CurrentCulture,
            "Contraste do texto em destaque sobre o fundo: {0:0.0}:1 · texto sobre a cor de destaque: {1:0.0}:1 (mínimo recomendado 4,5:1).{2}",
            text, p.OnAccentContrast,
            adjusted ? " Para continuar legível neste tema, os textos usam um tom ajustado da cor escolhida." : string.Empty);
    }
}
