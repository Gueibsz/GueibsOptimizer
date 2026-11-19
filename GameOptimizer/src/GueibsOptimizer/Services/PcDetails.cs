using System.Management;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GueibsOptimizer.Services;

/// <summary>Um campo de uma aba do Meu PC. Seriais ficam ocultos por padrão na tela e na exportação.</summary>
public sealed record PcField(string Label, string Value, bool IsSerial = false);

public sealed record PcSection(string Key, string Title, IReadOnlyList<PcField> Fields);

public sealed record CacheEntry(int Level, ulong SizeKb, int Count);

public sealed record SlotDetail(string Locator, bool Occupied, string Text);

public sealed record SlotSummary(int Total, int Occupied, int Free, ulong MaxCapacityKb, IReadOnlyList<SlotDetail> Slots);

public sealed record DiskDetail(string Model, string Kind, ulong SizeBytes, string Health, string Serial);

/// <summary>Dados do Meu PC avançado (8.6). Tudo lido via WMI/registro; cada parte falha de forma isolada.</summary>
public sealed record PcDetails(
    SystemSummary Summary,
    string CpuManufacturer,
    string Socket,
    int CurrentClockMhz,
    string Virtualization,
    IReadOnlyList<CacheEntry> Caches,
    string BoardManufacturer,
    string BoardProduct,
    string BoardVersion,
    string BoardSerial,
    string BiosVersion,
    string BiosDate,
    string BiosSerial,
    string Chipset,
    SlotSummary Slots,
    IReadOnlyList<DiskDetail> Disks,
    IReadOnlyList<string> MemorySerials);

public static partial class PcDetailsLogic
{
    /// <summary>Win32_CacheMemory.Level: 3 = L1 (primário), 4 = L2, 5 = L3.</summary>
    public static int CacheLevel(int wmiLevel, string purpose)
    {
        if (wmiLevel is >= 3 and <= 5) return wmiLevel - 2;
        var m = Regex.Match(purpose ?? string.Empty, @"L(\d)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Soma por nível; entradas desconhecidas (nível 0) são ignoradas.</summary>
    public static IReadOnlyList<CacheEntry> GroupCaches(IEnumerable<(int Level, ulong SizeKb)> raw) =>
        raw.Where(c => c.Level is >= 1 and <= 4 && c.SizeKb > 0)
            .GroupBy(c => c.Level)
            .OrderBy(g => g.Key)
            .Select(g => new CacheEntry(g.Key, g.Aggregate(0UL, (s, c) => s + c.SizeKb), g.Count()))
            .ToList();

    public static string CacheSize(ulong kb) => kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";

    /// <summary>
    /// Slots: total = soma de MemoryDevices das matrizes de memória do sistema (Use = 3); ocupados = pentes
    /// detectados. Se o firmware não informar o total, usa o número de pentes.
    /// </summary>
    public static SlotSummary ComputeSlots(IEnumerable<(int Use, int MemoryDevices, ulong MaxCapacityKb)> arrays, IReadOnlyList<MemoryModule> modules)
    {
        var system = arrays.Where(a => a.Use is 3 or 0).ToList();
        int total = system.Sum(a => Math.Max(0, a.MemoryDevices));
        ulong max = system.Aggregate(0UL, (s, a) => s + a.MaxCapacityKb);
        int occupied = modules.Count(m => m.CapacityBytes > 0);
        if (total < occupied) total = occupied;
        var slots = modules.Where(m => m.CapacityBytes > 0)
            .Select(m => new SlotDetail(SlotName(m),
                true,
                $"{SystemSummary.Gb(m.CapacityBytes)} {SystemSummary.MemoryTypeText(m.SmbiosType)} {m.ConfiguredMts} MT/s {m.Manufacturer} {m.PartNumber}".Replace("  ", " ").Trim()))
            .ToList();
        for (int i = occupied; i < total; i++) slots.Add(new SlotDetail($"Slot livre {i - occupied + 1}", false, "vazio"));
        return new SlotSummary(total, occupied, total - occupied, max, slots);
    }

    private static string SlotName(MemoryModule m) =>
        string.IsNullOrWhiteSpace(m.Locator) ? (string.IsNullOrWhiteSpace(m.Bank) ? "Slot" : m.Bank) : m.Locator;

    /// <summary>
    /// Chipset inferido: primeiro pelo nome da placa (ex.: "PRIME B250M-A" → B250), depois pelos dispositivos PCI
    /// (LPC/SMBus/Chipset, preferindo o modelo entre parênteses). Null quando não dá para identificar.
    /// </summary>
    public static string? InferChipset(string boardProduct, IEnumerable<string> pciNames)
    {
        if (MatchChipset(boardProduct) is { } fromBoard) return fromBoard;
        foreach (var name in pciNames.Where(n => Regex.IsMatch(n, "LPC|SMBus|Chipset|eSPI", RegexOptions.IgnoreCase)))
        {
            var inParens = Regex.Matches(name, @"\(([^)]*)\)").Select(m => MatchChipset(m.Groups[1].Value)).FirstOrDefault(c => c is not null);
            if (inParens is not null) return inParens;
            if (MatchChipset(name) is { } c) return c;
        }
        return null;
    }

    private static string? MatchChipset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ChipsetRegex().Match(text);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])([ABHQXZW]\d{3})(?:[A-Z]{0,2})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetRegex();

