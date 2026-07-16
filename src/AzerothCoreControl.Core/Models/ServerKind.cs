namespace AzerothCoreControl.Core.Models;

/// <summary>The two AzerothCore server processes this app supervises.</summary>
public enum ServerKind
{
    /// <summary>The login server (authserver.exe).</summary>
    Auth,

    /// <summary>The world/game server (worldserver.exe) — the one with the interactive console.</summary>
    World,
}

public static class ServerKindExtensions
{
    /// <summary>Default executable file name for each server kind (Windows).</summary>
    public static string ExecutableName(this ServerKind kind) => kind switch
    {
        ServerKind.Auth => "authserver.exe",
        ServerKind.World => "worldserver.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string DisplayName(this ServerKind kind) => kind switch
    {
        ServerKind.Auth => "Auth Server",
        ServerKind.World => "World Server",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
