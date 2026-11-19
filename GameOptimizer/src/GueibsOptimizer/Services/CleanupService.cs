using System.IO;
using System.Runtime.InteropServices;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public enum CleanupKind
{
    UserTemp,
    WindowsTemp,
    ShaderCache,
    WindowsUpdate,
    RecycleBin
}

public sealed record CleanupCategory(CleanupKind Kind, string Name, string Description, IReadOnlyList<string> Paths, bool DefaultSelected);

public sealed record CleanupEstimate(CleanupCategory Category, long Bytes, int Files);

public sealed record CleanupResult(long FreedBytes, int DeletedFiles, int SkippedFiles, bool Simulated);

/// <summary>
/// Limpeza de temporários e caches. Não apaga Prefetch. Arquivos em uso são ignorados sem travar,
/// e junções/links simbólicos nunca são seguidos.
/// </summary>
public sealed class CleanupService(Shell shell, Logger logger, bool dryRun)
{
    public static IReadOnlyList<CleanupCategory> Categories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            new(CleanupKind.UserTemp, "Temporários do usuário", "Arquivos em %TEMP% deixados por programas.", [Path.GetTempPath()], true),
            new(CleanupKind.WindowsTemp, "Temporários do Windows", @"Arquivos em C:\Windows\Temp.", [Path.Combine(windows, "Temp")], true),
            new(CleanupKind.ShaderCache, "Cache de shaders", "DirectX (D3DSCache), NVIDIA e AMD. É recriado pelos jogos; a primeira partida pode ter travadinhas.",
                [
                    Path.Combine(local, "D3DSCache"),
                    Path.Combine(local, "NVIDIA", "DXCache"),
                    Path.Combine(local, "NVIDIA", "GLCache"),
                    Path.Combine(local, "AMD", "DxCache"),
                    Path.Combine(local, "AMD", "DxcCache"),
                    Path.Combine(local, "AMD", "VkCache")
                ], true),
            new(CleanupKind.WindowsUpdate, "Cache do Windows Update", "Downloads já instalados em SoftwareDistribution\\Download. O serviço é parado e reiniciado.",
                [Path.Combine(windows, "SoftwareDistribution", "Download")], false),
            new(CleanupKind.RecycleBin, "Lixeira", "Esvazia a lixeira de todas as unidades (não pode ser desfeito).", [], false)
        ];
    }

    public Task<IReadOnlyList<CleanupEstimate>> EstimateAsync(IEnumerable<CleanupCategory> categories) =>
        Task.Run<IReadOnlyList<CleanupEstimate>>(() => categories.Select(Estimate).ToList());

    public static CleanupEstimate Estimate(CleanupCategory category)
    {
        if (category.Kind == CleanupKind.RecycleBin)
        {
            var (bytes, items) = QueryRecycleBin();
            return new CleanupEstimate(category, bytes, (int)items);
        }
        long total = 0;
        int files = 0;
        foreach (var path in category.Paths)
            foreach (var file in EnumerateFilesSafe(path))
            {
                try { total += new FileInfo(file).Length; files++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return new CleanupEstimate(category, total, files);
    }

    public async Task<CleanupResult> CleanAsync(IEnumerable<CleanupCategory> categories, CancellationToken ct = default)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var category in categories)
        {
            ct.ThrowIfCancellationRequested();
            if (category.Kind == CleanupKind.RecycleBin)
            {
                var (bytes, items) = QueryRecycleBin();
                if (dryRun) { logger.Simulated($"Esvaziar lixeira ({MemoryCleanResult.FormatBytes((ulong)bytes)})"); continue; }
                if (EmptyRecycleBin()) { freed += bytes; deleted += (int)items; }
                continue;
            }

            bool stopUpdate = category.Kind == CleanupKind.WindowsUpdate;
            if (stopUpdate)
                await shell.RunMutatingAsync("Parar Windows Update", "net.exe", ["stop", "wuauserv"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            try
            {
                foreach (var path in category.Paths)
                {
                    var r = await Task.Run(() => CleanDirectory(path, dryRun, logger), ct).ConfigureAwait(false);
                    freed += r.FreedBytes; deleted += r.DeletedFiles; skipped += r.SkippedFiles;
                }
            }
            finally
            {
                if (stopUpdate)
                    await shell.RunMutatingAsync("Iniciar Windows Update", "net.exe", ["start", "wuauserv"], TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            }
        }
        logger.Info(dryRun
            ? $"Simulação de limpeza: {MemoryCleanResult.FormatBytes((ulong)freed)} seriam liberados."
            : $"Limpeza: {MemoryCleanResult.FormatBytes((ulong)freed)} liberados, {deleted} arquivos apagados, {skipped} em uso ignorados.");
        return new CleanupResult(freed, deleted, skipped, dryRun);
    }

    /// <summary>Apaga o conteúdo da pasta (não a pasta). Em uso/sem permissão = ignorado.</summary>
    public static CleanupResult CleanDirectory(string root, bool dryRun, Logger logger)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        if (!Directory.Exists(root)) return new CleanupResult(0, 0, 0, dryRun);

        foreach (var file in EnumerateFilesSafe(root).ToList())
        {
            long size;
            try { size = new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }

            if (dryRun) { freed += size; deleted++; continue; }
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                freed += size;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        if (dryRun)
        {
            logger.Simulated($"Limpar {root}: {deleted} arquivos, {MemoryCleanResult.FormatBytes((ulong)freed)}");
        }
        else
        {
            // Remove subpastas que ficaram vazias (da mais profunda para a mais rasa).
            foreach (var dir in EnumerateDirectoriesSafe(root).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new CleanupResult(freed, deleted, skipped, dryRun);
    }

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint // nunca segue junções/links
    };

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateFiles(root, "*", Options).Where(f => !IsProtected(root, f)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", Options).Where(d => !IsProtected(root, d)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Pastas que nunca são limpas: a extração dos executáveis .NET de arquivo único (%TEMP%\.net, ou
    /// DOTNET_BUNDLE_EXTRACT_BASE_DIR). O próprio Gueibs Optimizer publicado roda de lá; apagar as DLLs nativas
    /// extraídas derruba o app (e outros apps .NET abertos) ao fechar, com DllNotFoundException.
    /// </summary>
    public static bool IsProtected(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } extract &&
            (full + "\\").StartsWith(Path.GetFullPath(extract).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = full[rootFull.Length..];
        return relative.Equals(".net", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(".net\\", StringComparison.OrdinalIgnoreCase);
    }

    private static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    private static bool EmptyRecycleBin()
    {
        const uint noConfirmation = 0x1, noProgress = 0x2, noSound = 0x4;
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, noConfirmation | noProgress | noSound);
        return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED = lixeira já vazia
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