    /// <summary>MSFT_PhysicalDisk.HealthStatus: 0 = Saudável, 1 = Atenção, 2 = Não saudável.</summary>
    public static string HealthText(int status) => status switch
    {
        0 => "Saudável",
        1 => "Atenção",
        2 => "Não saudável",
        _ => "Desconhecido"
    };

    public static string VirtualizationText(bool? firmwareEnabled, bool hypervisorPresent) => (firmwareEnabled, hypervisorPresent) switch
    {
        (_, true) => "Em uso (hypervisor do Windows ativo)",
        (true, _) => "Ativada no firmware (BIOS/UEFI)",
        (false, _) => "Desativada no firmware (BIOS/UEFI)",
        _ => "Não informado"
    };

    // ---------- Seções (usadas pelas abas e pela exportação) ----------

    public static IReadOnlyList<PcSection> Sections(PcDetails d)
    {
        var s = d.Summary;
        var list = new List<PcSection>();

        var cpu = new List<PcField>();
        if (s.Cpu is { } c)
        {
            cpu.Add(new("Modelo", c.Name));
            cpu.Add(new("Fabricante", Or(d.CpuManufacturer)));
            cpu.Add(new("Núcleos / threads", $"{c.Cores} / {c.Threads}"));
            cpu.Add(new("Clock base (máximo informado)", c.MaxClockMhz > 0 ? $"{c.MaxClockMhz} MHz" : "—"));
            cpu.Add(new("Clock atual", d.CurrentClockMhz > 0 ? $"{d.CurrentClockMhz} MHz" : "—"));
        }
        else cpu.Add(new("Modelo", "não identificado"));
        cpu.Add(new("Soquete", Or(d.Socket)));
        cpu.Add(new("Virtualização", d.Virtualization));
        list.Add(new("cpu", "Processador", cpu));

        list.Add(new("cache", "Caches", d.Caches.Count == 0
            ? [new PcField("Caches", "não informado pelo firmware")]
            : d.Caches.Select(x => new PcField($"L{x.Level}", x.Count > 1 ? $"{CacheSize(x.SizeKb)} (em {x.Count} blocos)" : CacheSize(x.SizeKb))).ToList()));

        list.Add(new("board", "Placa-mãe",
        [
            new("Fabricante", Or(d.BoardManufacturer)),
            new("Modelo", Or(d.BoardProduct)),
            new("Versão", Or(d.BoardVersion)),
            new("Chipset", d.Chipset),
            new("BIOS", Or(d.BiosVersion)),
            new("Data da BIOS", Or(d.BiosDate)),
            new("Número de série da placa", Or(d.BoardSerial), IsSerial: true),
            new("Número de série da BIOS", Or(d.BiosSerial), IsSerial: true)
        ]));

        var mem = new List<PcField> { new("Total", s.Memory.Count > 0 ? SystemSummary.Gb(s.TotalMemoryBytes) : "—") };
        if (s.Memory.FirstOrDefault() is { } first)
        {
            mem.Add(new("Tipo", SystemSummary.MemoryTypeText(first.SmbiosType)));
            mem.Add(new("Velocidade configurada", first.ConfiguredMts > 0 ? $"{first.ConfiguredMts} MT/s" : "—"));
            mem.Add(new("Velocidade nominal", first.SpeedMts > 0 ? $"{first.SpeedMts} MT/s" : "—"));
            mem.Add(new("Pentes", s.Memory.Count.ToString()));
        }
        for (int i = 0; i < d.MemorySerials.Count; i++)
            mem.Add(new($"Número de série do pente {i + 1}", Or(d.MemorySerials[i]), IsSerial: true));
        list.Add(new("memory", "Memória", mem));

        var slots = new List<PcField>
        {
            new("Slots no total", d.Slots.Total > 0 ? d.Slots.Total.ToString() : "não informado"),
            new("Ocupados", d.Slots.Occupied.ToString()),
            new("Livres", d.Slots.Free.ToString())
        };
        if (d.Slots.MaxCapacityKb > 0) slots.Add(new("Capacidade máxima suportada", SystemSummary.Gb(d.Slots.MaxCapacityKb * 1024)));
        slots.AddRange(d.Slots.Slots.Select(x => new PcField(x.Locator, x.Text)));
        list.Add(new("slots", "Slots de memória", slots));

        var gfx = new List<PcField>();
        foreach (var g in s.Gpus)
        {
            gfx.Add(new("GPU", g.DisplayName));
            gfx.Add(new("Driver", $"{g.Driver} ({g.DriverDate})"));
            gfx.Add(new("VRAM", g.VramBytes is { } v ? SystemSummary.Gb(v) : "desconhecida"));
        }
        foreach (var m in s.Monitors)
            gfx.Add(new(m.Primary ? "Monitor principal" : "Monitor", $"{m.Width}x{m.Height} a {m.CurrentHz} Hz (máx. {m.MaxHzAtResolution} Hz)"));
        if (gfx.Count == 0) gfx.Add(new("GPU", "não identificada"));
        list.Add(new("gpu", "Gráficos", gfx));

        var storage = new List<PcField>();
        foreach (var disk in d.Disks)
        {
            storage.Add(new("Disco", $"{disk.Model} ({disk.Kind}, {SystemSummary.Gb(disk.SizeBytes)})"));
            storage.Add(new("Saúde", disk.Health));
            storage.Add(new("Número de série", Or(disk.Serial), IsSerial: true));
        }
        foreach (var v in s.Volumes)
            storage.Add(new($"Unidade {v.Name.TrimEnd('\\')}", $"{SystemSummary.Gb(v.FreeBytes)} livres de {SystemSummary.Gb(v.TotalBytes)}"));
        if (storage.Count == 0) storage.Add(new("Disco", "não identificado"));
        list.Add(new("storage", "Armazenamento", storage));

        list.Add(new("windows", "Windows", [new PcField("Versão", s.Windows)]));
        return list;
    }

