using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Views;

namespace GueibsOptimizer.Tests;

public class IconVariantTests
{
    private static string Repo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GueibsOptimizer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    /// <summary>Lê o diretório de um .ico: tamanhos declarados (0 no arquivo = 256).</summary>
    private static IReadOnlyList<int> IcoSizes(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.Equal(0, BitConverter.ToUInt16(b, 0));
        Assert.Equal(1, BitConverter.ToUInt16(b, 2)); // tipo 1 = ícone
        int count = BitConverter.ToUInt16(b, 4);
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int w = b[6 + 16 * i];
            int size = (int)BitConverter.ToUInt32(b, 6 + 16 * i + 8);
            int offset = (int)BitConverter.ToUInt32(b, 6 + 16 * i + 12);
            Assert.True(offset + size <= b.Length, $"{Path.GetFileName(path)}: entrada {i} fora do arquivo");
            sizes.Add(w == 0 ? 256 : w);
        }
        return sizes;
    }

    [Fact]
    public void EveryPaletteColorHasAMultiResolutionIcon()
    {
        Assert.Equal(ThemeService.Palette.Count, IconService.AssetSlugs.Count);
        foreach (var option in ThemeService.Palette)
        {
            var slug = IconService.AssetSlugFor(option.Hex);
            Assert.NotNull(slug);
            var file = Repo("src", "GueibsOptimizer", "Assets", "Icons", $"icon-{slug}.ico");
            Assert.True(File.Exists(file), file);
            Assert.Equal([16, 32, 48, 256], IcoSizes(file));
        }
    }

    [Fact]
    public void ExeIconStillHasAllSizes() =>
        Assert.Equal([16, 24, 32, 48, 64, 128, 256], IcoSizes(Repo("src", "GueibsOptimizer", "Resources", "app.ico")));

    [Fact]
    public void CustomColorsHaveNoAssetAndAreRenderedAtRuntime()
    {
        Assert.Null(IconService.AssetSlugFor("#123456"));
        Assert.Equal("roxo", IconService.AssetSlugFor("a855f7"));
        ColorMath.TryParseHex("#123456", out var c);
        using var icon = IconRenderer.CreateIcon(c, 32);
        Assert.Equal(32, icon.Width);
        Assert.Equal(32, icon.Height);
    }

    [Fact]
    public void OldDefaultAccentMigratesUnlessChosenByUser()
    {
        var s = new AppSettings { AccentColor = "#22d3ee" };
        SettingsService.Sanitize(s);
        Assert.Equal("#C52613", s.AccentColor);
        s = new AppSettings { AccentColor = "#22D3EE", AccentChosenByUser = true };
        SettingsService.Sanitize(s);
        Assert.Equal("#22D3EE", s.AccentColor);
        Assert.Equal("#C52613", new AppSettings().AccentColor);
    }

    [Fact]
    public void SettingsScreenHasNoIconColorOption()
    {
        var xaml = File.ReadAllText(Repo("src", "GueibsOptimizer", "Views", "SettingsView.xaml"));
        Assert.DoesNotContain("ChooseIconColor", xaml);
        Assert.DoesNotContain("IconSwatches", xaml);
    }

    [Fact]
    public void InstallerHasNoIconColorChoiceAndUsesExeIcon()
    {
        var iss = File.ReadAllText(Repo("installer", "GueibsOptimizer.iss"));
        Assert.DoesNotContain("icon-ciano", iss);
        Assert.DoesNotContain("Cor do ícone", iss);
        Assert.Contains(@"SetupIconFile=..\src\GueibsOptimizer\Resources\app.ico", iss);
    }
}
