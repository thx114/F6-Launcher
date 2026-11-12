namespace HoYoShadeHub.Extensions.Services;

/// <summary>Elapsed time since the dialog actually opened; timer tick counts cannot unlock approval early.</summary>
public sealed class ConfirmationDelay(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _opened;
    public void Start() => _opened = _time.GetTimestamp();
    public bool CanApply => _opened is { } start && _time.GetElapsedTime(start) >= TimeSpan.FromSeconds(3);
    public int RemainingSeconds => _opened is { } start
        ? Math.Max(0, (int)Math.Ceiling(3 - _time.GetElapsedTime(start).TotalSeconds)) : 3;
}
