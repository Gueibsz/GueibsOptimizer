using System.Text.Json.Serialization;

namespace GueibsOptimizer.Models;

/// <summary>Conjunto de ids de ajustes. O arquivo .gopro é este JSON; nunca contém comandos.</summary>
public sealed class Profile
{
    public const string FormatId = "GueibsOptimizer.Profile";
    /// <summary>Formato dos perfis exportados até a versão 2.0 (o app se chamava GameOptimizer). Aceito na importação.</summary>
    public const string LegacyFormatId = "GameOptimizer.Profile";
    public const int CurrentVersion = 1;

    [JsonPropertyName("format")] public string Format { get; set; } = FormatId;
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("tweaks")] public List<string> Tweaks { get; set; } = [];
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Perfis padrão não podem ser excluídos nem sobrescritos.</summary>
    [JsonIgnore] public bool IsBuiltIn { get; init; }

    [JsonIgnore] public string? FilePath { get; set; }

    [JsonIgnore] public string KindText => IsBuiltIn ? "Padrão" : "Personalizado";
    [JsonIgnore] public string CountText => $"{Tweaks.Count} ajustes";
}
