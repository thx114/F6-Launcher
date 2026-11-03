namespace HoYoShadeHub.Extensions.Services;

/// <summary>Read-only drop inspection; never treats an extracted directory as an installable file.</summary>
public static class LocalPackageDropInput
{
    public static string? GetValidationError(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "没有收到文件路径，请重新拖入覆盖包 ZIP。";
        if (Directory.Exists(path)) return "拖入的是文件夹；请直接拖入原始覆盖包 .zip，不要先解压。";
        if (!File.Exists(path)) return "拖入的文件不存在或无法访问，请确认文件仍在本机并重试。";
        string extension = Path.GetExtension(path);
        if (!new[] { ".zip", ".dll", ".addon64", ".addon32", ".addon" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase))
            return "不支持这个文件类型；覆盖包请使用原始 .zip，勿只修改文件后缀。";
        return null;
    }

    public static LocalPackageDropInspection Inspect(string path)
    {
        string? error = GetValidationError(path);
        if (error is not null) return new(path, null, error);
        try { return new(path, LocalPackageInstaller.DetectKind(path), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return new(path, null, "无法读取安装包，请确认下载完整、文件未被占用，并使用原始 ZIP：" + ex.Message);
        }
    }
}

public sealed record LocalPackageDropInspection(string Path, LocalPackageKind? Kind, string? Error);
