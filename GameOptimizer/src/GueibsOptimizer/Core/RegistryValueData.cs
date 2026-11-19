using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace GueibsOptimizer.Core;

public enum RegHive
{
    LocalMachine,
    CurrentUser
}

/// <summary>
/// Valor de registro com tipo explícito, serializável em JSON sem perder o tipo
/// (um <c>object</c> cru viraria JsonElement ao recarregar).
/// </summary>
public sealed record RegistryValueData
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RegistryValueKind Kind { get; init; }

    /// <summary>String / ExpandString.</summary>
    public string? Text { get; init; }

    /// <summary>DWord (como uint32) / QWord (como uint64 reinterpretado em long).</summary>
    public long? Number { get; init; }

    /// <summary>MultiString.</summary>
    public string[]? Lines { get; init; }

    /// <summary>Binary em Base64.</summary>
    public string? Base64 { get; init; }

    public static RegistryValueData FromObject(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString =>
            new RegistryValueData { Kind = kind, Text = Convert.ToString(value) ?? string.Empty },
        RegistryValueKind.DWord => new RegistryValueData { Kind = kind, Number = NormalizeDWord(value) },
        RegistryValueKind.QWord => new RegistryValueData { Kind = kind, Number = NormalizeQWord(value) },
        RegistryValueKind.MultiString => new RegistryValueData { Kind = kind, Lines = (string[])value },
        RegistryValueKind.Binary => new RegistryValueData { Kind = kind, Base64 = Convert.ToBase64String((byte[])value) },
        _ => throw new NotSupportedException($"Tipo de registro não suportado: {kind}")
    };

    public static RegistryValueData DWord(uint value) => new() { Kind = RegistryValueKind.DWord, Number = value };
    public static RegistryValueData String(string value) => new() { Kind = RegistryValueKind.String, Text = value };

    /// <summary>Converte para o objeto que <see cref="RegistryKey.SetValue(string?, object, RegistryValueKind)"/> espera.</summary>
    public object ToRegistryObject() => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => Text ?? string.Empty,
        RegistryValueKind.DWord => unchecked((int)(uint)(Number ?? 0)),
        RegistryValueKind.QWord => Number ?? 0L,
        RegistryValueKind.MultiString => Lines ?? [],
        RegistryValueKind.Binary => Convert.FromBase64String(Base64 ?? string.Empty),
        _ => throw new NotSupportedException($"Tipo de registro não suportado: {Kind}")
    };

    public bool SameValueAs(RegistryValueData? other)
    {
        if (other is null) return false;
        bool kindsCompatible = Kind == other.Kind ||
            (IsStringKind(Kind) && IsStringKind(other.Kind));
        if (!kindsCompatible) return false;

        return Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => string.Equals(Text, other.Text, StringComparison.Ordinal),
            RegistryValueKind.DWord or RegistryValueKind.QWord => Number == other.Number,
            RegistryValueKind.MultiString => (Lines ?? []).SequenceEqual(other.Lines ?? []),
            RegistryValueKind.Binary => Base64 == other.Base64,
            _ => false
        };
    }

    public string Display() => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => $"\"{Text}\"",
        RegistryValueKind.DWord or RegistryValueKind.QWord => $"{Number} (0x{Number:X})",
        RegistryValueKind.MultiString => string.Join(" | ", Lines ?? []),
        RegistryValueKind.Binary => $"bin:{Base64}",
        _ => "?"
    };

    private static bool IsStringKind(RegistryValueKind k) => k is RegistryValueKind.String or RegistryValueKind.ExpandString;

    // O registro devolve DWORD como int; guardamos como uint32 para 0xFFFFFFFF não virar -1.
    private static long NormalizeDWord(object value) => value switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        long l => unchecked((uint)l),
        _ => unchecked((uint)Convert.ToInt64(value))
    };

    private static long NormalizeQWord(object value) => value switch
    {
        long l => l,
        ulong ul => unchecked((long)ul),
        _ => Convert.ToInt64(value)
    };
}
