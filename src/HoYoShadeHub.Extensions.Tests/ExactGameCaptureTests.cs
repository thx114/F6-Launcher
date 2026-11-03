using System.Buffers.Binary;
using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ExactGameCaptureTests
{
    private const string GamePath = @"D:\Games\YuanShen.exe";
    private const long Creation = 134377430398371062;

    private sealed class Fake : IExactGameClickTarget
    {
        public int Pid { get; set; } = 43024;
        public long CreationFileTime { get; set; } = Creation;
        public string ImagePath { get; set; } = GamePath;
        public bool HasExited { get; set; }
        public nint ForegroundWindow { get; set; } = 1234;
        public int OwnerPid { get; set; } = 43024;
        public bool Visible { get; set; } = true;
        public bool Disposed { get; private set; }
        public int CursorMoves { get; private set; }
        public int Clicks { get; private set; }
        public int WindowProcessId(nint _) => OwnerPid;
        public bool IsTopLevelVisible(nint _) => Visible;
        public (int Width, int Height) ClientSize(nint _) => (1920, 1080);
        public (int X, int Y) ToScreen(nint _, int x, int y) => (x, y);
        public void SetCursor(int _, int __) => CursorMoves++;
        public void ClickOnce() => Clicks++;
        public void Dispose() => Disposed = true;
    }

    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
        JsonElement Step(string json) => JsonDocument.Parse(json).RootElement.Clone();
        var valid = Step("""{"action":"capture_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062}""");
        var request = ExactGameCaptureRequest.Parse(valid);
        var action = new PackAutoAction();
        action.Steps.Add(new PackActionStep { Action = "capture_game", Raw = valid });
        Check(ExactGameCaptureRequest.ValidateCliBatch("hk4e_cn", [action]) == request, "single capture step accepted");
        foreach (string invalid in new[] {
            """{"action":"capture_game","expectedPid":43024}""",
            """{"action":"capture_game","expectedPid":43024.0,"expectedCreationFileTime":134377430398371062}""",
            """{"action":"capture_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062.0}""",
            """{"action":"capture_game","expectedPid":-1,"expectedCreationFileTime":134377430398371062}""" })
        {
            bool rejected = false; try { ExactGameCaptureRequest.Parse(Step(invalid)); } catch { rejected = true; }
            Check(rejected, "invalid exact identity rejected");
        }
        foreach (string biz in new[] { "hkrpg_cn", "hk4e_global", "nap_cn" })
        {
            bool rejected = false; try { ExactGameCaptureRequest.ValidateCliBatch(biz, [action]); } catch { rejected = true; }
            Check(rejected, "wrong biz rejected: " + biz);
        }
        action.Steps.Add(new PackActionStep { Action = "click_game", Raw = valid });
        bool mixedRejected = false;
        try { ExactGameCaptureRequest.ValidateCliBatch("hk4e_cn", [action]); } catch { mixedRejected = true; }
        Check(mixedRejected, "mixed action rejected");

        byte[] png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), 1920);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), 1080);
        string root = Path.Combine(Path.GetTempPath(), "exact-capture-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            async Task Denied(Fake fake, string name, Action<Fake>? during = null)
            {
                int calls = 0;
                bool denied = false;
                try
                {
                    await ExactGameCapture.ExecuteAsync(request, GamePath, root,
                        _ => { calls++; during?.Invoke(fake); return Task.FromResult(png); }, _ => fake);
                }
                catch (InvalidOperationException) { denied = true; }
                Check(denied && fake.CursorMoves == 0 && fake.Clicks == 0 && fake.Disposed
                    && !Directory.Exists(root), name);
            }
            await Denied(new Fake { CreationFileTime = Creation + 1 }, "PID reuse rejected");
            await Denied(new Fake { ImagePath = @"D:\Other\YuanShen.exe" }, "wrong executable path rejected");
            await Denied(new Fake { OwnerPid = 99 }, "foreign foreground rejected");
            await Denied(new Fake { Visible = false }, "hidden foreground rejected");
            await Denied(new Fake(), "focus change during capture rejected", f => f.ForegroundWindow = 42);
            await Denied(new Fake(), "process exit during capture rejected", f => f.HasExited = true);
            var good = new Fake();
            int captures = 0;
            var result = await ExactGameCapture.ExecuteAsync(request, GamePath, root,
                _ => { captures++; return Task.FromResult(png); }, _ => good);
            Check(captures == 1 && good.Disposed && good.Clicks == 0 && good.CursorMoves == 0,
                "exactly one read-only frame");
            Check(result.Width == 1920 && result.Height == 1080 && File.Exists(result.Path)
                && result.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)),
                "PNG result persisted with dimensions and digest");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Exact game capture tests: PASS {passed}, FAIL 0 (fake windows only)");
        return 0;
    }
}
