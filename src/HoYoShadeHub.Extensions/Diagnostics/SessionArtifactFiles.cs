using System.Security.Cryptography;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Diagnostics;

/// <summary>会话日志的时间边界，以及已写完转储的单份归档。</summary>
public static class SessionArtifactFiles
{
    public static bool IsCurrent(string path, DateTime startedUtc, DateTime nowUtc)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LastWriteTimeUtc > startedUtc && info.LastWriteTimeUtc <= nowUtc;
    }

    /// <summary>
    /// 日志复制以实际打开的文件句柄时间为准；旧文件不创建目标，也不回退到其他文件。
    /// 配置快照传 null，不要求配置在本局改过。提交使用临时文件，失败不留下半份快照。
    /// </summary>
    public static (long Written, bool Tail) CopySnapshot(string source, string target, long maxBytes,
        DateTime? requiredAfterUtc = null)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (requiredAfterUtc is { } startedUtc)
        {
            DateTime modifiedUtc = File.GetLastWriteTimeUtc(input.SafeFileHandle);
            if (modifiedUtc <= startedUtc || modifiedUtc > DateTime.UtcNow) return (0, false);
        }
        // 固定本次读取长度，避免高频追踪追加速度超过复制速度时无界增长。
        long length = input.Length;
        bool tail = length > maxBytes;
        if (tail)
        {
            input.Seek(-maxBytes, SeekOrigin.End);
            while (input.Position < length)
            {
                int value = input.ReadByte();
                if (value < 0 || value == '\n') break;
            }
        }
        long remaining = length - input.Position;
        long written = 0;
        string staged = target + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024];
                while (remaining > 0)
                {
                    int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                    remaining -= read;
                    written += read;
                }
            }
            File.Move(staged, target, overwrite: true);
            return (written, tail);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    /// <summary>
    /// 写入中的转储不能移动。持有禁止写入的读句柄，同卷移动不复制大文件；
    /// 跨卷则先写临时文件并验证 SHA256，提交后才移除源。已有目标永不覆盖。
    /// </summary>
    public static void ArchiveCompletedDump(string source, string target)
    {
        string sourceFull = Path.GetFullPath(source);
        string targetFull = Path.GetFullPath(target);
        if (sourceFull.Equals(targetFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("转储源与归档目标不能相同。");
        if ((File.GetAttributes(sourceFull) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("转储源不能是重解析点：" + sourceFull);
        using var input = new FileStream(sourceFull, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length == 0) throw new IOException("转储尚未写完：" + sourceFull);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
        if (HysxFileLink.SameVolume(sourceFull, targetFull))
        {
            File.Move(sourceFull, targetFull); // 不覆盖；同卷重命名保持原文件身份。
            return;
        }
        string staged = targetFull + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            input.Position = 0;
            using (var check = File.OpenRead(staged))
            {
                if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(check)))
                    throw new IOException("转储归档 SHA256 校验失败，保留源文件。");
            }
            File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(sourceFull));
            File.Move(staged, targetFull);
            File.Delete(sourceFull);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
