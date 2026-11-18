using HoYoShadeHub.Core;

namespace HoYoShadeHub;

// This executable links the production service, with only persistence replaced by an offline fixture.
internal static class AppConfig
{
    public static string? Value;
    public static bool ReadOnWorker;
    public static bool WriteOnWorker;
    public static string? CachedGameInfo
    {
        get { ReadOnWorker = Thread.CurrentThread.IsThreadPoolThread; return Value; }
        set { WriteOnWorker = Thread.CurrentThread.IsThreadPoolThread; Value = value; }
    }
    public static string? UserDataFolder => null;
    public static void SetVersionPoster(GameBiz biz, string name) { }
    public static bool GetEnableDX12(GameBiz biz) => false;
    public static bool GetIgnoreDX12Check(GameBiz biz) => false;
}
