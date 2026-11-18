using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoYoShadeHub;

internal static class AppConfig
{
    public static bool IsPortable => false;
    public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
}
