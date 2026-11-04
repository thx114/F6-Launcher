// Compile this fixture with current or git-exported legacy updater sources, then apply the real ZIP.
// Keeping the old validator unchanged catches compatibility breaks that new-source tests miss.
using System.IO.Compression;
using System.Security.Cryptography;
using HoYoShadeHub;
using HoYoShadeHub.Features.Update;

if (args.Length is < 1 or > 2)
    throw new ArgumentException("Usage: HoYoShadeHub.Update.Tests <archive.zip> [--expect-legacy-rejection]");
string archive = Path.GetFullPath(args[0]);
bool expectRejection = args.Length == 2 && args[1] == "--expect-legacy-rejection";
string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
// GithubUpdateService derives the portable root from the running fixture's parent.
root = Directory.GetParent(root)!.FullName;
if (!Path.GetFileName(root).StartsWith("f6-update-fixture-", StringComparison.Ordinal))
    throw new InvalidOperationException("Run only from a fresh f6-update-fixture-* directory.");
AppConfig.PortableDirectory = root;
string pointer = Path.Combine(root, "version.ini");
var protectedFiles = new Dictionary<string, byte[]>
{
    [pointer] = "exe_path=app-old/HoYoShadeHub.exe\r\n"u8.ToArray(),
    [Path.Combine(root, "config.ini")] = "keep-user-config"u8.ToArray(),
    [Path.Combine(root, "OptiScaler", "profiles", "hk4e_cn.ini")] = "[NR]\nQuality=user-value\n"u8.ToArray(),
    [Path.Combine(root, "app-old", "keep.txt")] = "rollback-app"u8.ToArray(),
};
foreach (var (path, bytes) in protectedFiles)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
}
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
using (ZipArchive zip = ZipFile.OpenRead(archive))
{
    try
    {
        await new GithubUpdateService().ApplyAsync(archive);
        Check(!expectRejection, "Original package must reproduce the old validator failure.");
    }
    catch (InvalidDataException error) when (expectRejection)
    {
        Check(error.Message == "version.ini must point to app-*/HoYoShadeHub.exe.", "Rejection must match the reported error.");
        Check(File.ReadAllBytes(pointer).SequenceEqual(protectedFiles[pointer]), "Rejected update must retain the old activation pointer.");
        Check(!File.Exists(Path.Combine(root, "F6Launcher.exe")), "Rejected update must not install payloads.");
        Console.WriteLine("Reproduced: " + error.Message);
    }
    if (!expectRejection)
    {
        byte[] expectedPointer;
        using (var stream = new MemoryStream())
        {
            using Stream input = zip.GetEntry("version.ini")!.Open();
            input.CopyTo(stream);
            expectedPointer = stream.ToArray();
        }
        Check(File.ReadAllBytes(pointer).SequenceEqual(expectedPointer), "Successful update must activate the exact package pointer.");
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/') && e.FullName != "version.ini"))
        {
            string path = Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            using var expected = entry.Open();
            using var actual = File.OpenRead(path);
            Check(SHA256.HashData(expected).SequenceEqual(SHA256.HashData(actual)), "Installed content differs: " + entry.FullName);
        }
        Check(File.Exists(Path.Combine(root, "F6Launcher.exe")) && !File.Exists(Path.Combine(root, "HoYoShadeHub.exe")), "Only F6Launcher.exe belongs at the portable root.");
    }
}
foreach (var (path, bytes) in protectedFiles.Where(p => p.Key != pointer))
    Check(File.ReadAllBytes(path).SequenceEqual(bytes), "User settings or rollback files changed: " + path);
Console.WriteLine($"Portable update integration: PASS {checks}; legacyRejection={expectRejection}; gameTested=false; uiTested=false.");
