namespace HoYoShadeHub.Helpers;

/// <summary>Compatibility adapter sharing the public-only download policy.</summary>
public static class LauncherUpdateProxyManager
{
    public static string? GetProxyUrl(int serverIndex) => CloudProxyManager.GetProxyUrl(serverIndex);
    public static string[] GetAllProxiesForServer(int serverIndex) => CloudProxyManager.GetAllProxiesForServer(serverIndex);
}
