using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace GueibsOptimizer.Services;

public sealed record PingStats(string Target, int Sent, int Received, double MinMs, double AvgMs, double MaxMs, double JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;

    /// <summary>Jitter = média da diferença absoluta entre latências consecutivas (como no RFC 3550, sem suavização).</summary>
    public static PingStats From(string target, IReadOnlyList<double?> samples)
    {
        var ok = samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        if (ok.Count == 0) return new PingStats(target, samples.Count, 0, 0, 0, 0, 0);
        double jitter = 0;
        if (ok.Count > 1)
        {
            for (int i = 1; i < ok.Count; i++) jitter += Math.Abs(ok[i] - ok[i - 1]);
            jitter /= ok.Count - 1;
        }
        return new PingStats(target, samples.Count, ok.Count, ok.Min(), ok.Average(), ok.Max(), jitter);
    }

    public string Summary => Received == 0
        ? $"{Target}: sem resposta (100% de perda)"
        : $"{Target}: média {AvgMs:0} ms · mín {MinMs:0} · máx {MaxMs:0} · jitter {JitterMs:0.0} ms · perda {LossPercent:0}%";
}

public sealed record SpeedProgress(string Phase, double Percent, double CurrentMbps);

public sealed record SpeedResult(double DownloadMbps, double UploadMbps);

/// <summary>
/// Ping (ICMP) e teste de velocidade com os endpoints públicos do speed test da Cloudflare
/// (speed.cloudflare.com/__down e /__up). Nenhum dado do usuário é enviado: o upload é um bloco de zeros.
/// </summary>
public sealed class NetworkTestService
{
    public const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=";
    public const string UploadUrl = "https://speed.cloudflare.com/__up";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static IReadOnlyList<string> ParseTargets(string text) =>
        text.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length is > 0 and <= 253 && t.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

    public async Task<PingStats> PingAsync(string target, int count, Action<double?>? onSample = null, CancellationToken ct = default)
    {
        var samples = new List<double?>();
        using var ping = new Ping();
        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            double? ms = null;
            try
            {
                var reply = await ping.SendPingAsync(target, 2000).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
            }
            catch (PingException) { }
            samples.Add(ms);
            onSample?.Invoke(ms);
            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (TaskCanceledException) { break; }
        }
        return PingStats.From(target, samples);
    }

    public async Task<SpeedResult> SpeedTestAsync(IProgress<SpeedProgress>? progress, CancellationToken ct, int connections = 4, int seconds = 8)
    {
        double down = await MeasureAsync("Download", connections, seconds, progress, DownloadOnce, ct).ConfigureAwait(false);
        double up = await MeasureAsync("Upload", connections, seconds, progress, UploadOnce, ct).ConfigureAwait(false);
        return new SpeedResult(down, up);
    }

    public static double Mbps(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000;

    private delegate Task TransferOnce(Action<long> counted, CancellationToken ct);

    /// <summary>Várias conexões paralelas repetindo transferências até o tempo acabar.</summary>
    private static async Task<double> MeasureAsync(string phase, int connections, int seconds, IProgress<SpeedProgress>? progress,
        TransferOnce transfer, CancellationToken ct)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        void Count(long n) => Interlocked.Add(ref total, n);

        var workers = Enumerable.Range(0, connections).Select(async _ =>
        {
            while (!timeout.IsCancellationRequested)
            {
                try { await transfer(Count, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (HttpRequestException) { await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); }
            }
        }).ToList();

        var reporter = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                progress?.Report(new SpeedProgress(phase, Math.Min(100, sw.Elapsed.TotalSeconds * 100 / seconds),
                    Mbps(Interlocked.Read(ref total), sw.Elapsed)));
                try { await Task.Delay(250, timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);
        await reporter.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = Mbps(Interlocked.Read(ref total), sw.Elapsed);
        progress?.Report(new SpeedProgress(phase, 100, result));
        return result;
    }

    private static async Task DownloadOnce(Action<long> counted, CancellationToken ct)
    {
        using var response = await Http.GetAsync(DownloadUrl + 25_000_000, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) counted(read);
    }

    private static async Task UploadOnce(Action<long> counted, CancellationToken ct)
    {
        var payload = new byte[2_000_000];
        using var content = new CountingContent(payload, counted);
        using var response = await Http.PostAsync(UploadUrl, content, ct).ConfigureAwait(false);
    }

    /// <summary>Conta os bytes à medida que saem, para a medição de upload ser em tempo real.</summary>
    private sealed class CountingContent(byte[] data, Action<long> counted) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
        {
            const int chunk = 64 * 1024;
            for (int offset = 0; offset < data.Length; offset += chunk)
            {
                int len = Math.Min(chunk, data.Length - offset);
                await stream.WriteAsync(data.AsMemory(offset, len)).ConfigureAwait(false);
                counted(len);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }
    }
}
