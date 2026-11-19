using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GueibsOptimizer.Core;

namespace GueibsOptimizer.Services;

public sealed record PresentMonAsset(string Version, string FileName, string Url);

/// <summary>
/// Baixa o PresentMon de console da página oficial de releases da Intel (GitHub GameTechDev).
/// Só aceita URLs desse repositório e só usa o arquivo se a assinatura digital for válida e da Intel.
/// </summary>
public sealed partial class PresentMonDownloader(Logger logger, AppSettings settings, bool dryRun)
{
    public const string ReleasesApi = "https://api.github.com/repos/GameTechDev/PresentMon/releases";
    public const string AllowedPrefix = "https://github.com/GameTechDev/PresentMon/releases/download/";

    private static readonly HttpClient Http = CreateClient();

    public static string ToolsDirectory => Path.Combine(AppPaths.DataDirectory, "tools", "PresentMon");

    [GeneratedRegex(@"^PresentMon-(\d+\.\d+\.\d+)-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleAsset();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // A API do GitHub exige um User-Agent; usamos um nome genérico, sem dados do usuário.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GueibsOptimizer");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Escolhe o executável de console x64 da release estável mais recente.</summary>
    public static PresentMonAsset? SelectAsset(string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : [doc.RootElement];

        foreach (var release in releases)
        {
            if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var match = ConsoleAsset().Match(name);
                if (match.Success && url.StartsWith(AllowedPrefix, StringComparison.Ordinal) && url.EndsWith("/" + name, StringComparison.Ordinal))
                    return new PresentMonAsset(match.Groups[1].Value, name, url);
            }
        }
        return null;
    }

    /// <summary>Baixa, verifica a assinatura e grava o caminho nas configurações. Retorna o caminho ou lança com mensagem clara.</summary>
    public async Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        string json = await Http.GetStringAsync(ReleasesApi + "?per_page=10", ct).ConfigureAwait(false);
        var asset = SelectAsset(json) ?? throw new InvalidOperationException("Não encontrei o PresentMon de console na página oficial de releases.");

        if (dryRun)
        {
            logger.Simulated($"Baixar {asset.FileName} de {asset.Url}");
            return asset.FileName;
        }

        Directory.CreateDirectory(ToolsDirectory);
        string final = Path.Combine(ToolsDirectory, asset.FileName);
        string temp = final + ".download";

        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(temp);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done * 100.0 / total.Value);
            }
        }

        string? signer = Authenticode.SignerName(temp);
        if (!Authenticode.IsTrusted(temp) || signer is null || !signer.Contains("O=Intel Corporation", StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidOperationException($"O arquivo baixado não tem uma assinatura válida da Intel (assinante: {signer ?? "nenhum"}). Ele foi apagado.");
        }

        File.Move(temp, final, overwrite: true);
        settings.PresentMonPath = final;
        settings.Save();
        logger.Info($"PresentMon {asset.Version} baixado e verificado (assinado por {signer}).");
        return final;
    }
}
