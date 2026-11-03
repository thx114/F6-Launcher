using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using System;
using System.IO;

namespace HoYoShadeHub.Features.Plugins;

internal static class GameNrRuntimeService
{
    // 显式准备/启动入口调用；导航和只读查询不得创建加载目录。
    public static void PrepareOpti(GameId game)
        => PrepareOpti(game, AppConfig.GetGameNrVersion(game, "opti"));

    // 只有准备成功后才写选择；CLI / 覆盖包使用与启动链相同的加载目录及配置投影。
    public static void SetOptiVersion(GameId game, string? version)
    {
        PrepareOpti(game, version);
        AppConfig.SetGameNrVersion(game, "opti", version);
    }

    private static void PrepareOpti(GameId game, string? nr)
    {
        var library = new OptiScalerLibrary(AppConfig.OptiScalerRootPath);
        string? id = AppConfig.GetSelectedOptiScalerId(game);
        var build = library.List(includeDirectorySize: false).Find(b => b.Id == id) ?? library.GetSelected(includeDirectorySize: false);
        if (nr is null && (build is null || !Directory.Exists(NrRuntimeLibrary.OptiDirectory(AppConfig.CacheRoot, game.GameBiz.Value, build.Id)))) return;
        if (build?.DllPath is not { } dll) throw new IOException("已选择独立 NR，但 OptiScaler 构建缺失。");
        string runtimeDll = NrRuntimeLibrary.PrepareOpti(AppConfig.CacheRoot, game.GameBiz.Value, build.Id, dll, nr);
        if (!OptiScalerProfiles.PrepareAuthoritativeProfile(Path.GetDirectoryName(runtimeDll)!,
            Path.GetDirectoryName(dll)!, game.GameBiz.Value))
            throw new IOException("无法将正式 OptiScaler 游戏配置安全投影到运行目录，已停止准备。");
    }
}
