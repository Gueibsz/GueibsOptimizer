using GueibsOptimizer.Models;

namespace GueibsOptimizer.Services;

/// <summary>
/// Classificação do PC em Entrada / Médio / Alto / Topo. Regras (nesta ordem):
/// 1. Nível da GPU pelo índice de desempenho do <see cref="HardwareNameParser"/>:
///    integrada → Entrada; índice &lt; 3 → Entrada; &lt; 5 → Médio; &lt; 7 → Alto; ≥ 7 → Topo.
/// 2. Limites pela CPU (threads): ≤ 2 → Entrada; ≤ 4 → no máximo Médio; &lt; 12 → no máximo Alto.
/// 3. Limites pela RAM: &lt; 8 GB → Entrada; &lt; 16 GB → no máximo Médio.
/// O nível final é o menor entre GPU e limites, porque jogos travam no componente mais fraco.
/// </summary>
public static class PcTierClassifier
{
    public const double GiB = 1024.0 * 1024 * 1024;

    public static PcTier GpuTier(GpuIdentity gpu) =>
        gpu.IsIntegrated ? PcTier.Entrada
        : gpu.PerformanceIndex < 3 ? PcTier.Entrada
        : gpu.PerformanceIndex < 5 ? PcTier.Medio
        : gpu.PerformanceIndex < 7 ? PcTier.Alto
        : PcTier.Topo;

    public static (PcTier Tier, IReadOnlyList<string> Reasons) Classify(GpuIdentity gpu, int threads, ulong ramBytes)
    {
        var reasons = new List<string>();
        var tier = GpuTier(gpu);
        reasons.Add(gpu.IsIntegrated
            ? "Vídeo integrado: adequado para jogos leves e competitivos em configurações baixas."
            : $"Placa de vídeo {gpu.FriendlyName}: nível {Text(tier)} (índice {gpu.PerformanceIndex:0.#}).");

        PcTier cpuCap = threads <= 2 ? PcTier.Entrada : threads <= 4 ? PcTier.Medio : threads < 12 ? PcTier.Alto : PcTier.Topo;
        if (cpuCap < tier)
        {
            tier = cpuCap;
            reasons.Add($"Processador com {threads} threads limita o nível a {Text(cpuCap)} (jogos atuais usam 6 a 8 núcleos).");
        }

        double gb = ramBytes / GiB;
        PcTier ramCap = gb < 7.5 ? PcTier.Entrada : gb < 15 ? PcTier.Medio : PcTier.Topo;
        if (ramCap < tier)
        {
            tier = ramCap;
            reasons.Add($"{gb:0} GB de RAM limitam o nível a {Text(ramCap)} (16 GB é o recomendado hoje).");
        }
        return (tier, reasons);
    }

    public static string Text(PcTier tier) => tier switch
    {
        PcTier.Entrada => "Entrada",
        PcTier.Medio => "Médio",
        PcTier.Alto => "Alto",
        _ => "Topo"
    };

    /// <summary>Dicas informativas conforme o hardware. Nada é aplicado automaticamente.</summary>
    public static IReadOnlyList<string> Recommendations(CpuIdentity cpu, int threads, GpuIdentity gpu, ulong? vramBytes,
        bool isLaptop, bool hybridGraphics, ulong ramBytes)
    {
        var list = new List<string>();
        if (cpu.IsX3D)
        {
            bool dualCcd = cpu.Model.StartsWith("79", StringComparison.Ordinal) || cpu.Model.StartsWith("99", StringComparison.Ordinal);
            list.Add(dualCcd
                ? "Ryzen X3D com dois CCDs: mantenha o driver de chipset AMD atualizado e o Modo de Jogo ligado, para os jogos rodarem no CCD com cache 3D."
                : "Ryzen X3D: o cache 3D rende mais com o driver de chipset AMD atualizado; não precisa de overclock de memória agressivo.");
        }
        if (cpu.IsHybridCores)
            list.Add("Processador com núcleos P e E: prefira o Windows 11 (Thread Director) e mantenha o Modo de Jogo ligado.");
        if (gpu.SupportsReflex)
            list.Add("NVIDIA: ative o NVIDIA Reflex (\"Ativado\" ou \"Ativado + Boost\") nos jogos que oferecem, como CS2, Valorant e Fortnite, para reduzir a latência.");
        if (gpu.Vendor == HardwareVendor.Amd && !gpu.IsIntegrated)
            list.Add("AMD Radeon: o Radeon Anti-Lag no Adrenalin reduz a latência em jogos competitivos.");
        if (cpu.Vendor == HardwareVendor.Amd && cpu.Brand.StartsWith("Ryzen", StringComparison.Ordinal))
            list.Add("AMD Ryzen: a velocidade da memória pesa mais no Ryzen (Infinity Fabric). Ative o perfil EXPO/DOCP na BIOS e mantenha o driver de chipset AMD atualizado (veja o Check-up).");
        if (gpu.Vendor == HardwareVendor.Amd && gpu.IsIntegrated)
            list.Add("Vídeo integrado Radeon: memória rápida em dual channel aumenta muito o FPS; em algumas placas dá para reservar mais memória de vídeo na BIOS (UMA Frame Buffer Size).");
        if (isLaptop && hybridGraphics)
            list.Add("Notebook com vídeo híbrido: em Meus Jogos, defina a GPU de alto desempenho para cada jogo e jogue na tomada.");
        else if (isLaptop)
            list.Add("Notebook: jogue na tomada e com o plano de energia de alto desempenho para não perder desempenho.");
        if (vramBytes is { } v && !gpu.IsIntegrated && v / PcTierClassifier.GiB < 6)
            list.Add($"Placa com {v / GiB:0} GB de VRAM: use texturas no médio ou baixo para evitar travadas.");
        if (threads <= 4)
            list.Add("Processador de 4 threads ou menos: feche navegador e programas em segundo plano antes de jogar.");
        if (ramBytes / GiB < 15)
            list.Add("Menos de 16 GB de RAM: ampliar a memória é a melhoria mais barata para jogos atuais.");
        if (gpu.IsIntegrated)
            list.Add("Vídeo integrado: use resolução menor ou escala de renderização (FSR/XeSS) e limite o FPS para manter estabilidade.");
        return list;
    }
}
