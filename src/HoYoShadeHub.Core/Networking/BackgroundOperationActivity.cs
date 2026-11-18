namespace HoYoShadeHub.Core.Networking;

public sealed record BackgroundOperationSnapshot(long Id, string Owner, string Name, bool InlineVisible,
    DateTimeOffset Started, string Status, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100) : null;
}

/// <summary>Operation state outlives navigation; UI controls never enter the registry.</summary>
public static class BackgroundOperationActivity
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, BackgroundOperationSnapshot> Active = [];
    private static readonly AsyncLocal<long> Context = new();
    private static long _next;
    public static long CurrentId => Context.Value;
    public static bool HasActive { get { lock (Gate) return Active.Count != 0; } }

    public static void SetCurrentName(string name)
    {
        lock (Gate)
            if (Active.TryGetValue(Context.Value, out var item)) Active[item.Id] = item with { Name = name };
    }

    public static IReadOnlyList<BackgroundOperationSnapshot> Snapshot()
    { lock (Gate) return Active.Values.OrderBy(x => x.Id).ToArray(); }

    public static Operation Begin(string owner, string name, bool inlineVisible)
    {
        long id = Interlocked.Increment(ref _next), previous = Context.Value;
        lock (Gate) Active[id] = new(id, owner, name, inlineVisible, DateTimeOffset.UtcNow, "准备中", 0, null);
        Context.Value = id;
        return new Operation(id, previous);
    }

    public static void SetOwnerVisible(string owner, bool visible)
    {
        lock (Gate)
            foreach (var pair in Active.Where(x => x.Value.Owner == owner).ToArray())
                Active[pair.Key] = pair.Value with { InlineVisible = visible };
    }

    public static Transfer BeginTransfer(string name)
    {
        Operation? owned = null;
        lock (Gate)
        {
            if (!Active.ContainsKey(Context.Value)) owned = Begin(string.Empty, "下载 " + name, false);
            var item = Active[Context.Value];
            Active[item.Id] = item with { Status = "连接中", BytesReceived = 0, TotalBytes = null };
        }
        return new Transfer(Context.Value, owned);
    }

    public sealed class Operation : IDisposable
    {
        private readonly long id, previous;
        internal Operation(long id, long previous) { this.id = id; this.previous = previous; }
        private int _disposed;
        public long Id => id;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate) Active.Remove(id);
            if (Context.Value == id) Context.Value = previous;
        }
    }

    public sealed class Transfer : IDisposable
    {
        private readonly long id;
        private readonly Operation? owned;
        internal Transfer(long id, Operation? owned) { this.id = id; this.owned = owned; }
        public void Report(long received, long? total)
        {
            lock (Gate)
                if (Active.TryGetValue(id, out var item))
                    Active[id] = item with { Status = "下载中", BytesReceived = received, TotalBytes = total };
        }
        public void Dispose()
        {
            lock (Gate)
                if (Active.TryGetValue(id, out var item)) Active[id] = item with { Status = "正在校验 / 安装" };
            owned?.Dispose();
        }
    }
}
