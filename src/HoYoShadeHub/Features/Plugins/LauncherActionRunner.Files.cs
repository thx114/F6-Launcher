using HoYoShadeHub.Extensions.ReShade;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

public static partial class LauncherActionRunner
{
    private static async Task<string> PlaceFileAsync(PackActionStep step, LauncherActionContext context)
    {
        var result = await Task.Run(() =>
        {
            var plan = PackFilePlacement.Resolve(step, context.PackRoot, context.Entry?.GameDirectory,
                AppConfig.UserDataFolder, context.Host?.RootPath);
            return PackFilePlacement.Apply(plan, context.PackRoot!);
        });
        return (result.Changed ? "已放置文件：" : "文件内容一致，已保留：") + result.Destination
            + (result.Backup is null ? string.Empty : "；原文件备份：" + result.Backup);
    }
}
