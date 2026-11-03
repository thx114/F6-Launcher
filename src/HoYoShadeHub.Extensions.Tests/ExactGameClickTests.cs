using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ExactGameClickTests
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
        public bool StealFocusOnMove { get; set; }
        public int WindowProcessId(nint window) => OwnerPid;
        public bool IsTopLevelVisible(nint window) => Visible;
        public (int Width, int Height) ClientSize(nint window) => (1920, 1080);
        public (int X, int Y) ToScreen(nint window, int x, int y) => (x + 100, y + 50);
        public void SetCursor(int x, int y) { CursorMoves++; if (StealFocusOnMove) ForegroundWindow = 9876; }
        public void ClickOnce() => Clicks++;
        public void Dispose() => Disposed = true;
    }

    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
        JsonElement Step(string json) => JsonDocument.Parse(json).RootElement.Clone();
        var valid = Step("""{"action":"click_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062,"x":0.5,"y":0.75}""");
        var request = ExactGameClickRequest.Parse(valid);
        Check(request.Pid == 43024 && request.CreationFileTime == Creation && request.X == 0.5, "exact JSON identity and normalized coordinates");
        var action = new PackAutoAction(); action.Steps.Add(new PackActionStep { Action = "click_game", Raw = valid });
        Check(ExactGameClickRequest.ValidateCliBatch("hk4e_cn", [action]) == request, "single-step Genshin CLI accepted");
        foreach (string invalid in new[] {
            """{"action":"click_game","expectedPid":43024,"x":0.5,"y":0.5}""",
            """{"action":"click_game","expectedPid":43024.0,"expectedCreationFileTime":134377430398371062,"x":0.5,"y":0.5}""",
            """{"action":"click_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062.0,"x":0.5,"y":0.5}""",
            """{"action":"click_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062,"x":1,"y":0.5}""",
            """{"action":"click_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062,"x":-0.01,"y":0.5}""",
            """{"action":"click_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062,"x":0.5}""" })
        {
            bool rejected = false; try { ExactGameClickRequest.Parse(Step(invalid)); } catch { rejected = true; }
            Check(rejected, "malformed click request rejected");
        }
        foreach (string biz in new[] { "hk4e_global", "hkrpg_cn", "nap_cn" })
        {
            bool rejected = false; try { ExactGameClickRequest.ValidateCliBatch(biz, [action]); } catch { rejected = true; }
            Check(rejected, "non-hk4e_cn biz rejected: " + biz);
        }
        action.Steps.Add(new PackActionStep { Action = "click_game", Raw = valid });
        bool batchRejected = false; try { ExactGameClickRequest.ValidateCliBatch("hk4e_cn", [action]); } catch { batchRejected = true; }
        Check(batchRejected, "multiple clicks rejected before execution");
        action.Steps.RemoveAt(1);
        batchRejected = false; try { ExactGameClickRequest.ValidateCliBatch("hk4e_cn", [action, action]); } catch { batchRejected = true; }
        Check(batchRejected, "multiple actions rejected before execution");

        void Denied(Fake fake, string name)
        {
            bool denied = false;
            try { ExactGameClick.Execute(request, GamePath, _ => fake); } catch { denied = true; }
            Check(denied && fake.Clicks == 0 && fake.Disposed, name);
        }
        Denied(new Fake { CreationFileTime = Creation + 1 }, "reused PID rejected");
        Denied(new Fake { ImagePath = @"D:\Other\YuanShen.exe" }, "same basename, wrong path rejected");
        Denied(new Fake { Pid = 999 }, "wrong bound PID rejected");
        Denied(new Fake { HasExited = true }, "exited process rejected");
        Denied(new Fake { OwnerPid = 999 }, "foreign foreground window rejected");
        Denied(new Fake { Visible = false }, "hidden foreground window rejected");
        Denied(new Fake { StealFocusOnMove = true }, "focus stolen before click rejected");
        var good = new Fake();
        string result = ExactGameClick.Execute(request, GamePath, _ => good);
        Check(good.Clicks == 1 && good.CursorMoves == 1 && good.Disposed && result.Contains("client=(960,810)"), "valid request clicks exactly once with logged target");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        bool nativeRejected = false;
        try { ExactGameClick.Execute(new(self.Id, self.StartTime.ToFileTimeUtc() + 1, 0.5, 0.5), Environment.ProcessPath!); }
        catch (InvalidOperationException) { nativeRejected = true; }
        Check(nativeRejected, "native self-host wrong creation FILETIME rejected before any cursor or click");
        Console.WriteLine($"Exact game click tests: PASS {passed}, FAIL 0 (fake processes and windows only)");
        return 0;
    }
}
