namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// UI-thread-owned snapshots for a picker: late reads cannot replace a newer selection,
/// and an open popup retains its item containers until it closes.
/// The owner schedules TryTake outside selection/open/close event callbacks.
/// </summary>
public sealed class DeferredPickerUpdate<T> where T : class
{
    private long _revision;
    private T? _pending;

    public long Begin()
    {
        _pending = null;
        return ++_revision;
    }

    public void Invalidate() => Begin();

    public bool Complete(long revision, T snapshot)
    {
        if (revision != _revision) return false;
        _pending = snapshot;
        return true;
    }

    public bool TryTake(bool popupOpen, out T? snapshot)
    {
        snapshot = null;
        if (popupOpen || _pending is null) return false;
        snapshot = _pending;
        _pending = null;
        return true;
    }
}
