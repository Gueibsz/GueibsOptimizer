using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Views;

/// <summary>Mede o polling rate com Raw Input: cada WM_INPUT do mouse recebe um timestamp do Stopwatch.</summary>
public partial class PollingTestWindow : Window
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RidevInputSink = 0x00000100;

    private readonly PollingCalculator _calc = new(Stopwatch.Frequency);
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private HwndSource? _source;

    public PollingTestWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        _ui.Tick += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            _ui.Stop();
            _source?.RemoveHook(Hook);
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
        var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevInputSink, Target = hwnd };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            NearestText.Text = "Não foi possível registrar o Raw Input do mouse.";
        _ui.Start();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput) return IntPtr.Zero;
        long now = Stopwatch.GetTimestamp();
        uint size = (uint)Marshal.SizeOf<RawInputHeader>();
        var header = new RawInputHeader();
        if (GetRawInputData(lParam, RidInputHeader, ref header, ref size, (uint)Marshal.SizeOf<RawInputHeader>()) != uint.MaxValue &&
            header.Type == RimTypeMouse)
        {
            _calc.Add(now);
        }
        return IntPtr.Zero;
    }

    private const uint RidInputHeader = 0x10000005;

    private void Refresh()
    {
        if (_calc.Events == 0) return;
        CurrentText.Text = $"{_calc.CurrentHz:0} Hz";
        AverageText.Text = $"{_calc.AverageHz:0} Hz";
        MaxText.Text = $"{_calc.MaxHz:0} Hz";
        int nearest = PollingCalculator.NearestStandard(_calc.AverageHz);
        NearestText.Text = nearest == 0 ? "Mova o mouse para começar." : $"Taxa provável configurada: {nearest} Hz";
        EventsText.Text = $"{_calc.Events} leituras recebidas";
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _calc.Reset();
        CurrentText.Text = AverageText.Text = MaxText.Text = "— Hz";
        NearestText.Text = "Mova o mouse para começar.";
        EventsText.Text = string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, ref RawInputHeader data, ref uint size, uint headerSize);
}
