using System.Globalization;
using System.Text.RegularExpressions;

namespace GueibsOptimizer.Services;

/// <summary>
/// Leitura do CSV do PresentMon. O nome da coluna de frame time muda entre versões
/// (MsBetweenPresents no 1.x/2.0, FrameTime no 2.1+), então ela é detectada pelo cabeçalho.
/// </summary>
public sealed class PresentMonCsv
{
    public static readonly string[] FrameTimeColumns = ["MsBetweenPresents", "FrameTime", "msBetweenPresents", "MsBetweenAppStart"];

    private int _frameIndex = -1;
    private int _pidIndex = -1;
    private int _swapChainIndex = -1;
    private int _columns;

    // Um processo pode ter várias swapchains (ex.: launcher + jogo, ou o DWM); misturá-las distorce o FPS.
    // Seguimos só a que mais apresentou quadros até agora.
    private readonly Dictionary<string, int> _swapChainCounts = [];
    private string? _leader;

    public bool HasHeader => _frameIndex >= 0;
    public string? FrameColumn { get; private set; }

    /// <summary>Processa uma linha; retorna o frame time (ms) ou null se não for um frame válido.</summary>
    public double? ParseLine(string line, int? processId = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var cells = line.Split(',');

        if (!HasHeader)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                var name = cells[i].Trim().Trim('"');
                if (_frameIndex < 0 && FrameTimeColumns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    _frameIndex = i;
                    FrameColumn = name;
                }
                if (name.Equals("ProcessID", StringComparison.OrdinalIgnoreCase)) _pidIndex = i;
                if (name.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase)) _swapChainIndex = i;
            }
            _columns = cells.Length;
            return null;
        }

        if (cells.Length < _columns || _frameIndex >= cells.Length) return null;
        if (processId is { } pid && _pidIndex >= 0 &&
            (!int.TryParse(cells[_pidIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowPid) || rowPid != pid))
            return null;

        if (!double.TryParse(cells[_frameIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
            return null;

        if (_swapChainIndex >= 0)
        {
            var swapChain = cells[_swapChainIndex];
            int count = _swapChainCounts[swapChain] = _swapChainCounts.GetValueOrDefault(swapChain) + 1;
            if (_leader is null || (swapChain != _leader && count > _swapChainCounts[_leader])) _leader = swapChain;
            if (swapChain != _leader) return null;
        }
        return ms;
    }

    public static Version? ParseVersion(string text)
    {
        var m = Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>
    /// Argumentos por versão: o 2.x usa "--opcao", o 1.x usa "-opcao". Captura pelo NOME do executável
    /// (não pelo PID da janela): navegadores, apps Electron e alguns jogos apresentam os quadros
    /// em um processo filho com o mesmo nome (ex.: o processo de GPU do Chrome/Brave).
    /// </summary>
    public static IReadOnlyList<string> CaptureArguments(Version? version, string processName)
    {
        string p = version is { Major: >= 2 } || version is null ? "--" : "-";
        var exe = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName : processName + ".exe";
        var args = new List<string>
        {
            $"{p}process_name", exe,
            $"{p}output_stdout",
            $"{p}stop_existing_session",
            $"{p}session_name", "GueibsOptimizerFps",
            $"{p}terminate_on_proc_exit"
        };
        return args;
    }
}
