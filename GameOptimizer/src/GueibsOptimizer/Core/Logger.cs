using System.IO;
using System.Text;

namespace GueibsOptimizer.Core;

public enum LogLevel
{
    Info,
    Warning,
    Error,
    /// <summary>Ação que seria executada, mas foi apenas simulada (--dry-run).</summary>
    Simulation
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Warning => "AVISO",
        LogLevel.Error => "ERRO",
        LogLevel.Simulation => "SIMULAÇÃO",
        _ => Level.ToString()
    };

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{LevelText}] {Message}";
}

/// <summary>
/// Log em arquivo com rotação por tamanho, mais evento para a interface.
/// Falhas de escrita no arquivo nunca propagam exceção.
/// </summary>
public sealed class Logger
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxFiles = 5;
    private const string FileName = "gueibsoptimizer.log";

    private readonly string? _directory;
    private readonly object _sync = new();

    public event Action<LogEntry>? EntryLogged;

    /// <param name="directory">Pasta dos logs; null desativa a gravação em arquivo (útil em testes).</param>
    public Logger(string? directory)
    {
        _directory = directory;
        if (_directory is null) return;
        try { Directory.CreateDirectory(_directory); }
        catch { _directory = null; }
    }

    public string? LogDirectory => _directory;

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Simulated(string message) => Write(LogLevel.Simulation, message);

    public void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        if (_directory is not null)
        {
            lock (_sync)
            {
                try
                {
                    var path = Path.Combine(_directory, FileName);
                    RotateIfNeeded(path);
                    File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Log nunca pode derrubar o app.
                }
            }
        }

        try { EntryLogged?.Invoke(entry); }
        catch { /* assinante com erro não afeta o log */ }
    }

    private void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        for (int i = MaxFiles - 1; i >= 1; i--)
        {
            var src = $"{path}.{i}";
            var dst = $"{path}.{i + 1}";
            if (!File.Exists(src)) continue;
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
        File.Move(path, $"{path}.1", overwrite: true);
    }
}
