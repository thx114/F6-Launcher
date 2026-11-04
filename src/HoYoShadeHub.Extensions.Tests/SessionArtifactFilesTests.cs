using HoYoShadeHub.Extensions.Diagnostics;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class SessionArtifactFilesTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL " + name);
            passed++;
            Console.WriteLine("PASS " + name);
        }
        string root = Path.Combine(Path.GetTempPath(), "session-artifact-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime started = DateTime.UtcNow.AddMinutes(-2);
            foreach (string name in new[] { "ReShade.log", "ReShade2.log", "Dx11FsrBridge.log", "FsrBridgeDepthAddon.log", "OptiScaler.log" })
            {
                string source = Path.Combine(root, name), target = source + ".snapshot";
                File.WriteAllText(source, "previous-session");
                File.SetLastWriteTimeUtc(source, started.AddSeconds(-1));
                Check(!SessionArtifactFiles.IsCurrent(source, started, DateTime.UtcNow), name + " rejects previous-session timestamp");
                Check(SessionArtifactFiles.CopySnapshot(source, target, 1024, started).Written == 0 && !File.Exists(target), name + " does not create snapshot for old log");
                File.SetLastWriteTimeUtc(source, started);
                Check(!SessionArtifactFiles.IsCurrent(source, started, DateTime.UtcNow)
                    && SessionArtifactFiles.CopySnapshot(source, target, 1024, started).Written == 0 && !File.Exists(target), name + " rejects exact launch timestamp");
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(2));
                Check(!SessionArtifactFiles.IsCurrent(source, started, DateTime.UtcNow)
                    && SessionArtifactFiles.CopySnapshot(source, target, 1024, started).Written == 0, name + " rejects future timestamp");
                File.WriteAllText(source, "current-session");
                Check(SessionArtifactFiles.IsCurrent(source, started, DateTime.UtcNow)
                    && SessionArtifactFiles.CopySnapshot(source, target, 1024, started).Written == 15
                    && File.ReadAllText(target) == "current-session", name + " copies current log");
                File.WriteAllText(source + ".replacement", "old-file-replaced-after-discovery");
                File.SetLastWriteTimeUtc(source + ".replacement", started.AddMinutes(-1));
                File.Move(source + ".replacement", source, true);
                Check(SessionArtifactFiles.CopySnapshot(source, target, 1024, started).Written == 0
                    && File.ReadAllText(target) == "current-session", name + " rechecks source at copy and preserves valid snapshot");
            }
            Check(!SessionArtifactFiles.IsCurrent(Path.Combine(root, "missing.log"), started, DateTime.UtcNow), "missing log is not current");
            string config = Path.Combine(root, "OptiScaler.ini"), configCopy = config + ".snapshot";
            File.WriteAllText(config, "preserved-quality-and-resolution");
            File.SetLastWriteTimeUtc(config, started.AddDays(-1));
            SessionArtifactFiles.CopySnapshot(config, configCopy, 1024);
            Check(File.ReadAllText(configCopy) == File.ReadAllText(config), "old configuration still collected independently from log freshness");
            string large = Path.Combine(root, "large.log"), tail = large + ".snapshot";
            File.WriteAllText(large, "old-line\npartial-boundary\nlast-line\n");
            var copied = SessionArtifactFiles.CopySnapshot(large, tail, 16, started);
            Check(copied.Tail && copied.Written <= 16 && File.ReadAllText(tail) == "last-line\n", "bounded log tail skips partial leading line");
            string sourceDump = Path.Combine(root, "OptiScaler_crash_1.dmp");
            string archived = Path.Combine(root, "log/sessions/current/opti-dumps/crash.dmp");
            File.WriteAllBytes(sourceDump, [1, 2, 3, 4]);
            string alias = sourceDump + ".identity";
            HysxFileLink.LinkOrCopy(sourceDump, alias);
            DateTime modified = File.GetLastWriteTimeUtc(sourceDump);
            SessionArtifactFiles.ArchiveCompletedDump(sourceDump, archived);
            Check(!File.Exists(sourceDump) && File.ReadAllBytes(archived).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "completed dump moved into log with full contents");
            Check(HysxFileLink.AreHardLinked(alias, archived) && File.GetLastWriteTimeUtc(archived) == modified, "same-volume dump archive preserves file identity and modification time");
            File.WriteAllBytes(sourceDump, [5, 6]);
            bool collided = false;
            try { SessionArtifactFiles.ArchiveCompletedDump(sourceDump, archived); } catch (IOException) { collided = true; }
            Check(collided && File.ReadAllBytes(sourceDump).SequenceEqual(new byte[] { 5, 6 }) && new FileInfo(archived).Length == 4, "archive collision preserves both source and prior evidence");
            string writing = Path.Combine(root, "OptiScaler_crash_writing.dmp");
            string writingTarget = archived + ".writing";
            bool locked = false;
            using (var writer = new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                writer.WriteByte(1); writer.Flush();
                try { SessionArtifactFiles.ArchiveCompletedDump(writing, writingTarget); } catch (IOException) { locked = true; }
                Check(locked && File.Exists(writing) && !File.Exists(writingTarget), "actively written dump remains at source for retry");
            }
            SessionArtifactFiles.ArchiveCompletedDump(writing, writingTarget);
            Check(!File.Exists(writing) && new FileInfo(writingTarget).Length == 1, "closed dump succeeds on retry");
            string empty = Path.Combine(root, "empty.dmp"); File.WriteAllBytes(empty, []);
            bool rejected = false;
            try { SessionArtifactFiles.ArchiveCompletedDump(empty, archived + ".empty"); } catch (IOException) { rejected = true; }
            Check(rejected && File.Exists(empty) && !File.Exists(archived + ".empty"), "empty dump is not archived as completed evidence");
            if (!HysxFileLink.SameVolume(root, AppContext.BaseDirectory))
            {
                string crossDirectory = Path.Combine(AppContext.BaseDirectory, "dump-archive-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(crossDirectory);
                try
                {
                    string crossSource = Path.Combine(root, "cross-volume.dmp");
                    string crossTarget = Path.Combine(crossDirectory, "archived.dmp");
                    File.WriteAllBytes(crossSource, [8, 9, 10]);
                    DateTime crossTime = File.GetLastWriteTimeUtc(crossSource);
                    SessionArtifactFiles.ArchiveCompletedDump(crossSource, crossTarget);
                    Check(!File.Exists(crossSource) && File.ReadAllBytes(crossTarget).SequenceEqual(new byte[] { 8, 9, 10 })
                        && File.GetLastWriteTimeUtc(crossTarget) == crossTime, "cross-volume archive verifies contents and preserves timestamp before removing source");
                    File.WriteAllBytes(crossSource, [11, 12]);
                    bool crossCollision = false;
                    try { SessionArtifactFiles.ArchiveCompletedDump(crossSource, crossTarget); } catch (IOException) { crossCollision = true; }
                    Check(crossCollision && File.ReadAllBytes(crossSource).SequenceEqual(new byte[] { 11, 12 })
                        && File.ReadAllBytes(crossTarget).SequenceEqual(new byte[] { 8, 9, 10 })
                        && !Directory.EnumerateFiles(crossDirectory, "*.pending").Any(), "cross-volume collision keeps source and cleans incomplete archive");
                }
                finally { Directory.Delete(crossDirectory, recursive: true); }
            }
            Check(!Directory.EnumerateFiles(root, "*.pending", SearchOption.AllDirectories).Any(), "no unfinished snapshot artifacts remain");
            Console.WriteLine($"PASS {passed} session artifact checks");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
