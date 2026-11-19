using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

/// <summary>
/// FPS via PresentMon (eventos ETW). Nada é injetado no jogo: o PresentMon só lê eventos do sistema.
/// Captura o processo em primeiro plano e reinicia a captura quando ele muda.
/// </summary>
public sealed class FpsService : IDisposable
{
    public const string DownloadPage = "https://github.com/GameTechDev/PresentMon/releases";

    private readonly Logger _logger;
    private readonly AppSettings _settings;
    private readonly FrameStats _stats = new();
    private readonly int _ownPid = Environment.ProcessId;
    private Process? _process;
    private Timer? _watch;
    private int _currentPid;
    private Version? _version;

    public FpsService(Logger logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public bool IsRunning => _watch is not null;
    public string? ExecutablePath { get; private set; }
    public string Status { get; private set; } = "Parado.";
    public string? TargetName { get; private set; }

    public FpsSnapshot Snapshot() => _stats.Snapshot();

    /// <summary>Caminho configurado pelo usuário ou instalações conhecidas do PresentMon.</summary>
    public string? Locate()
    {
        if (!string.IsNullOrEmpty(_settings.PresentMonPath) && File.Exists(_settings.PresentMonPath))
            return _settings.PresentMonPath;

        var roots = new[]
        {
            PresentMonDownloader.ToolsDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages")
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var exe = Directory.EnumerateFiles(root, "PresentMon*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 })
                    // O app com interface (PresentMonApplication) e o serviço não aceitam argumentos de console.
                    .Where(f => !f.Contains("Service", StringComparison.OrdinalIgnoreCase) &&
                                !f.Contains("Application", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
                if (exe is not null) return exe;
            }
            catch (IOException) { }
        }
        return null;
    }

    /// <summary>Primeiro passo: descobrir a versão instalada (define o formato dos argumentos).</summary>
    public async Task<Version?> DetectVersionAsync(string exe)
    {
        var shell = new Shell(_logger, dryRun: false); // apenas leitura da versão
        var r = await shell.RunAsync(exe, ["--version"], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var v = PresentMonCsv.ParseVersion(r.StdOut + " " + r.StdErr);
        if (v is null)
        {
            var help = await shell.RunAsync(exe, ["-?"], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            v = PresentMonCsv.ParseVersion(help.StdOut + " " + help.StdErr);
        }
        return v;
    }

    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;
        ExecutablePath = Locate();
        if (ExecutablePath is null)
        {
            Status = "PresentMon não encontrado. Aponte o PresentMon.exe (versão de console).";
            return false;
        }
        _version = await DetectVersionAsync(ExecutablePath).ConfigureAwait(false);
        _logger.Info($"PresentMon {(_version?.ToString() ?? "versão desconhecida")} em {ExecutablePath}");
        _watch = new Timer(_ => CheckForeground(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Status = "Aguardando um jogo em primeiro plano...";
        return true;
    }

    public void Stop()
    {
        _watch?.Dispose();
        _watch = null;
        StopCapture();
        _stats.Clear();
        Status = "Parado.";
        TargetName = null;
    }

    private void CheckForeground()
    {
        try
        {
            int pid = ForegroundProcessId();
            if (pid == 0 || pid == _ownPid || pid == _currentPid) return;
            using var target = Process.GetProcessById(pid);
            string name = target.ProcessName;
            _currentPid = pid;
            // Outra janela do mesmo programa: a captura por nome já cobre.
            if (_process is not null && string.Equals(name, TargetName, StringComparison.OrdinalIgnoreCase)) return;
            StopCapture();
            _currentPid = pid;
            _stats.Clear();
            TargetName = name;
            StartCapture(name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // processo fechou entre a leitura e a captura
        }
    }

    private void StartCapture(string processName)
    {
        if (ExecutablePath is null) return;
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in PresentMonCsv.CaptureArguments(_version, processName)) psi.ArgumentList.Add(a);

        try
        {
            var process = Process.Start(psi);
            if (process is null) return;
            _process = process;
            var csv = new PresentMonCsv();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                bool hadHeader = csv.HasHeader;
                // Sem filtro de PID: todos os processos com esse nome entram, e o parser segue a swapchain principal.
                if (csv.ParseLine(e.Data) is { } ms) _stats.Add(ms);
                if (!hadHeader && csv.HasHeader)
                    Status = csv.FrameColumn is { } col
                        ? $"Capturando {TargetName} (coluna {col})."
                        : "O PresentMon respondeu em um formato desconhecido (sem coluna de tempo de quadro).";
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Warn($"PresentMon: {e.Data}");
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Status = $"Capturando {TargetName}...";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Falha ao iniciar o PresentMon: {ex.Message}";
            _logger.Error("Falha ao iniciar o PresentMon", ex);
        }
    }

    private void StopCapture()
    {
        var p = _process;
        _process = null;
        _currentPid = 0;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    private static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
