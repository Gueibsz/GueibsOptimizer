using System.Globalization;
using System.Text.RegularExpressions;

namespace GueibsOptimizer.Services.Tweaks;

/// <summary>Camada do plano de energia ligada aos níveis de desempenho (8.4).</summary>
public enum PowerTier
{
    /// <summary>Baixo e Médio: tempos em "Nunca" e timers de despertar desativados.</summary>
    Base,
    /// <summary>Alto: USB, PCIe, Wi-Fi, mídia, fundo e processador 5%–100%.</summary>
    Advanced,
    /// <summary>Máximo: processador 100%–100% e extras (boost, EPP, core parking, resfriamento) se existirem.</summary>
    Maximum
}

/// <summary>
/// Uma configuração do plano (GUIDs oficiais do Windows). <paramref name="Timeout"/> = tempo, aplicado na tomada e na bateria;
/// as demais são de desempenho e, em notebook, só mudam na tomada. <paramref name="AtLeast"/>: basta o valor atual ser maior
/// ou igual (o nível Alto não rebaixa o processador mínimo de 100% do Máximo).
/// </summary>
public sealed record PowerSettingDef(PowerTier Tier, string Subgroup, string Setting, uint Value, string Title, bool Timeout, bool AtLeast = false);

/// <summary>Uma linha de comando do powercfg já com os argumentos separados (para ArgumentList).</summary>
public sealed record PowerCommand(string Description, IReadOnlyList<string> Arguments)
{
    public string Display => "powercfg " + string.Join(" ", Arguments);
}

public static partial class PowerPlanSettings
{
    public const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    public const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    public const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    public const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    public const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    public const string SubBackground = "0d7dbae2-4294-402a-ba8e-26777e8488cd";
    public const string SubInternetExplorer = "02f815b5-a5cf-4c84-bf20-649d1f75d3d8";
    public const string SubMultimedia = "9596fb26-9850-41fd-ac3e-f7c3c00afd4b";
    public const string SubCooling = SubProcessor;

    /// <summary>Estados de ociosidade do processador (C-states). NUNCA é alterado; está aqui só para os testes garantirem isso.</summary>
    public const string ProcessorIdleDisable = "5d76a2ca-e8c0-402f-a133-2158492d58ad";

