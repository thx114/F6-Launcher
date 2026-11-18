using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HoYoShadeHub.Extensions.Games;

public static class StarRailGraphicsSettings
{
    /// <summary>仅修改已有图形配置的 FPS，保留渲染精度和其他画质字段。</summary>
    public static byte[] ApplyFps(byte[]? data, int fps)
    {
        if (fps is not (30 or 60 or 120))
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (data is null || data.Length == 0)
            throw new InvalidOperationException("崩铁图形配置尚未生成，请先启动游戏并保存一次图形设置。");

        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(Encoding.UTF8.GetString(data).TrimEnd('\0')) as JsonObject
                ?? throw new InvalidOperationException("崩铁图形配置格式无效，无法只修改帧率。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("崩铁图形配置格式无效，无法只修改帧率。", ex);
        }

        if (settings["FPS"] is not JsonValue currentFps
            || !currentFps.TryGetValue<int>(out int value) || value <= 0)
            throw new InvalidOperationException("崩铁图形配置缺少有效帧率，请先在游戏内保存一次图形设置。");

        settings["FPS"] = fps;
        return Encoding.UTF8.GetBytes(settings.ToJsonString() + "\0");
    }
}
