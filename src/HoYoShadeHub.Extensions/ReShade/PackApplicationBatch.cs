using System.Text.Json;
using System.Text.Json.Serialization;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 安装器生成的应用批次，不由包作者提供，也不因内容、路径或启动次数变化而重置。
/// 旧包首次使用时建立本地批次；只有一次完整的显式导入才建立下一批次。
/// </summary>
public static class PackApplicationBatch
{
    public const string FileName = ".hysx_apply.json";
    internal sealed class State
    {
        [JsonRequired] public int Schema { get; set; } = 1;
        [JsonRequired] public string Batch { get; set; } = Guid.NewGuid().ToString("N");
        public bool Imported { get; set; }
        [JsonRequired] public Dictionary<string, string> Operations { get; set; } = new(StringComparer.Ordinal);
    }

    public static bool IsLocalState(string path) =>
        Path.GetFileName(path).StartsWith(".hysx_apply", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).StartsWith(".hysx_once", StringComparison.OrdinalIgnoreCase);

    /// <summary>只对已验证的解压暂存目录调用；该记录随完整 GamePack 事务提交。</summary>
    public static string BeginImport(string stagedPackRoot)
    {
        var state = new State { Imported = true };
        Write(stagedPackRoot, state);
        return state.Batch;
    }

    /// <summary>只读身份；旧包尚未建账时返回空，不为查看或快照创建新批次。</summary>
    public static string ReadIdentity(string packRoot) => Read(packRoot) is { Imported: true } state ? state.Batch : string.Empty;

    public static Session Open(string packRoot)
    {
        string root = Path.GetFullPath(packRoot);
        PackageTree.ResolveRelative(root, FileName);
        var lease = new FileStream(Path.Combine(root, FileName + ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try { return new Session(root, lease, Read(root) ?? new State()); }
        catch { lease.Dispose(); throw; }
    }

    public sealed class Session : IDisposable
    {
        private readonly string _root;
        private readonly FileStream _lease;
        private readonly State _state;
        private bool _disposed;
        internal Session(string root, FileStream lease, State state) => (_root, _lease, _state) = (root, lease, state);

        /// <summary>先落 pending；仅真实完成或确定的无改动才落 success。异常保持可重试。</summary>
        public bool ApplyOnce(string operation, Func<bool> apply)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.Operations.TryGetValue(operation, out string? state) && state == "success") return false;
            _state.Operations[operation] = "pending";
            Write(_root, _state);
            bool changed = apply();
            _state.Operations[operation] = "success";
            Write(_root, _state);
            return changed;
        }

        public void Dispose() { if (!_disposed) { _disposed = true; _lease.Dispose(); } }
    }

    private static State? Read(string root)
    {
        string json;
        try { json = File.ReadAllText(Path.Combine(root, FileName)); }
        catch (FileNotFoundException) { return null; }
        State state;
        try { state = JsonSerializer.Deserialize<State>(json) ?? throw new InvalidDataException("应用批次为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("应用批次损坏；保留用户配置，等待显式重新导入。", ex); }
        if (state.Schema != 1 || !Guid.TryParseExact(state.Batch, "N", out _) || state.Operations is null
            || state.Operations.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is not ("pending" or "success")))
            throw new InvalidDataException("应用批次状态非法；保留用户配置，等待显式重新导入。");
        return state;
    }

    private static void Write(string root, State state)
    {
        string path = PackageTree.ResolveRelative(root, FileName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
