using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// Debug probe that gates engine execution. The engine thread blocks at bar boundaries
/// until an external command tells it to proceed.
/// Thread-safe: the engine thread blocks via <see cref="ManualResetEventSlim"/>,
/// the HTTP thread sends commands via <see cref="SendCommandAsync"/>.
/// Only one command may be pending at a time — sending a second while the first is
/// awaiting a breakpoint throws <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class GatingDebugProbe : IDebugProbe, IDisposable
{
    private readonly ManualResetEventSlim _gate = new(false); // starts closed (paused)
    private readonly object _lock = new();
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BreakCondition _breakCondition = BreakCondition.Always.Instance;
    private DebugSnapshot _lastSnapshot;
    private TaskCompletionSource<DebugSnapshot>? _snapshotWaiter;
    private bool _disposed;
    private volatile bool _running;

    public bool IsActive => true;

    public DebugSnapshot LastSnapshot
    {
        get { lock (_lock) return _lastSnapshot; }
    }

    public bool IsRunning => _running;

    public void OnRunStart()
    {
        _running = true;
        _readyTcs.TrySetResult();
        // Gate starts non-signaled — block until first command arrives
        _gate.Wait();
    }

    public void OnBarProcessed(DebugSnapshot snapshot)
    {
        lock (_lock) _lastSnapshot = snapshot;

        if (Volatile.Read(ref _breakCondition).ShouldBreak(snapshot))
        {
            // Reset BEFORE notifying so that if the caller's continuation
            // immediately calls SendCommandAsync → _gate.Set(), the Set
            // is not clobbered by a subsequent Reset.
            _gate.Reset();
            NotifyWaiters(snapshot);
            _gate.Wait(); // if Set was already called, passes immediately
        }
    }

    public void OnEventEmitted(string eventType, long sequenceNumber)
    {
        var condition = Volatile.Read(ref _breakCondition);
        if (!condition.IsEventLevel)
            return;

        if (condition.ShouldBreakOnEvent(eventType, sequenceNumber))
        {
            _gate.Reset();
            // Snapshot is bar-granularity: it reflects the last completed bar, not the
            // mid-bar event that triggered this break. The event's own sequenceNumber
            // is available via the EventBus stream (the "sq" field in emitted JSON).
            NotifyWaiters(_lastSnapshot);
            _gate.Wait();
        }
    }

    public void OnRunEnd()
    {
        _running = false;
        lock (_lock)
        {
            NotifyWaitersUnsafe(_lastSnapshot);
        }
    }

    /// <summary>
    /// Called by the HTTP handler. Sets the command and unblocks the engine.
    /// Returns a task that completes when the engine hits the next break point.
    /// For <see cref="DebugCommand.Continue"/>, returns immediately.
    /// Throws <see cref="InvalidOperationException"/> if a command is already pending.
    /// </summary>
    public async Task<DebugSnapshot> SendCommandAsync(DebugCommand command, CancellationToken ct = default)
    {
        // Wait for the engine thread to reach OnRunStart before sending commands
        await _readyTcs.Task.WaitAsync(ct).ConfigureAwait(false);

        TaskCompletionSource<DebugSnapshot> waiter;
        CancellationTokenRegistration registration;
        lock (_lock)
        {
            if (!_running)
                return _lastSnapshot;

            var condition = BreakCondition.FromCommand(command);
            Volatile.Write(ref _breakCondition, condition);

            if (condition is BreakCondition.Never)
            {
                // Continue: unblock engine, return immediately with last known snapshot
                _gate.Set();
                return _lastSnapshot;
            }

            if (_snapshotWaiter is not null)
                throw new InvalidOperationException("A debug command is already pending. Wait for it to complete before sending another.");

            _snapshotWaiter = new TaskCompletionSource<DebugSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            waiter = _snapshotWaiter;
            registration = ct.Register(() => waiter.TrySetCanceled());

            _gate.Set(); // unblock engine thread
        }

        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private void NotifyWaiters(DebugSnapshot snapshot)
    {
        lock (_lock)
        {
            NotifyWaitersUnsafe(snapshot);
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>.</summary>
    private void NotifyWaitersUnsafe(DebugSnapshot snapshot)
    {
        _snapshotWaiter?.TrySetResult(snapshot);
        _snapshotWaiter = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        Volatile.Write(ref _breakCondition, BreakCondition.Never.Instance);
        _readyTcs.TrySetResult(); // unblock SendCommandAsync if engine never started
        NotifyWaiters(default);   // complete any pending TCS
        _gate.Set();              // unblock engine thread if waiting
        // Don't dispose _gate here — engine thread may still be unwinding
    }
}
