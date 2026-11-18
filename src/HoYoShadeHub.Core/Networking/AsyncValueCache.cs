namespace HoYoShadeHub.Core.Networking;

/// <summary>Shares refresh work across short-lived pages without retaining those pages.</summary>
public sealed class AsyncValueCache<T>
{
    private readonly object _gate = new();
    private Task<T>? _pending;
    private T? _value;
    private bool _hasValue;
    private DateTimeOffset _expires;
    private long _revision;

    public Task<T> GetAsync(Func<Task<T>> factory, TimeSpan lifetime, bool force = false)
    {
        lock (_gate)
        {
            if (!force && _hasValue && DateTimeOffset.UtcNow < _expires) return Task.FromResult(_value!);
            if (_pending is { IsCompleted: false }) return _pending;
            long revision = _revision;
            return _pending = LoadAsync(factory, lifetime, revision);
        }
    }

    public void Invalidate()
    { lock (_gate) { _revision++; _hasValue = false; _pending = null; } }

    private async Task<T> LoadAsync(Func<Task<T>> factory, TimeSpan lifetime, long revision)
    {
        // Run outside the caller's UI stack, including the synchronous part of the factory.
        var value = await Task.Run(factory).ConfigureAwait(false);
        lock (_gate)
            if (revision == _revision)
            { _value = value; _hasValue = true; _expires = DateTimeOffset.UtcNow + lifetime; }
        return value;
    }
}
