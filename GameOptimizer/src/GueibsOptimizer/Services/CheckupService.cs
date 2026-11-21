using System.Globalization;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;
using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

public enum CheckStatus
{
    Ok,
    Attention,
    Error
}

public sealed record CheckItem(string Title, CheckStatus Status, string Detail, string HowToFix)
{
    public string StatusText => Status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Attention => "Atenção",
        _ => "Erro"
    };
}

public sealed record PcieLink(string Device, int CurrentGen, int MaxGen, int CurrentWidth, int MaxWidth);

/// <summary>Análises puras do check-up (testadas). Todas as heurísticas são rotuladas como estimativa.</summary>
public static partial class CheckupRules
{
    [GeneratedRegex(@"(?:CHANNEL|CH)\s*([A-H])|DIMM_?([A-H])\d|([A-H])\d\b|BANK\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    public static string? ChannelOf(MemoryModule m)
    {
        foreach (var text in new[] { m.Locator, m.Bank })
        {
            var match = ChannelPattern().Match(text ?? string.Empty);
            if (!match.Success) continue;
            for (int g = 1; g < match.Groups.Count; g++)
                if (match.Groups[g].Success) return match.Groups[g].Value.ToUpperInvariant();
        }
        return null;
    }

    public static CheckItem DualChannel(IReadOnlyList<MemoryModule> modules)
    {
        const string title = "Dual Channel (estimativa)";
        if (modules.Count == 0)
            return new(title, CheckStatus.Attention, "Não foi possível ler os pentes de memória.", "Confira no CPU-Z ou na BIOS.");
        if (modules.Count == 1)
            return new(title, CheckStatus.Attention, "Apenas 1 pente instalado: a memória funciona em canal único (single channel).",
                "Use 2 pentes iguais nos slots indicados no manual da placa-mãe (geralmente A2 e B2) para dobrar a largura de banda.");

        var channels = modules.Select(ChannelOf).Where(c => c is not null).Distinct().Count();
        bool sameSize = modules.Select(m => m.CapacityBytes).Distinct().Count() == 1;

        if (channels == 1)
            return new(title, CheckStatus.Attention, "Os pentes parecem estar no mesmo canal.",
                "Mova um dos pentes para o slot do outro canal (veja o manual da placa-mãe).");
        if (!sameSize)
            return new(title, CheckStatus.Attention, $"{modules.Count} pentes de tamanhos diferentes: provável modo flex (só parte da memória em dual channel).",
                "Para o melhor desempenho, use pentes de mesma capacidade.");

        string basis = channels >= 2 ? "em canais diferentes" : "em número par e com o mesmo tamanho";
        return new(title, CheckStatus.Ok, $"Provável dual channel: {modules.Count} pentes iguais {basis}. É uma estimativa: o Windows não informa o modo real.",
            "Nada a fazer.");
    }

    public static CheckItem Xmp(IReadOnlyList<MemoryModule> modules)
    {
        const string title = "XMP / EXPO (estimativa)";
        var m = modules.FirstOrDefault(x => x.ConfiguredMts > 0);
        if (m is null)
            return new(title, CheckStatus.Attention, "O Windows não informou a velocidade configurada da memória.", "Confira na BIOS se o perfil XMP/EXPO está ativo.");

        int nominal = modules.Max(x => x.SpeedMts);
        int configured = m.ConfiguredMts;
        // Velocidades padrão JEDEC típicas: acima disso quase sempre é um perfil XMP/EXPO.
        int jedecLimit = m.SmbiosType switch { 34 or 35 => 4800, 26 => 2666, 24 => 1600, _ => 0 };

        if (nominal > 0 && configured < nominal * 0.95)
            return new(title, CheckStatus.Attention,
                $"A memória está em {configured} MT/s, abaixo da velocidade nominal de {nominal} MT/s. Provavelmente o XMP/EXPO está desligado.",
                "Entre na BIOS e ative o perfil XMP (Intel) ou EXPO/DOCP (AMD). Isso não é um ajuste do Windows.");
        if (jedecLimit > 0 && configured <= jedecLimit)
            return new(title, CheckStatus.Attention,
                $"A memória está em {configured} MT/s, uma velocidade padrão (JEDEC). Se seus pentes forem mais rápidos, o XMP/EXPO pode estar desligado.",
                "Confira a velocidade na etiqueta dos pentes e ative o XMP/EXPO na BIOS se for maior.");
        return new(title, CheckStatus.Ok, $"Memória a {configured} MT/s (nominal {nominal} MT/s). Parece estar no perfil de fábrica. Estimativa: o Windows não informa o XMP diretamente.",
            "Nada a fazer.");
    }

    public static CheckItem Pcie(IReadOnlyList<PcieLink> links)
    {
        const string title = "Link PCIe da placa de vídeo";
        var link = links.FirstOrDefault(l => l.MaxGen > 0 || l.MaxWidth > 0);
        if (link is null)
            return new(title, CheckStatus.Attention, "Não foi possível ler o link PCIe (comum em GPUs integradas).", "Nada a fazer se a GPU for integrada.");

        string text = $"{link.Device}: PCIe {link.CurrentGen}.0 x{link.CurrentWidth} (máximo PCIe {link.MaxGen}.0 x{link.MaxWidth}).";
        if (link.CurrentWidth > 0 && link.MaxWidth > 0 && link.CurrentWidth < link.MaxWidth)
            return new(title, CheckStatus.Attention, text + " A largura está abaixo do máximo.",
                "Verifique se a placa está no slot principal (o mais próximo da CPU) e bem encaixada. Algumas GPUs são x8 por projeto.");
        if (link.CurrentGen > 0 && link.CurrentGen < link.MaxGen)
            return new(title, CheckStatus.Attention, text + " A geração atual está abaixo da máxima; em repouso isso é normal (economia de energia).",
                "Meça de novo com um jogo aberto. Se continuar baixo, verifique o slot e a configuração PCIe da BIOS.");
        return new(title, CheckStatus.Ok, text, "Nada a fazer.");
    }

    public static CheckItem RefreshRate(IReadOnlyList<MonitorMode> monitors)
    {
        const string title = "Taxa de atualização do monitor";
        if (monitors.Count == 0)
            return new(title, CheckStatus.Attention, "Não foi possível ler os monitores.", "Confira em Configurações > Sistema > Tela > Tela avançada.");
        var low = monitors.Where(m => m.CurrentHz < m.MaxHzAtResolution).ToList();
        if (low.Count == 0)
            return new(title, CheckStatus.Ok, string.Join("; ", monitors.Select(m => $"{m.Width}x{m.Height} a {m.CurrentHz} Hz (máximo)")), "Nada a fazer.");
        return new(title, CheckStatus.Attention,
            string.Join("; ", low.Select(m => $"{m.Width}x{m.Height} está a {m.CurrentHz} Hz, mas suporta {m.MaxHzAtResolution} Hz")),
            "Abra Configurações > Sistema > Tela > Tela avançada e escolha a maior taxa de atualização. Use cabo DisplayPort/HDMI adequado.");
    }

    public static CheckItem FromTweak(string title, ITweak? tweak, string okText, string fixText)
    {
        if (tweak is null) return new(title, CheckStatus.Error, "Ajuste não encontrado.", fixText);
        try
        {
            if (!tweak.IsApplicable()) return new(title, CheckStatus.Ok, "Não aplicável nesta versão do Windows.", "Nada a fazer.");
            return tweak.IsApplied()
                ? new(title, CheckStatus.Ok, okText, "Nada a fazer.")
                : new(title, CheckStatus.Attention, $"\"{tweak.Name}\" não está aplicado.", fixText);
        }
        catch (Exception ex)
        {
            return new(title, CheckStatus.Error, $"Não foi possível verificar: {ex.Message}", fixText);
        }
    }

    // Páginas oficiais de drivers (só abertas/indicadas; o app não baixa drivers de vídeo nem de chipset).
    public const string NvidiaDriversUrl = "https://www.nvidia.com/Download/index.aspx";
    public const string AmdDriversUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDriversUrl = "https://www.intel.com/content/www/us/en/support/detect.html";

    public static string? DriversUrl(HardwareVendor vendor) => vendor switch
    {
        HardwareVendor.Nvidia => NvidiaDriversUrl,
        HardwareVendor.Amd => AmdDriversUrl,
        HardwareVendor.Intel => IntelDriversUrl,
        _ => null
    };

    private static string VendorName(HardwareVendor vendor) => vendor switch
    {
        HardwareVendor.Nvidia => "NVIDIA",
        HardwareVendor.Amd => "AMD",
        HardwareVendor.Intel => "Intel",
        _ => "fabricante"
    };

    /// <summary>Driver da placa de vídeo principal (NVIDIA, AMD ou Intel): mais de 6 meses vira "Atenção".</summary>
    public static CheckItem GpuDriver(IReadOnlyList<GpuInfo> gpus, DateTime today)
    {
        const string title = "Driver da placa de vídeo";
        var main = gpus.Where(g => !g.IsVirtual)
            .Select(g => (Info: g, Id: HardwareNameParser.ParseGpu(g.Name)))
            .OrderBy(p => p.Id.IsIntegrated)
            .ThenByDescending(p => p.Id.PerformanceIndex)
            .FirstOrDefault();
        if (main.Info is null)
            return new(title, CheckStatus.Attention, "Não foi possível ler a placa de vídeo.",
                "Confira no Gerenciador de Dispositivos > Adaptadores de vídeo.");

        var url = DriversUrl(main.Id.Vendor);
        string fix = url is null
            ? "Atualize pelo Windows Update ou pelo site do fabricante da placa."
            : $"Baixe o driver mais recente na página oficial da {VendorName(main.Id.Vendor)}: {url}";
        string name = main.Info.Name;
        string version = string.IsNullOrWhiteSpace(main.Info.Driver) ? "—" : main.Info.Driver;

        if (!TryParseDriverDate(main.Info.DriverDate, out var date))
            return new(title, CheckStatus.Attention,
                $"{name}: driver {version}, data desconhecida.", fix);

        int months = (int)((today - date).TotalDays / 30.44);
        if (months >= 6)
            return new(title, CheckStatus.Attention,
                $"{name}: driver {version} de {date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}, com cerca de {months} meses. Drivers novos trazem correções e otimizações para jogos recentes.",
                fix);
        return new(title, CheckStatus.Ok,
            $"{name}: driver {version} de {date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.",
            "Nada a fazer.");
    }

    public static bool TryParseDriverDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text, ["dd/MM/yyyy", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// Driver de chipset da AMD (só em processadores AMD). Estimativa: procura drivers do sistema cujo fornecedor é a AMD;
    /// os drivers genéricos que vêm com o Windows têm a Microsoft como fornecedora.
    /// </summary>
    public static CheckItem? AmdChipset(CpuIdentity cpu, IReadOnlyList<string> amdSystemDrivers)
    {
        if (cpu.Vendor != HardwareVendor.Amd) return null;
        const string title = "Driver de chipset AMD (estimativa)";
        if (amdSystemDrivers.Count > 0)
            return new(title, CheckStatus.Ok,
                $"Drivers de chipset da AMD encontrados ({amdSystemDrivers.Count}), por exemplo: {amdSystemDrivers[0]}.",
                $"Para atualizar, use a página oficial da AMD: {AmdDriversUrl}");
        return new(title, CheckStatus.Attention,
            cpu.IsX3D
                ? "Nenhum driver de chipset da AMD encontrado: o Windows parece usar os drivers genéricos. Nos Ryzen X3D ele é essencial para os jogos usarem o cache 3D."
                : "Nenhum driver de chipset da AMD encontrado: o Windows parece usar os drivers genéricos. No Ryzen, o driver de chipset traz o gerenciamento de energia e o agendamento corretos.",
            $"Instale o AMD Chipset Software pela página oficial da AMD ({AmdDriversUrl}): escolha Chipsets e o soquete da sua placa-mãe.");
    }

    /// <summary>Converte o valor do DEVPKEY de velocidade de link (1 = 2.5 GT/s ... 5 = 32 GT/s) em geração.</summary>
    public static int GenFromLinkSpeed(int value) => value is >= 1 and <= 6 ? value : 0;
}

public sealed class CheckupService(TweakRegistry registry, SystemInfoService info, Shell shell, Logger logger)
{
    public async Task<IReadOnlyList<CheckItem>> RunAsync()
    {
        var memory = await Task.Run(() =>
        {
            try { return info.ReadMemory(); }
            catch (Exception ex) { logger.Warn($"Memória: {ex.Message}"); return (IReadOnlyList<MemoryModule>)[]; }
        });
        var links = await ReadPcieAsync();
        var monitors = await Task.Run(DisplayInfo.Monitors);
        var (gpus, cpu, amdDrivers) = await Task.Run(() =>
        {
            IReadOnlyList<GpuInfo> g = [];
            CpuIdentity c = HardwareNameParser.ParseCpu(string.Empty);
            IReadOnlyList<string> d = [];
            try { g = info.ReadGpus(); } catch (Exception ex) { logger.Warn($"GPU: {ex.Message}"); }
            try { c = HardwareNameParser.ParseCpu(info.ReadCpuName()); } catch (Exception ex) { logger.Warn($"CPU: {ex.Message}"); }
            if (c.Vendor == HardwareVendor.Amd)
                try { d = info.ReadAmdSystemDrivers(); } catch (Exception ex) { logger.Warn($"Drivers AMD: {ex.Message}"); }
            return (g, c, d);
        });
        var amdChipset = CheckupRules.AmdChipset(cpu, amdDrivers);

        var tweakChecks = await Task.Run(() => new[]
        {
            CheckupRules.FromTweak("Plano de energia", registry.Find("power.plan"), "Plano de alto desempenho ativo.",
                "Aplique \"Plano de energia de alto desempenho\" em Otimizações."),
            CheckupRules.FromTweak("Modo de Jogo", registry.Find("gaming.game-mode"), "Modo de Jogo e prioridade multimídia ativos.",
                "Aplique \"Modo de Jogo e prioridade multimídia\" em Otimizações."),
            CheckupRules.FromTweak("HAGS", registry.Find("gpu.hags"), "Agendamento de GPU acelerado por hardware ligado.",
                "Aplique \"HAGS\" em Otimizações e reinicie o PC.")
        });

        return
        [
            CheckupRules.DualChannel(memory),
            CheckupRules.Xmp(memory),
            CheckupRules.Pcie(links),
            CheckupRules.GpuDriver(gpus, DateTime.Today),
            .. (amdChipset is null ? Array.Empty<CheckItem>() : [amdChipset]),
            .. tweakChecks,
            CheckupRules.RefreshRate(monitors)
        ];
    }

    private async Task<IReadOnlyList<PcieLink>> ReadPcieAsync()
    {
        const string script = """
            Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'PCI\*' } | ForEach-Object {
              $d = $_
              $vals = @{}
              Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_PciDevice_CurrentLinkSpeed,DEVPKEY_PciDevice_MaxLinkSpeed,DEVPKEY_PciDevice_CurrentLinkWidth,DEVPKEY_PciDevice_MaxLinkWidth -ErrorAction SilentlyContinue |
                ForEach-Object { $vals[$_.KeyName] = $_.Data }
              "{0}|{1}|{2}|{3}|{4}" -f $d.FriendlyName, $vals['DEVPKEY_PciDevice_CurrentLinkSpeed'], $vals['DEVPKEY_PciDevice_MaxLinkSpeed'], $vals['DEVPKEY_PciDevice_CurrentLinkWidth'], $vals['DEVPKEY_PciDevice_MaxLinkWidth']
            }
            """;
        var r = await shell.RunPowerShellAsync(script, TimeSpan.FromSeconds(30));
        return r.Success ? ParsePcie(r.StdOut) : [];
    }

    public static IReadOnlyList<PcieLink> ParsePcie(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Where(p => p.Length == 5)
            .Select(p => new PcieLink(p[0],
                CheckupRules.GenFromLinkSpeed(ParseInt(p[1])), CheckupRules.GenFromLinkSpeed(ParseInt(p[2])),
                ParseInt(p[3]), ParseInt(p[4])))
            .ToList();

    private static int ParseInt(string s) => int.TryParse(s.Trim(), out var v) ? v : 0;
}
