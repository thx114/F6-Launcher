using HoYoShadeHub.Extensions.ReShade;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

public static partial class LauncherActionRunner
{
    private static Task<string> PlaceFileAsync(PackActionStep step, LauncherActionContext context)
        => PackLocalActionExecutor.PlaceFileAsync(step, new()
        {
            Entry = context.Entry, Host = context.Host, Plugins = context.Plugins,
            PackRoot = context.PackRoot, LauncherDirectory = AppConfig.UserDataFolder,
            LauncherVersion = AppConfig.AppVersion,
        });

}
