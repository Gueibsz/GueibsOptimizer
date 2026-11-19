namespace GueibsOptimizer.Models;

public enum DebloatCategory
{
    Safe,
    Optional,
    Protected,
    /// <summary>Instalado, mas fora do catálogo: nunca é removido pelo app.</summary>
    Unclassified
}

public enum DebloatPreset
{
    Leve,
    Equilibrado,
    Agressivo
}

public sealed record DebloatCatalogEntry(string Id, string Name, DebloatCategory Category, string Reason, string StoreId);

/// <summary>Pacote Appx instalado, como devolvido pelo Get-AppxPackage.</summary>
public sealed record InstalledPackage(string Name, string FullName, string Publisher, bool NonRemovable, bool IsFramework, long? SizeBytes);

public sealed record DebloatItem(InstalledPackage Package, DebloatCatalogEntry? Entry, string? ProvisionedPackageName)
{
    public DebloatCategory Category =>
        Package.NonRemovable || Package.IsFramework ? DebloatCategory.Protected
        : Entry?.Category ?? DebloatCategory.Unclassified;

    public bool IsRemovable => Category is DebloatCategory.Safe or DebloatCategory.Optional;

    public string DisplayName => Entry?.Name ?? Package.Name;

    public string Explanation => Category switch
    {
        DebloatCategory.Protected when Package.NonRemovable =>
            "O Windows marca este pacote como não removível (parte do sistema).",
        DebloatCategory.Protected when Package.IsFramework =>
            "É uma biblioteca usada por outros apps; removê-la quebraria esses apps.",
        DebloatCategory.Protected => Entry?.Reason ?? "Protegido.",
        DebloatCategory.Unclassified =>
            "Não está no catálogo do Gueibs Optimizer. Para não remover algo necessário, o app só remove itens classificados.",
        _ => Entry?.Reason ?? string.Empty
    };

    public string CategoryText => Category switch
    {
        DebloatCategory.Safe => "Seguro remover",
        DebloatCategory.Optional => "Opcional",
        DebloatCategory.Protected => "Não remover / protegido",
        _ => "Não classificado"
    };

    public string ReinstallText => string.IsNullOrEmpty(Entry?.StoreId)
        ? "Para reinstalar: o app abre a busca na Microsoft Store (não há ID confirmado para o winget)."
        : $"Para reinstalar: winget install --id {Entry!.StoreId} --source msstore (ou pela aba Restauração).";

    public string SizeText => Package.SizeBytes is { } b and > 0 ? $"{b / (1024.0 * 1024):0} MB" : "tamanho indisponível";
}

public sealed record DebloatPlan(IReadOnlyList<DebloatItem> ToRemove, IReadOnlyList<DebloatItem> Blocked, bool IncludeProvisioned)
{
    public bool HasOptional => ToRemove.Any(i => i.Category == DebloatCategory.Optional);
}
