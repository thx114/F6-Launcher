using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>One read-only frame from an exactly identified foreground Genshin window.</summary>
public sealed record ExactGameCaptureRequest(int Pid, long CreationFileTime)
{
    public static ExactGameCaptureRequest Parse(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) throw new ArgumentException("capture_game requires an object step.");
        int pid = checked((int)PositiveInteger(step, "expectedPid"));
        long creation = PositiveInteger(step, "expectedCreationFileTime");
        try { _ = DateTime.FromFileTimeUtc(creation); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("expectedCreationFileTime is outside Windows FILETIME range."); }
        return new(pid, creation);
    }

    public static ExactGameCaptureRequest ValidateCliBatch(string? biz, IReadOnlyList<PackAutoAction> actions)
    {
        if (!string.Equals(biz, "hk4e_cn", StringComparison.Ordinal))
            throw new ArgumentException("capture_game is restricted to --biz hk4e_cn.");
        if (actions.Count != 1 || actions[0].Steps.Count != 1
            || !string.Equals(actions[0].Steps[0].Action.Trim(), "capture_game", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("capture_game must be the only step in the only action of this CLI call.");
        return Parse(actions[0].Steps[0].Raw);
    }

    private static long PositiveInteger(JsonElement step, string name)
    {
        if (!step.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            throw new ArgumentException(name + " must be a positive JSON integer.");
        string raw = value.GetRawText();
        if (raw.Length == 0 || raw.Any(c => c is < '0' or > '9')
            || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
            throw new ArgumentException(name + " must be a positive decimal integer.");
        return number;
    }
}

public sealed record ExactGameCaptureResult(string Path, int Width, int Height, string Sha256);

public static class ExactGameCapture
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<ExactGameCaptureResult> ExecuteAsync(
        ExactGameCaptureRequest request, string expectedImagePath, string outputDirectory,
        Func<nint, Task<byte[]>> captureFrame,
        Func<int, IExactGameClickTarget>? bind = null)
    {
        if (captureFrame is null) throw new ArgumentNullException(nameof(captureFrame));
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            throw new InvalidOperationException("Capture log directory must be absolute.");
        using IExactGameClickTarget target = (bind ?? ExactGameClick.BindTarget)(request.Pid);
        var before = ExactGameClick.VerifyForeground(target, request.Pid, request.CreationFileTime, expectedImagePath);
        byte[] png = await captureFrame(before.Window).ConfigureAwait(false);
        var after = ExactGameClick.VerifyForeground(target, request.Pid, request.CreationFileTime, expectedImagePath);
        if (after != before) throw new InvalidOperationException("Game window or client area changed during capture.");
        if (png.Length < 24 || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException("Capture did not produce PNG data.");
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidOperationException("Capture PNG dimensions are invalid.");
        if (width != after.Width || height != after.Height)
            throw new InvalidOperationException("Capture PNG does not match the game client area.");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory,
            $"capture-hk4e_cn-{request.Pid}-{request.CreationFileTime}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.png");
        bool created = false;
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            await file.WriteAsync(png).ConfigureAwait(false);
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
        return new(path, width, height, Convert.ToHexString(SHA256.HashData(png)));
    }
}
