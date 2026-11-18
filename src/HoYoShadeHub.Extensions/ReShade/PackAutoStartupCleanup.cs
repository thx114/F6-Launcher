namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 兼容旧调用的无操作入口。启动时不能区分未同意、失败待重试与已完成的 auto.json，
/// 因此不枚举目录、不删除动作；执行资格由快照、同意和批次账本决定。
/// </summary>
public static class PackAutoStartupCleanup
{
    public static int DeleteExisting(params string?[] roots) => 0;
}