    public static readonly IReadOnlyList<PowerSettingDef> All =
    [
        // --- Base (Baixo/Médio): tempos em "Nunca" (0) e timers de despertar ---
        new(PowerTier.Base, SubVideo, "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", 0, "Desligar a tela após: nunca", Timeout: true),
        new(PowerTier.Base, SubVideo, "8ec4b3a5-6868-48c2-be75-4f3044be88a7", 0, "Desligar a tela na tela de bloqueio após: nunca", Timeout: true),
        new(PowerTier.Base, SubSleep, "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", 0, "Suspender após: nunca", Timeout: true),
        new(PowerTier.Base, SubSleep, "9d7815a6-7ee4-497e-8888-515a05f02364", 0, "Hibernar após: nunca", Timeout: true),
        new(PowerTier.Base, SubDisk, "6738e2c4-e8a5-4a42-b16a-e040e769756e", 0, "Desligar o disco rígido após: nunca", Timeout: true),
        new(PowerTier.Base, SubSleep, "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", 0, "Timers de despertar: desativados", Timeout: true),

        // --- Alto ---
        new(PowerTier.Advanced, SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, "Suspensão seletiva de USB: desativada", Timeout: false),
        new(PowerTier.Advanced, SubPcie, "ee12f906-d277-404b-b6da-e5fa1a576df5", 0, "Gerenciamento de energia do link PCIe: desligado", Timeout: false),
        new(PowerTier.Advanced, SubWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0, "Adaptador sem fio: desempenho máximo", Timeout: false),
        new(PowerTier.Advanced, SubBackground, "309dce9b-bef4-4119-9921-a851fb12f0f4", 1, "Apresentação de slides do fundo: pausada", Timeout: false),
        new(PowerTier.Advanced, SubInternetExplorer, "4c793e7d-a264-42e1-87d3-7a0d2f523ccd", 1, "Timer JavaScript do IE: desempenho máximo", Timeout: false),
        new(PowerTier.Advanced, SubMultimedia, "03680956-93bc-4294-bba6-4e0f09bb717f", 1, "Ao compartilhar mídia: impedir ociosidade", Timeout: false),
        new(PowerTier.Advanced, SubMultimedia, "10778347-1370-4ee0-8bbd-33bdacaade49", 1, "Qualidade de reprodução de vídeo: viés de desempenho", Timeout: false),
        new(PowerTier.Advanced, SubMultimedia, "34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4", 0, "Ao reproduzir vídeo: otimizar a qualidade", Timeout: false),
        new(PowerTier.Advanced, SubProcessor, "893dee8e-2bef-41e0-89c6-b55d0929964c", 5, "Estado mínimo do processador: 5%", Timeout: false, AtLeast: true),
        new(PowerTier.Advanced, SubProcessor, "bc5038f7-23e0-4960-96da-33abaf5935ec", 100, "Estado máximo do processador: 100%", Timeout: false),

        // --- Máximo (extras só se o GUID existir na máquina) ---
        new(PowerTier.Maximum, SubProcessor, "893dee8e-2bef-41e0-89c6-b55d0929964c", 100, "Estado mínimo do processador: 100%", Timeout: false),
        new(PowerTier.Maximum, SubProcessor, "bc5038f7-23e0-4960-96da-33abaf5935ec", 100, "Estado máximo do processador: 100%", Timeout: false),
        new(PowerTier.Maximum, SubProcessor, "be337238-0d82-4146-a960-4f3749d470c7", 2, "Modo de boost do processador: agressivo", Timeout: false),
        new(PowerTier.Maximum, SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", 0, "Preferência de energia (EPP): 0, desempenho", Timeout: false),
        new(PowerTier.Maximum, SubProcessor, "0cc5b647-c1df-4637-891a-dec35c318583", 100, "Core parking: 100% dos núcleos sempre ativos", Timeout: false),
        new(PowerTier.Maximum, SubCooling, "94d3a615-a899-4ac5-ae2b-e4d8f634367f", 1, "Política de resfriamento do sistema: ativa", Timeout: false)
    ];

    public static IReadOnlyList<PowerSettingDef> ForTier(PowerTier tier) => All.Where(s => s.Tier == tier).ToList();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,8})\s*$")]
    private static partial Regex HexAtEnd();

    /// <summary>
    /// Lê a saída de "powercfg /qh &lt;plano&gt;" (inclui configurações ocultas). Cada bloco começa numa linha com GUID;
    /// os dois últimos números 0x... do bloco são o índice na tomada (AC) e na bateria (DC). Usa só GUIDs e números,
    /// então funciona em qualquer idioma do Windows. Configurações ausentes simplesmente não aparecem.
    /// </summary>
    public static IReadOnlyDictionary<string, (uint Ac, uint Dc)> ParseQuery(string output)
    {
        var result = new Dictionary<string, (uint, uint)>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var hex = new List<uint>();
        void Flush()
        {
            if (current is not null && hex.Count >= 2) result[current] = (hex[^2], hex[^1]);
            hex.Clear();
        }
        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            var g = GuidPattern().Match(raw);
            if (g.Success)
            {
                Flush();
                current = g.Value.ToLowerInvariant();
                continue;
            }
            var h = HexAtEnd().Match(raw.Trim());
            if (h.Success && current is not null) hex.Add(uint.Parse(h.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        Flush();
        return result;
    }

    public static bool IsSatisfied(PowerSettingDef s, (uint Ac, uint Dc) current, bool changeDc)
    {
        bool Ok(uint v) => s.AtLeast ? v >= s.Value : v == s.Value;
        return Ok(current.Ac) && (!changeDc || Ok(current.Dc));
    }

    /// <summary>Tempos mudam também na bateria; desempenho só na tomada quando há bateria (notebook).</summary>
    public static bool ChangesDc(PowerSettingDef s, bool hasBattery) => s.Timeout || !hasBattery;

    /// <summary>
    /// Comandos para levar a camada ao alvo, pulando o que já está certo (idempotente) e o que não existe nesta máquina.
    /// </summary>
    public static (IReadOnlyList<PowerCommand> Commands, IReadOnlyList<PowerSettingDef> Missing) BuildCommands(
        string scheme, IEnumerable<PowerSettingDef> settings, IReadOnlyDictionary<string, (uint Ac, uint Dc)> state, bool hasBattery)
    {
        var commands = new List<PowerCommand>();
        var missing = new List<PowerSettingDef>();
        foreach (var s in settings)
        {
            if (!state.TryGetValue(s.Setting, out var cur)) { missing.Add(s); continue; }
            bool dc = ChangesDc(s, hasBattery);
            if (IsSatisfied(s, cur, dc)) continue;
            string value = s.Value.ToString(CultureInfo.InvariantCulture);
            if (!(s.AtLeast ? cur.Ac >= s.Value : cur.Ac == s.Value))
                commands.Add(new PowerCommand($"{s.Title} (tomada)", ["/setacvalueindex", scheme, s.Subgroup, s.Setting, value]));
            if (dc && !(s.AtLeast ? cur.Dc >= s.Value : cur.Dc == s.Value))
                commands.Add(new PowerCommand($"{s.Title} (bateria)", ["/setdcvalueindex", scheme, s.Subgroup, s.Setting, value]));
        }
        return (commands, missing);
    }
}
