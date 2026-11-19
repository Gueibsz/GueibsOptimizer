using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>Leitura do catálogo e regras puras (testadas): classificação, presets e plano de remoção.</summary>
public static class DebloatRules
{
    public static IReadOnlyList<DebloatCatalogEntry> ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Catálogo de debloat sem a lista \"apps\".");
        var list = new List<DebloatCatalogEntry>();
        foreach (var e in apps.EnumerateArray())
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
            var id = S("id");
            if (id.Length == 0 || !Enum.TryParse<DebloatCategory>(S("category"), out var cat) || cat == DebloatCategory.Unclassified) continue;
            list.Add(new DebloatCatalogEntry(id, S("name").Length > 0 ? S("name") : id, cat, S("reason"), cat == DebloatCategory.Protected ? string.Empty : S("storeId")));
        }
        return list;
    }

    /// <summary>Casamento pelo nome do pacote. "*" casa qualquer trecho. Protegido vence se mais de uma regra casar.</summary>
    public static DebloatCatalogEntry? Match(IReadOnlyList<DebloatCatalogEntry> catalog, string packageName)
    {
        var hits = catalog.Where(c => Matches(c.Id, packageName)).ToList();
        return hits.FirstOrDefault(h => h.Category == DebloatCategory.Protected)
               ?? hits.FirstOrDefault(h => !h.Id.Contains('*'))
               ?? hits.FirstOrDefault();
    }

    public static bool Matches(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    /// <summary>Saída JSON do script de listagem: { packages: [...] , provisioned: [...] , provisionedError: "..." }.</summary>
    public static (IReadOnlyList<InstalledPackage> Packages, IReadOnlyDictionary<string, string> Provisioned, string? ProvisionedError) ParseInventory(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var packages = new List<InstalledPackage>();
        foreach (var p in AsArray(root, "packages"))
        {
            string S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            bool B(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            long? size = p.TryGetProperty("Size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
            if (S("Name").Length == 0) continue;
            packages.Add(new InstalledPackage(S("Name"), S("PackageFullName"), S("Publisher"), B("NonRemovable"), B("IsFramework"), size));
        }

        var provisioned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in AsArray(root, "provisioned"))
        {
            var display = p.TryGetProperty("DisplayName", out var d) ? d.GetString() : null;
            var pkg = p.TryGetProperty("PackageName", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(display) && !string.IsNullOrEmpty(pkg)) provisioned[display] = pkg;
        }
        string? error = root.TryGetProperty("provisionedError", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
        return (packages, provisioned, string.IsNullOrWhiteSpace(error) ? null : error);
    }

    // ConvertTo-Json devolve objeto (não array) quando há um item só.
    private static IEnumerable<JsonElement> AsArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().ToList(),
            JsonValueKind.Object => [v],
            _ => []
        };
    }

    public static IReadOnlyList<DebloatItem> Classify(IReadOnlyList<DebloatCatalogEntry> catalog,
        IEnumerable<InstalledPackage> packages, IReadOnlyDictionary<string, string> provisioned) =>
        packages
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase) // -AllUsers repete o pacote por arquitetura/usuário
            .Select(g => g.OrderByDescending(p => p.SizeBytes ?? 0).First())
            .Select(p => new DebloatItem(p, Match(catalog, p.Name), provisioned.GetValueOrDefault(p.Name)))
            .OrderBy(i => i.Category).ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Ids "Leve": só o que é claramente dispensável e fácil de reinstalar.</summary>
    public static readonly string[] LightIds =
    [
        "Clipchamp.Clipchamp", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.BingNews", "Microsoft.BingWeather",
        "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.WindowsFeedbackHub", "Microsoft.MicrosoftOfficeHub"
    ];

    /// <summary>Quais itens um preset marca. Itens protegidos/não classificados nunca são marcados.</summary>
    public static IReadOnlyList<DebloatItem> PresetSelection(DebloatPreset preset, IEnumerable<DebloatItem> items) =>
        items.Where(i => i.IsRemovable && preset switch
        {
            DebloatPreset.Leve => i.Category == DebloatCategory.Safe && LightIds.Contains(i.Entry!.Id, StringComparer.OrdinalIgnoreCase),
            DebloatPreset.Equilibrado => i.Category == DebloatCategory.Safe,
            _ => true
        }).ToList();

    /// <summary>Plano final: mesmo que algo protegido seja passado, ele vai para "Blocked", nunca para "ToRemove".</summary>
    public static DebloatPlan BuildPlan(IEnumerable<DebloatItem> selected, bool includeProvisioned)
    {
        var list = selected.Distinct().ToList();
        return new DebloatPlan(list.Where(i => i.IsRemovable).ToList(), list.Where(i => !i.IsRemovable).ToList(), includeProvisioned);
    }
}

public sealed record DebloatResult(DebloatItem Item, bool Success, string Message);

/// <summary>
/// Lista e remove apps Appx de forma controlada. Remoção de Appx NÃO é 100% reversível por registro:
/// o desfazer reinstala pela Store (winget msstore quando há ID confirmado; senão abre a busca da Store).
/// </summary>
public sealed class DebloatService(TweakContext ctx) : IUndoProvider
{
    public const string UndoPrefix = "debloat:";
    private static readonly Lazy<IReadOnlyList<DebloatCatalogEntry>> CatalogCache = new(LoadCatalog);

    public static IReadOnlyList<DebloatCatalogEntry> Catalog => CatalogCache.Value;

