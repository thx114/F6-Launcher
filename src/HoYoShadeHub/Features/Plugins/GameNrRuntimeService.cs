using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

internal static class GameNrRuntimeService
{
    private sealed class GameWork
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long SelectionRevision;
    }

    private static readonly ConcurrentDictionary<string, GameWork> Work = new(StringComparer.OrdinalIgnoreCase);
    private static GameWork For(GameId game) => Work.GetOrAdd(game.GameBiz.Value, _ => new GameWork());

    // 显式准备/启动入口调用；导航和只读查询不得创建加载目录。
    public static void PrepareOpti(GameId game)
    {
        var work = For(game);
        work.Gate.Wait();
        try { NrRuntimeWorkQueue.Run(() => PrepareOpti(game, AppConfig.GetGameNrVersion(game, "opti"))); }
        finally { work.Gate.Release(); }
    }

    public static async Task PrepareOptiAsync(GameId game, CancellationToken cancellationToken = default)
    {
        var work = For(game);
        await work.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 只在准备开始前取消；已开始的链接/配置投影必须完成，不能留下半次切换。
            await NrRuntimeWorkQueue.RunAsync(() => PrepareOpti(game, AppConfig.GetGameNrVersion(game, "opti")), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { work.Gate.Release(); }
    }

    // 只有准备成功后才写选择；CLI / 覆盖包使用与启动链相同的加载目录及配置投影。
    public static void SetOptiVersion(GameId game, string? version)
    {
        var work = For(game);
        long revision = Interlocked.Increment(ref work.SelectionRevision);
        work.Gate.Wait();
        try { NrRuntimeWorkQueue.Run(() => ApplySelection(game, version, work, revision)); }
        finally { work.Gate.Release(); }
    }

    /// <summary>
    /// 后台准备并串行提交同一游戏的 NR 选择。尚未开始的旧请求直接跳过；
    /// 已开始的请求完成准备与记账后才放行下一次。false 表示已有更新请求，调用方不应回写旧 UI。
    /// </summary>
    public static async Task<bool> SetOptiVersionAsync(GameId game, string? version,
        CancellationToken cancellationToken = default)
    {
        var work = For(game);
        cancellationToken.ThrowIfCancellationRequested();
        long revision = Interlocked.Increment(ref work.SelectionRevision);
        await work.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await NrRuntimeWorkQueue.RunAsync(() => ApplySelection(game, version, work, revision), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { work.Gate.Release(); }
    }

    private static bool ApplySelection(GameId game, string? version, GameWork work, long revision)
    {
        if (revision != Volatile.Read(ref work.SelectionRevision)) return false;
        PrepareOpti(game, version);
        AppConfig.SetGameNrVersion(game, "opti", version);
        return revision == Volatile.Read(ref work.SelectionRevision);
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
