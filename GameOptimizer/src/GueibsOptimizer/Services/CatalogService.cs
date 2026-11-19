using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>Leitura do catálogo embutido (Data/catalog.json), com validação de cada item.</summary>
public static partial class CatalogParser
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]{1,127}$")]
    private static partial Regex WingetIdPattern();

    public static readonly string[] Categories =
    [
        "Navegadores", "Jogos e Launchers", "Comunicação", "Utilitários", "Monitoramento",
        "Drivers e Runtimes", "Mídia", "Desenvolvimento", "Segurança", "Produtividade"
    ];

    /// <summary>Ids válidos para passar ao winget (sem espaços, aspas ou caracteres de shell).</summary>
    public static bool IsValidWingetId(string id) => WingetIdPattern().IsMatch(id);

    public static IReadOnlyList<AppItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Catálogo sem a lista \"apps\".");

        var result = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in apps.EnumerateArray())
        {
            string? Get(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
            var name = Get("name");
            var id = Get("wingetId");
            var category = Get("category");
            var description = Get("description") ?? string.Empty;
            if (string.IsNullOrEmpty(name) || id is null || !IsValidWingetId(id) || string.IsNullOrEmpty(category)) continue;
            if (!seen.Add(id)) continue;
            result.Add(new AppItem(name, id, category, description));
        }
        return result;
    }
}

public static class CatalogService
{
    private static readonly Lazy<IReadOnlyList<AppItem>> Cached = new(Load);

    public static IReadOnlyList<AppItem> Apps => Cached.Value;

    private static IReadOnlyList<AppItem> Load()
    {
        var asm = typeof(CatalogService).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return CatalogParser.Parse(reader.ReadToEnd());
    }

    /// <summary>Seleção sugerida para um PC de jogos recém-formatado.</summary>
    public static readonly string[] GamingEssentials =
    [
        "Valve.Steam", "Discord.Discord", "7zip.7zip", "VideoLAN.VLC", "Google.Chrome",
        "Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86", "Microsoft.DirectX",
        "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.EdgeWebView2Runtime"
    ];
}
