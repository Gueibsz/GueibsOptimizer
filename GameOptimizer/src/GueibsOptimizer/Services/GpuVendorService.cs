using System.Management;

namespace GueibsOptimizer.Services;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}

public sealed record GpuAdapter(string Name, GpuVendor Vendor);

/// <summary>Detecta as placas de vídeo via WMI (resultado em cache; falhas viram lista vazia).</summary>
public static class GpuVendorService
{
    private static readonly Lazy<IReadOnlyList<GpuAdapter>> Cached = new(Query);

    public static IReadOnlyList<GpuAdapter> Adapters => Cached.Value;

    public static GpuVendor Classify(string text)
    {
        var t = text.ToUpperInvariant();
        if (t.Contains("NVIDIA")) return GpuVendor.Nvidia;
        if (t.Contains("AMD") || t.Contains("ADVANCED MICRO DEVICES") || t.Contains("RADEON")) return GpuVendor.Amd;
        if (t.Contains("INTEL")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    public static string VendorText(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "desconhecido"
    };

    private static IReadOnlyList<GpuAdapter> Query()
    {
        var list = new List<GpuAdapter>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString() ?? "GPU";
                    var compat = mo["AdapterCompatibility"]?.ToString() ?? string.Empty;
                    if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new GpuAdapter(name, Classify(compat + " " + name)));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return list;
    }
}
