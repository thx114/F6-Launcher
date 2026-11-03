namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>
/// 启动器 NR/DLL 文件事务的共享调度入口。UI 通过异步入口调用，worker 只返回数据；
/// 已进入的文件事务完整结束后才释放，不在链接与选择记账之间响应取消。
/// 操作内调用普通 NR 库方法，不能再次进入本队列。
/// </summary>
public static class NrRuntimeWorkQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Run(Action action)
    {
        Gate.Wait();
        try { action(); }
        finally { Gate.Release(); }
    }

    public static Task RunAsync(Action action, CancellationToken cancellationToken = default) =>
        RunAsync(() => { action(); return true; }, cancellationToken);

    public static async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(action, cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    public static async Task<T> RunTaskAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(action, cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
}
