using System.Diagnostics;
using System.Runtime.InteropServices;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public sealed record MemoryCleanResult(ulong AvailableBefore, ulong AvailableAfter, ulong Total, int ProcessesTrimmed, bool Simulated)
{
    /// <summary>Memória realmente liberada (diferença medida; nunca inventada). Pode ser 0.</summary>
    public ulong Freed => AvailableAfter > AvailableBefore ? AvailableAfter - AvailableBefore : 0;

    public static string FormatBytes(ulong bytes) => bytes >= 1024UL * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.00} GB"
        : $"{bytes / (1024.0 * 1024):0} MB";
}

/// <summary>Esvazia o working set dos processos acessíveis (EmptyWorkingSet) e mede a memória disponível antes/depois.</summary>
public sealed class MemoryCleanerService(Logger logger, bool dryRun)
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;

    public Task<MemoryCleanResult> CleanAsync() => Task.Run(Clean);

    public MemoryCleanResult Clean()
    {
        var (before, total) = ReadMemory();
        int trimmed = 0;

        if (dryRun)
        {
            int count = Process.GetProcesses().Length;
            logger.Simulated($"Liberar RAM: EmptyWorkingSet em até {count} processos");
            return new MemoryCleanResult(before, before, total, 0, true);
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota, false, process.Id);
                    if (handle != IntPtr.Zero && EmptyWorkingSet(handle)) trimmed++;
                }
                catch
                {
                    // processo protegido ou encerrado: ignora
                }
                finally
                {
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                }
            }
        }

        Thread.Sleep(700); // dá tempo ao gerenciador de memória de atualizar os contadores
        var (after, _) = ReadMemory();
        var result = new MemoryCleanResult(before, after, total, trimmed, false);
        logger.Info($"RAM: {trimmed} processos aliviados, {MemoryCleanResult.FormatBytes(result.Freed)} liberados de verdade.");
        return result;
    }

    public static (ulong Available, ulong Total) ReadMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.ullAvailPhys, status.ullTotalPhys) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
