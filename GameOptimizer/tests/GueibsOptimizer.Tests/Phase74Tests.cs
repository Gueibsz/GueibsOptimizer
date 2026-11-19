using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using GueibsOptimizer.Core;
using GueibsOptimizer.Views;

namespace GueibsOptimizer.Tests;

public class ColorMathTests
{
    [Theory]
    [InlineData("#22D3EE", 0x22, 0xD3, 0xEE)]
    [InlineData("22d3ee", 0x22, 0xD3, 0xEE)]
    [InlineData("#fff", 0xFF, 0xFF, 0xFF)]
    [InlineData("  #3B82F6 ", 0x3B, 0x82, 0xF6)]
    public void ParsesValidHex(string text, byte r, byte g, byte b)
    {
        Assert.True(ColorMath.TryParseHex(text, out var c));
        Assert.Equal(Color.FromRgb(r, g, b), c);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("#AARRGGBB")]
    [InlineData("#FF22D3EE")]
    [InlineData("vermelho")]
    public void RejectsInvalidHex(string? text) => Assert.False(ColorMath.TryParseHex(text, out _));

    [Fact]
    public void ContrastMatchesWcagReference()
    {
        Assert.Equal(21, ColorMath.ContrastRatio(Colors.Black, Colors.White), 1);
        Assert.Equal(1, ColorMath.ContrastRatio(Colors.Red, Colors.Red), 3);
        // #767676 sobre branco é o cinza mínimo que passa 4.5:1.
        Assert.InRange(ColorMath.ContrastRatio(Color.FromRgb(0x76, 0x76, 0x76), Colors.White), 4.5, 4.6);
    }

    [Theory]
    [InlineData("#F59E0B", true)]   // âmbar claro → texto escuro
    [InlineData("#22D3EE", true)]   // ciano → texto escuro
    [InlineData("#1D4ED8", false)]  // azul escuro → texto branco
    [InlineData("#7C3AED", false)]  // roxo → texto branco
    public void PicksReadableTextOnAccent(string hex, bool expectDark)
    {
        ColorMath.TryParseHex(hex, out var c);
        Assert.Equal(expectDark ? ColorMath.NearBlack : ColorMath.NearWhite, ColorMath.BestTextOn(c));
    }

    public static IEnumerable<object[]> PaletteOnBothThemes() =>
        from option in ThemeService.Palette
        from bg in new[] { "#14171C", "#F3F5F8" }
        select new object[] { option.Hex, bg };

    [Theory]
    [MemberData(nameof(PaletteOnBothThemes))]
    public void EveryPaletteColorStaysReadableOnBothThemes(string accentHex, string bgHex)
    {
        ColorMath.TryParseHex(accentHex, out var accent);
        ColorMath.TryParseHex(bgHex, out var bg);
        var p = AccentPalette.From(accent, bg, isDark: bgHex == "#14171C");
        Assert.True(p.TextContrast(bg) >= AccentPalette.MinTextContrast, $"{accentHex} sobre {bgHex}: {p.TextContrast(bg):0.00}");
        Assert.True(p.OnAccentContrast >= 3.0, $"texto sobre {accentHex}: {p.OnAccentContrast:0.00}");
    }

    [Fact]
    public void CustomLowContrastColorIsAdjustedForText()
    {
        ColorMath.TryParseHex("#FFFF66", out var paleYellow);
        var white = Colors.White;
        var p = AccentPalette.From(paleYellow, white, isDark: false);
        Assert.NotEqual(paleYellow, p.AccentText);
        Assert.True(p.TextContrast(white) >= 4.5);
        Assert.Equal(paleYellow, p.Accent); // preenchimento continua com a cor escolhida
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void InvalidValuesFromFileFallBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"Theme":"Light","AccentColor":"#XYZXYZ"}""");
        try
        {
            var s = new SettingsService(path);
            Assert.Equal(AppTheme.Light, s.Current.Theme);
            Assert.Equal(SettingsService.DefaultAccent, s.Current.AccentColor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownThemeValueFallsBackToDark()
    {
        var s = new AppSettings { Theme = (AppTheme)42, AccentColor = "3b82f6" };
        SettingsService.Sanitize(s);
        Assert.Equal(AppTheme.Dark, s.Theme);
        Assert.Equal("#3B82F6", s.AccentColor);
    }

    [Fact]
    public void ThemeAndAccentArePersisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new SettingsService(path);
            s.Current.Theme = AppTheme.System;
            s.Current.AccentColor = "#A855F7";
            s.Save();
            Assert.Contains("\"System\"", File.ReadAllText(path));
            var reloaded = new SettingsService(path);
            Assert.Equal(AppTheme.System, reloaded.Current.Theme);
            Assert.Equal("#A855F7", reloaded.Current.AccentColor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PaletteHasEightToTenValidColors()
    {
        Assert.InRange(ThemeService.Palette.Count, 8, 10);
        Assert.All(ThemeService.Palette, o => Assert.NotNull(SettingsService.NormalizeAccent(o.Hex)));
        Assert.Contains(ThemeService.Palette, o => o.Hex == SettingsService.DefaultAccent);
    }
}

/// <summary>Garante a regra da 7.4: nenhuma cor fixa fora do tema e pincéis sempre via DynamicResource.</summary>
public class XamlThemeRulesTests
{
    private static IEnumerable<string> XamlFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "GueibsOptimizer"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static readonly string[] ThemeFiles = ["Theme.xaml", "Palette.Dark.xaml", "Palette.Light.xaml"];

    [Fact]
    public void NoHardcodedColorsOutsideThemeDictionaries()
    {
        var offenders = XamlFiles()
            .Where(f => !ThemeFiles.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, "\"#[0-9A-Fa-f]{6,8}\"") ||
                        Regex.IsMatch(x.line, "(Fill|Foreground|Background|BorderBrush|Stroke)=\"(Black|White|Red|Gray|Green|Blue)\""))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(offenders.Count == 0, "Cores fixas: " + string.Join(", ", offenders));
    }

    [Fact]
    public void BrushesAreAlwaysDynamicResources()
    {
        var offenders = XamlFiles()
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"StaticResource \w+Brush\}"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(offenders.Count == 0, "StaticResource de pincel: " + string.Join(", ", offenders));
    }

    [Fact]
    public void BothPalettesDefineTheSameKeys()
    {
        static HashSet<string> Keys(string file) =>
            Regex.Matches(File.ReadAllText(XamlFiles().First(f => f.EndsWith(file))), "x:Key=\"(\\w+)\"")
                .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(Keys("Palette.Dark.xaml"), Keys("Palette.Light.xaml"));
    }
}