    private static IReadOnlyList<DebloatCatalogEntry> LoadCatalog()
    {
        var asm = typeof(DebloatService).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("debloat.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return DebloatRules.ParseCatalog(reader.ReadToEnd());
    }

    public const string InventoryScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $pk = Get-AppxPackage -AllUsers | ForEach-Object {
            $size = $null
            if (-not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' -and $_.InstallLocation) {
                $size = (Get-ChildItem -LiteralPath $_.InstallLocation -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            }
            [pscustomobject]@{ Name = $_.Name; PackageFullName = $_.PackageFullName; Publisher = $_.Publisher;
                NonRemovable = [bool]$_.NonRemovable; IsFramework = [bool]$_.IsFramework; Size = $size }
        }
        $prov = @(); $provErr = $null
        try { $prov = Get-AppxProvisionedPackage -Online -ErrorAction Stop | Select-Object DisplayName, PackageName }
        catch { $provErr = $_.Exception.Message }
        [pscustomobject]@{ packages = @($pk); provisioned = @($prov); provisionedError = $provErr } | ConvertTo-Json -Depth 4 -Compress
        """;

    public string? ProvisionedError { get; private set; }

    public async Task<IReadOnlyList<DebloatItem>> ListAsync()
    {
        var r = await ctx.Shell.RunPowerShellAsync(InventoryScript, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        if (!r.Success || string.IsNullOrWhiteSpace(r.StdOut))
            throw new InvalidOperationException($"Não foi possível listar os apps instalados. {r.StdErr}".Trim());
        var (packages, provisioned, error) = DebloatRules.ParseInventory(r.StdOut.Trim());
        ProvisionedError = error;
        return DebloatRules.Classify(Catalog, packages, provisioned);
    }

    public async Task<IReadOnlyList<DebloatResult>> RemoveAsync(DebloatPlan plan, IProgress<string>? progress = null)
    {
        var results = new List<DebloatResult>();
        foreach (var item in plan.ToRemove)
        {
            if (!item.IsRemovable) continue; // proteção extra: nunca remove protegido/não classificado
            progress?.Report($"Removendo {item.DisplayName}...");
            var r = await ctx.Shell.RunPowerShellMutatingAsync($"Remover app {item.DisplayName}",
                $"Remove-AppxPackage -Package {Shell.PsQuote(item.Package.FullName)} -AllUsers -ErrorAction Stop",
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            if (r.Success && plan.IncludeProvisioned && item.ProvisionedPackageName is { } prov)
            {
                var rp = await ctx.Shell.RunPowerShellMutatingAsync($"Remover {item.DisplayName} para novos usuários",
                    $"Remove-AppxProvisionedPackage -Online -PackageName {Shell.PsQuote(prov)} -ErrorAction Stop | Out-Null",
                    TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                if (!rp.Success) ctx.Logger.Warn($"{item.DisplayName}: removido do usuário, mas não do provisionamento: {rp.StdErr.Trim()}");
            }

            if (r.Success)
            {
                if (!r.Simulated)
                    ctx.SaveCustomBackup(UndoPrefix + item.Package.Name, "store-id",
                        string.IsNullOrEmpty(item.Entry?.StoreId) ? $"search:{item.DisplayName}" : item.Entry!.StoreId,
                        $"App removido: {item.DisplayName} ({item.Package.FullName})");
                ctx.Logger.Info(r.Simulated ? $"Remoção simulada: {item.DisplayName}" : $"App removido: {item.DisplayName}");
                results.Add(new DebloatResult(item, true, r.Simulated ? "Simulado." : "Removido."));
            }
            else
            {
                var msg = string.IsNullOrWhiteSpace(r.StdErr) ? "O Windows recusou a remoção." : r.StdErr.Trim();
                ctx.Logger.Error($"Falha ao remover {item.DisplayName}: {msg}");
                results.Add(new DebloatResult(item, false, msg));
            }
        }
        return results;
    }

    // --- IUndoProvider: reinstalação pela aba Restauração ---
    public bool CanUndo(string id) => id.StartsWith(UndoPrefix, StringComparison.Ordinal);

    public string DisplayName(string id)
    {
        var name = id[UndoPrefix.Length..];
        return $"App removido: {DebloatRules.Match(Catalog, name)?.Name ?? name}";
    }

    public async Task UndoAsync(string id)
    {
        var value = ctx.Backup.GetCustom(id, "store-id") ?? string.Empty;
        var name = DebloatRules.Match(Catalog, id[UndoPrefix.Length..])?.Name ?? id[UndoPrefix.Length..];
        if (value.StartsWith("search:", StringComparison.Ordinal) || value.Length == 0)
        {
            // Sem ID confirmado: abre a busca da Store para o usuário reinstalar manualmente.
            var query = Uri.EscapeDataString(value.Length > 7 ? value[7..] : name);
            if (ctx.DryRun) ctx.Logger.Simulated($"Abrir a Microsoft Store na busca por \"{name}\"");
            else Process.Start(new ProcessStartInfo($"ms-windows-store://search/?query={query}") { UseShellExecute = true })?.Dispose();
        }
        else
        {
            if (!CatalogParser.IsValidWingetId(value)) throw new InvalidOperationException("ID da Store inválido no backup.");
            var r = await ctx.Shell.RunMutatingAsync($"Reinstalar {name}", "winget.exe",
                ["install", "--id", value, "-e", "--source", "msstore", "--silent",
                 "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(15)).ConfigureAwait(false);
            if (!r.Success && !r.Simulated)
                throw new InvalidOperationException(WingetService.Classify(value, r.ExitCode, r.StdOut + r.StdErr).Message);
        }
        if (!ctx.DryRun) ctx.Backup.RemoveTweak(id);
        ctx.Logger.Info($"Reinstalação de {name} iniciada.");
    }
}
