using HoYoShadeHub.Core.IO;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PortableLauncherLocationTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            passed++;
        }
        string root = Path.Combine(Path.GetTempPath(), "f6-portable-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string modern = Path.Combine(root, "F6Launcher.exe"), legacy = Path.Combine(root, "HoYoShadeHub.exe");
            Check(PortableLauncherLocation.Find(null) is null, "no installation directory is not portable");
            Check(PortableLauncherLocation.Find(Path.Combine(root, "missing")) is null, "missing installation directory is not portable");
            Check(PortableLauncherLocation.Find(root) is null, "empty directory is not portable");
            File.WriteAllText(modern, "fixture, never executed");
            Check(PortableLauncherLocation.Find(root) == modern, "F6-only package is recognized without the legacy executable");
            File.WriteAllText(legacy, "fixture, never executed");
            Check(PortableLauncherLocation.Find(root) == modern, "existing installations prefer the F6 launcher when both exist");
            File.Delete(modern);
            Check(PortableLauncherLocation.Find(root) == legacy, "legacy installations remain recognized");
            File.Delete(legacy);
            Directory.CreateDirectory(modern);
            Check(PortableLauncherLocation.Find(root) is null, "a same-named directory is not an executable marker");
            Console.WriteLine($"Portable launcher entry: PASS {passed} / FAIL 0; RuntimeTested=false.");
            return passed;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
