using System.IO;
using GueibsOptimizer.Core;
using GueibsOptimizer.Services;

namespace GueibsOptimizer.Tests;

public class PresentMonDownloaderTests
{
    private const string Releases = """
        [
          {"tag_name":"v2.7.0-beta","prerelease":true,"draft":false,"assets":[
            {"name":"PresentMon-2.7.0-x64.exe","browser_download_url":"https://github.com/GameTechDev/PresentMon/releases/download/v2.7.0-beta/PresentMon-2.7.0-x64.exe"}]},
          {"tag_name":"v2.6.0","prerelease":false,"draft":false,"assets":[
            {"name":"PresentMon-2.6.0.msi","browser_download_url":"https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0.msi"},
            {"name":"PresentMon-2.6.0-x86.exe","browser_download_url":"https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x86.exe"},
            {"name":"PresentMon-2.6.0-x64.exe","browser_download_url":"https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe"}]}
        ]
        """;

    [Fact]
    public void PicksLatestStableConsoleX64()
    {
        var asset = PresentMonDownloader.SelectAsset(Releases)!;
        Assert.Equal("2.6.0", asset.Version);
        Assert.Equal("PresentMon-2.6.0-x64.exe", asset.FileName);
        Assert.StartsWith(PresentMonDownloader.AllowedPrefix, asset.Url);
    }

    [Fact]
    public void RejectsAssetsHostedOutsideTheOfficialRepository()
    {
        const string json = """
            [{"prerelease":false,"assets":[
              {"name":"PresentMon-2.6.0-x64.exe","browser_download_url":"https://example.com/PresentMon-2.6.0-x64.exe"},
              {"name":"PresentMon-2.6.0-x64.exe","browser_download_url":"https://github.com/Outro/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe"}]}]
            """;
        Assert.Null(PresentMonDownloader.SelectAsset(json));
    }

    [Fact]
    public void AcceptsSingleReleaseObject() =>
        Assert.NotNull(PresentMonDownloader.SelectAsset("""
            {"prerelease":false,"assets":[{"name":"PresentMon-2.5.1-x64.exe","browser_download_url":"https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-2.5.1-x64.exe"}]}
            """));
}

public class AuthenticodeTests
{
    [Fact]
    public void UnsignedFileIsNotTrusted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0, 0]);
        try
        {
            Assert.False(Authenticode.IsTrusted(path));
            Assert.Null(Authenticode.SignerName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MicrosoftSignedDotnetHostIsTrusted()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return; // ambiente sem o host .NET instalado
        int code = Authenticode.Verify(dotnet);
        string? signer = Authenticode.SignerName(dotnet);
        Assert.True(code == 0 && signer?.Contains("O=Microsoft Corporation") == true, $"código 0x{code:X8}, assinante {signer ?? "null"}");
    }
}