    private static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

    public const string HiddenSerial = "(oculto)";

    private static string Value(PcField f, bool includeSerials) => f.IsSerial && !includeSerials ? HiddenSerial : f.Value;

    // ---------- Exportação ----------

    public static string ToText(IReadOnlyList<PcSection> sections, bool includeSerials, DateTime generatedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Relatório do PC — Gueibs Optimizer");
        sb.AppendLine($"Gerado em {generatedAt:dd/MM/yyyy HH:mm}");
        if (!includeSerials) sb.AppendLine("Números de série omitidos.");
        foreach (var section in sections)
        {
            sb.AppendLine();
            sb.AppendLine($"== {section.Title} ==");
            foreach (var f in section.Fields.Where(f => includeSerials || !f.IsSerial))
                sb.AppendLine($"{f.Label}: {Value(f, includeSerials)}");
        }
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<PcSection> sections, bool includeSerials, DateTime generatedAt)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>Relatório do PC</title>");
        sb.Append("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#16181c;color:#e8e8e8;margin:32px}h1{font-size:22px}");
        sb.Append("h2{font-size:16px;color:#e0503c;margin-top:24px}table{border-collapse:collapse;width:100%;max-width:820px}");
        sb.Append("td{padding:6px 10px;border-bottom:1px solid #2c3036}td:first-child{color:#9aa0a8;width:40%}</style></head><body>");
        sb.Append($"<h1>Relatório do PC — Gueibs Optimizer</h1><p>Gerado em {generatedAt:dd/MM/yyyy HH:mm}");
        if (!includeSerials) sb.Append(" · números de série omitidos");
        sb.Append("</p>");
        foreach (var section in sections)
        {
            sb.Append($"<h2>{E(section.Title)}</h2><table>");
            foreach (var f in section.Fields.Where(f => includeSerials || !f.IsSerial))
                sb.Append($"<tr><td>{E(f.Label)}</td><td>{E(Value(f, includeSerials))}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<PcSection> sections, bool includeSerials, DateTime generatedAt)
    {
        var payload = new
        {
            generatedBy = "Gueibs Optimizer",
            generatedAt = generatedAt.ToString("s"),
            serialsIncluded = includeSerials,
            sections = sections.Select(s => new
            {
                key = s.Key,
                title = s.Title,
                fields = s.Fields.Where(f => includeSerials || !f.IsSerial).Select(f => new { label = f.Label, value = f.Value })
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

public sealed partial class SystemInfoService
{
    public Task<PcDetails> CollectDetailsAsync(SystemSummary? summary = null) => Task.Run(() => CollectDetails(summary));

    public PcDetails CollectDetails(SystemSummary? known = null)
    {
        var summary = known ?? Collect();
        var cpu = Safe(() => Query("Win32_Processor", "Manufacturer", "SocketDesignation", "CurrentClockSpeed", "VirtualizationFirmwareEnabled", "L2CacheSize", "L3CacheSize").FirstOrDefault(), null);
        bool hypervisor = Safe(() => Query("Win32_ComputerSystem", "HypervisorPresent").Select(r => r[0] is true).FirstOrDefault(), false);
        var caches = Safe(() => PcDetailsLogic.GroupCaches(Query("Win32_CacheMemory", "Level", "Purpose", "InstalledSize")
            .Select(r => (PcDetailsLogic.CacheLevel(ToInt(r[0]), Str(r[1])), ToULong(r[2])))), []);
        if (caches.Count == 0 && cpu is not null)
            caches = PcDetailsLogic.GroupCaches([(2, ToULong(cpu[4])), (3, ToULong(cpu[5]))]);

        var board = Safe(() => Query("Win32_BaseBoard", "Manufacturer", "Product", "Version", "SerialNumber").FirstOrDefault(), null);
        var bios = Safe(() => Query("Win32_BIOS", "SMBIOSBIOSVersion", "ReleaseDate", "SerialNumber").FirstOrDefault(), null);
        string product = board is null ? string.Empty : Str(board[1]);
        var pci = Safe(() => Query("Win32_PnPEntity WHERE PNPClass = 'System'", "Name").Select(r => Str(r[0])).ToList(), new List<string>());
        string? chipset = PcDetailsLogic.InferChipset(product, pci);
        string vendor = cpu is null ? string.Empty : Str(cpu[0]).Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD" : Str(cpu[0]).Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : string.Empty;

        var arrays = Safe(() => Query("Win32_PhysicalMemoryArray", "Use", "MemoryDevices", "MaxCapacityEx", "MaxCapacity")
            .Select(r => (ToInt(r[0]), ToInt(r[1]), ToULong(r[2]) > 0 ? ToULong(r[2]) : ToULong(r[3]))).ToList(), []);
        var memorySerials = Safe(() => Query("Win32_PhysicalMemory", "SerialNumber").Select(r => Str(r[0])).ToList(), new List<string>());

        return new PcDetails(
            summary,
            cpu is null ? string.Empty : Str(cpu[0]),
            cpu is null ? string.Empty : Str(cpu[1]),
            cpu is null ? 0 : ToInt(cpu[2]),
            PcDetailsLogic.VirtualizationText(cpu?[3] as bool?, hypervisor),
            caches,
            board is null ? string.Empty : Str(board[0]),
            product,
            board is null ? string.Empty : Str(board[2]),
            board is null ? string.Empty : Str(board[3]),
            bios is null ? string.Empty : Str(bios[0]),
            bios is null ? string.Empty : FormatWmiDate(bios[1]),
            bios is null ? string.Empty : Str(bios[2]),
            chipset is null ? "não identificado" : $"{(vendor.Length > 0 ? vendor + " " : string.Empty)}{chipset} (inferido pelo nome da placa/PCI)",
            PcDetailsLogic.ComputeSlots(arrays, summary.Memory),
            Safe(ReadDiskDetails, []),
            memorySerials);
    }

    private static IReadOnlyList<DiskDetail> ReadDiskDetails()
    {
        var list = new List<DiskDetail>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
            "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus, SerialNumber FROM MSFT_PhysicalDisk");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
                list.Add(new DiskDetail(Str(mo["FriendlyName"]), DiskKind(ToInt(mo["MediaType"]), ToInt(mo["BusType"])), ToULong(mo["Size"]),
                    PcDetailsLogic.HealthText(mo["HealthStatus"] is null ? -1 : ToInt(mo["HealthStatus"])), Str(mo["SerialNumber"])));
        }
        return list;
    }
}
