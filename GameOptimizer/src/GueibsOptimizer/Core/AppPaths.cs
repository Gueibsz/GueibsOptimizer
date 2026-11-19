using System.IO;

namespace GueibsOptimizer.Core;

/// <summary>Caminhos de dados do app em %AppData%\GueibsOptimizer.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GueibsOptimizer");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string BackupFile => Path.Combine(DataDirectory, "backup.json");

    /// <summary>Pasta onde o executável está (usada para a exclusão do Defender).</summary>
    public static string AppDirectory => AppContext.BaseDirectory;
}
