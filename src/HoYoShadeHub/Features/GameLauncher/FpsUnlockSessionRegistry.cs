// A CLI batch may finish while the game still reads its FPS target from this process.
// Register ownership before starting work, expose attach readiness separately, and drain at CLI exit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

internal sealed class FpsUnlockSessionRegistry
{
    internal sealed record AttachResult(bool Armed, string Detail);

    internal sealed class Session
    {
        private readonly TaskCompletionSource<AttachResult> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AttachResult> Ready => _ready.Task;
        public Task Completion => _completed.Task;

        public void ReportAttached(bool armed, string detail) => _ready.TrySetResult(new(armed, detail));

        internal async Task RunAsync(Func<Session, Task> run)
        {
            try
            {
                await run(this).ConfigureAwait(false);
                _ready.TrySetResult(new(false, "帧率解锁会话在报告附加结果前结束。"));
                _completed.TrySetResult();
            }
            catch (Exception error)
            {
                _ready.TrySetResult(new(false, error.Message));
                // Preserve errors after armed too: a completed batch must not silently lose its monitor.
                _completed.TrySetException(error);
            }
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public bool HasActiveSessions
    {
        get { lock (_gate) return _sessions.Values.Any(s => !s.Completion.IsCompleted); }
    }

    public Session GetOrStart(string processIdentity, Func<Session, Task> run)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(processIdentity, out Session? existing)) return existing;
            var session = new Session();
            _sessions.Add(processIdentity, session);
            _ = Task.Run(() => session.RunAsync(run));
            return session;
        }
    }

    /// <summary>Called after all CLI steps, so a launch/wait/stop batch can still complete its stop step.</summary>
    public async Task DrainAsync()
    {
        List<Exception> failures = new();
        while (true)
        {
            KeyValuePair<string, Session>[] snapshot;
            lock (_gate) snapshot = _sessions.ToArray();
            if (snapshot.Length == 0)
            {
                if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
                if (failures.Count > 1) throw new AggregateException("帧率解锁会话异常。", failures);
                return;
            }
            try
            {
                await Task.WhenAll(snapshot.Select(p => p.Value.Completion)).ConfigureAwait(false);
            }
            catch
            {
                // A failed monitor must not let a later registered owner disappear at CLI exit.
                foreach (var entry in snapshot)
                    if (entry.Value.Completion.Exception is { } error)
                        failures.AddRange(error.InnerExceptions);
            }
            finally
            {
                lock (_gate)
                    foreach (var entry in snapshot)
                        if (entry.Value.Completion.IsCompleted) _sessions.Remove(entry.Key);
            }
        }
    }
}
