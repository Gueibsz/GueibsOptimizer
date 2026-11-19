using System.Text.Json.Serialization;

namespace GueibsOptimizer.Models;

/// <summary>Programa do catálogo de instalação (instalado pelo winget a partir do id).</summary>
public sealed record AppItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("wingetId")] string WingetId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description);
