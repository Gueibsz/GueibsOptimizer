using System.IO;

namespace GueibsOptimizer.Models;

public enum GameSource
{
    Steam,
    Epic,
    Launcher,   // Riot, Ubisoft, EA, Battle.net, Rockstar (pelo registro de desinstalação)
    Manual
}

/// <summary>Um jogo encontrado no PC: pasta e executável principal.</summary>
public sealed record GameEntry(string Name, GameSource Source, string Folder, string? Executable, string? SteamAppId = null)
{
    public string Key => (Executable ?? Folder).ToLowerInvariant();
    public string ExeName => Executable is null ? string.Empty : Path.GetFileName(Executable);

    public string SourceText => Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Launcher => "Launcher",
        _ => "Adicionado por você"
    };
}
