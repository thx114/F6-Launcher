using System.Text;
using System.Text.Json.Nodes;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class StarRailGraphicsSettingsTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(label);
            passed++;
        }

        var original = Encoding.UTF8.GetBytes("""
            {"FPS":60,"RenderScale":1.6,"EnableVSync":true,"ShadowQuality":1,
             "futureOption":{"enabled":true,"values":[0.7,"keep",null]}}
            """ + "\0");
        byte[] snapshot = (byte[])original.Clone();
        JsonObject Parse(byte[] data) => JsonNode.Parse(Encoding.UTF8.GetString(data).TrimEnd('\0'))!.AsObject();
        JsonObject expected = Parse(original);
        expected.Remove("FPS");
        foreach (int fps in new[] { 30, 60, 120 })
        {
            byte[] updated = StarRailGraphicsSettings.ApplyFps(original, fps);
            JsonObject actual = Parse(updated);
            Check(actual["FPS"]!.GetValue<int>() == fps, "Apply requested FPS");
            Check(actual["RenderScale"]!.GetValue<double>() == 1.6, "Preserve non-default render scale");
            actual.Remove("FPS");
            Check(JsonNode.DeepEquals(actual, expected), "Preserve all other graphics and unknown fields");
            Check(updated[^1] == 0 && original.SequenceEqual(snapshot), "Keep NUL termination without mutating source bytes");
        }

        byte[]?[] invalid =
        [
            null, [], Encoding.UTF8.GetBytes(" "), Encoding.UTF8.GetBytes("null"),
            Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("60"),
            Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{\"RenderScale\":0.8}"),
            Encoding.UTF8.GetBytes("{broken"), Encoding.UTF8.GetBytes("{\"FPS\":\"60\"}"),
            Encoding.UTF8.GetBytes("{\"FPS\":null}"), Encoding.UTF8.GetBytes("{\"FPS\":0}"),
            Encoding.UTF8.GetBytes("{\"FPS\":-1}")
        ];
        foreach (byte[]? data in invalid)
        {
            byte[]? before = data is null ? null : (byte[])data.Clone();
            bool rejected = false;
            try { StarRailGraphicsSettings.ApplyFps(data, 120); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Reject missing or malformed graphics before producing writable data");
            Check(data is null || data.SequenceEqual(before!), "Invalid source data remains untouched");
        }

        bool invalidFpsRejected = false;
        try { StarRailGraphicsSettings.ApplyFps(original, 144); }
        catch (ArgumentOutOfRangeException) { invalidFpsRejected = true; }
        Check(invalidFpsRejected, "Reject unsupported requested FPS");
        Console.WriteLine($"StarRail graphics settings: {passed} passed, 0 failed.");
        return 0;
    }
}
