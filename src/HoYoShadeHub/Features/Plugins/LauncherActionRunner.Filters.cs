using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.ReShade;
using System;
using System.IO;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

public static partial class LauncherActionRunner
{
    private static async Task<string> InstallOptFiltersAsync(PackActionStep step, LauncherActionContext context)
    {
        string? buildId = step.GetString("build");
        if (string.IsNullOrWhiteSpace(buildId)) return "✗ install_opt_filters 需要 build（例如 F6/v5.2）。";
        var build = new OptiScalerLibrary(AppConfig.OptiScalerRootPath).List(includeDirectorySize: false)
            .Find(candidate => string.Equals(candidate.Id, buildId, StringComparison.OrdinalIgnoreCase));
        if (build is null) return "✗ 滤镜所属的 OptiScaler 构建未安装：" + buildId;
        string source;
        bool fromLauncher = string.Equals(step.GetString("source"), "launcher", StringComparison.OrdinalIgnoreCase);
        if (fromLauncher)
        {
            var host = PluginHostLocator.Resolve(out string reason);
            if (host is null) return "✗ 无法找到启动器 HoYoShade 框架：" + reason;
            source = host.RootPath;
        }
        else
        {
            if (step.GetString("source") is not (null or "package")) return "✗ source 仅支持 package / launcher。";
            if (string.IsNullOrWhiteSpace(context.PackRoot)) return "✗ 安装滤镜需要覆盖包目录。";
            source = PackageTree.ResolveRelative(context.PackRoot, step.GetString("directory") ?? "HoYoShade");
        }
        var result = await Task.Run(() => OptiScalerFilterInstaller.Install(build.Directory, source,
            useSourceConfiguration: !fromLauncher));
        return $"已安装 F6 滤镜（{result.Files} 个载荷文件）：{result.Directory}";
    }
}
