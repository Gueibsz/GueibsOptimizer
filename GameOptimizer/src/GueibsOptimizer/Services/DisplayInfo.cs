using System.Runtime.InteropServices;

namespace GueibsOptimizer.Services;

public sealed record MonitorMode(string DeviceName, int Width, int Height, int CurrentHz, int MaxHzAtResolution, bool Primary);

/// <summary>Taxa de atualização atual e máxima de cada monitor (EnumDisplaySettings).</summary>
public static class DisplayInfo
{
    private const int EnumCurrentSettings = -1;

    public static IReadOnlyList<MonitorMode> Monitors()
    {
        var list = new List<MonitorMode>();
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var current = NewDevMode();
                if (!EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref current)) continue;

                int max = (int)current.dmDisplayFrequency;
                var mode = NewDevMode();
                for (int i = 0; EnumDisplaySettings(screen.DeviceName, i, ref mode); i++)
                {
                    if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight)
                        max = Math.Max(max, (int)mode.dmDisplayFrequency);
                }
                list.Add(new MonitorMode(screen.DeviceName, (int)current.dmPelsWidth, (int)current.dmPelsHeight,
                    (int)current.dmDisplayFrequency, max, screen.Primary));
            }
        }
        catch (Exception ex) when (ex is ExternalException or EntryPointNotFoundException)
        {
        }
        return list;
    }

    private static DevMode NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);
}
