using HoYoShadeHub.Core.Networking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoYoShadeHub;

// Only storage/settings are substituted; locator, latency cache and downloads are production code.
internal static class AppConfig
{
    public static string UserDataFolder { get; set; } = "";
    public static string? XxmiRoot { get; set; }
    public static Dictionary<string, string> Instances { get; } = [];
    public static string? GetXxmiInstance(string biz) => Instances.GetValueOrDefault(biz);
    public static bool EnableDoh { get; set; }
    public static bool EnableEch { get; set; }
    public static DohProvider DohProvider { get; set; }
    public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
}
