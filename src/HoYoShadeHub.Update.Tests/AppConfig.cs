// The real updater runs against an isolated portable directory. No UI or network path is invoked.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoYoShadeHub;

internal static class AppConfig
{
    public static string PortableDirectory { get; set; } = string.Empty;
    public static bool IsPortable => true;
    public static string UserDataFolder => Path.Combine(PortableDirectory, "user-data");
    public static string AppVersion => "1.4.8";
    public static string? HoYoShadeHubLauncherExecutePath => null;
    public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
}
