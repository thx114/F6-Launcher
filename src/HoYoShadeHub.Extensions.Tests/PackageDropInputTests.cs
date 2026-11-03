using System.IO.Compression;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackageDropInputTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-drop-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            passed++;
        }
        try
        {
            Check(LocalPackageDropInput.GetValidationError(null) is not null, "Empty path must not disappear");
            Check(LocalPackageDropInput.GetValidationError(" ") is not null, "Blank path rejected");
            string folder = Path.Combine(root, "aaaaaa");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "sentinel"), "keep");
            Check(LocalPackageDropInput.Inspect(folder) is { Kind: null, Error: not null }, "Folder rejected with reason");
            Check(LocalPackageDropInput.GetValidationError(folder)!.Contains("不要先解压"), "Folder provides original ZIP instructions");
            Check(LocalPackageDropInput.Inspect(Path.Combine(root, "missing.zip")) is { Kind: null, Error: not null }, "Missing file rejected");
            string fakeZip = Path.Combine(root, "bad.zip");
            File.WriteAllText(fakeZip, "not a zip");
            Check(LocalPackageDropInput.Inspect(fakeZip) is { Kind: null, Error: not null }, "Corrupt ZIP reported rather than thrown");
            string extensionless = Path.Combine(root, "no-extension");
            File.WriteAllText(extensionless, "unknown");
            Check(LocalPackageDropInput.Inspect(extensionless) is { Kind: null, Error: not null }, "Extensionless file not silently installed as a module");
            string archive7z = Path.Combine(root, "package.7z");
            File.WriteAllText(archive7z, "7z");
            Check(LocalPackageDropInput.GetValidationError(archive7z) is not null, "Unsupported archive reports ZIP requirement");
            string overlay = Path.Combine(root, "覆盖包.ZIP");
            using (var archive = ZipFile.Open(overlay, ZipArchiveMode.Create))
            {
                archive.CreateEntry("HoYoShade/ReShade64.dll");
                archive.CreateEntry("HoYoShade/reshade-shaders/Addons/fixture.addon64");
            }
            Check(LocalPackageDropInput.Inspect(overlay) is { Kind: LocalPackageKind.Overlay, Error: null }, "ZIP overlay detection preserved");
            var mixed = new[] { fakeZip, folder, overlay }.Select(LocalPackageDropInput.Inspect).ToArray();
            Check(mixed.Count(p => p.Kind == LocalPackageKind.Overlay) == 1 && mixed.Count(p => p.Error is not null) == 2,
                "One rejected input cannot abort a batch containing a good overlay");
            string dll = Path.Combine(root, "OptiScaler.dll");
            File.WriteAllText(dll, "fixture");
            Check(LocalPackageDropInput.Inspect(dll) is { Kind: LocalPackageKind.OptiScaler, Error: null }, "DLL routing preserved");
            string addon = Path.Combine(root, "fixture.addon64");
            File.WriteAllText(addon, "fixture");
            Check(LocalPackageDropInput.Inspect(addon) is { Kind: LocalPackageKind.Addon, Error: null }, "Addon routing preserved");
            Check(File.ReadAllText(Path.Combine(folder, "sentinel")) == "keep", "Read-only inspection never changes a folder");
            Check(File.ReadAllText(fakeZip) == "not a zip", "Rejected file remains untouched");
            Console.WriteLine($"Package drop input tests: PASS {passed}, FAIL 0");
        }
        finally { Directory.Delete(root, true); }
    }
}
