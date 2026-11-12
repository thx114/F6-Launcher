using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class DeferredPickerUpdateTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("Picker refresh: " + message);
            passed++;
        }

        var updates = new DeferredPickerUpdate<string>();
        Check(!updates.TryTake(false, out _), "empty refresh produced a snapshot");
        long read = updates.Begin();
        Check(updates.Complete(read, "catalog-A"), "current read was discarded");
        for (int i = 0; i < 100; i++)
            if (updates.TryTake(true, out _)) throw new InvalidOperationException("Open popup's containers can be replaced");
        Check(updates.TryTake(false, out var snapshot) && snapshot == "catalog-A", "closing popup lost pending snapshot");
        Check(!updates.TryTake(false, out _), "snapshot applied twice");

        read = updates.Begin();
        updates.Complete(read, "old selection");
        updates.Invalidate(); // user selects while the open popup is waiting for refreshed data
        Check(!updates.TryTake(false, out _), "closing popup restored an old user selection");
        Check(!updates.Complete(read, "late old selection"), "late catalog overrode user's choice");

        long older = updates.Begin(), newer = updates.Begin();
        Check(updates.Complete(newer, "new game"), "latest game failed to publish");
        Check(!updates.Complete(older, "old game"), "older game accepted late result");
        Check(updates.TryTake(false, out snapshot) && snapshot == "new game", "older completion erased new game");

        read = updates.Begin();
        updates.Complete(read, "unloaded page");
        updates.Invalidate(); // unloaded owner
        Check(!updates.TryTake(false, out _), "unloaded page kept a queued snapshot");
        long reloaded = updates.Begin();
        Check(!updates.Complete(read, "old attachment") && updates.Complete(reloaded, "new attachment"),
            "reloaded control accepted old attachment");
        Check(updates.TryTake(false, out snapshot) && snapshot == "new attachment", "new attachment could not bind");

        read = updates.Begin();
        updates.Complete(read, "queued refresh");
        long replacement = updates.Begin();
        Check(!updates.TryTake(false, out _), "new request retained obsolete queued refresh");
        updates.Complete(replacement, "replacement");
        Check(!updates.TryTake(true, out _), "reopened popup accepted an item replacement");
        Check(updates.TryTake(false, out snapshot) && snapshot == "replacement", "replacement lost after reopening");
        Console.WriteLine($"PASS {passed} deferred picker snapshot checks (managed scheduling only)");
        return passed;
    }
}
