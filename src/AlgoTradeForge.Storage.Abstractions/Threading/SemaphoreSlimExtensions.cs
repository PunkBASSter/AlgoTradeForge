namespace AlgoTradeForge.Storage.Threading;

public static class SemaphoreSlimExtensions
{
    // Lets callers use `using var _ = await gate.LockAsync(ct);` instead of try/finally
    // around WaitAsync/Release. SemaphoreSlim has no built-in scope-release pattern because
    // it doubles as a counting primitive; this releaser is for the mutex (initialCount=1) case.
    public static async Task<IDisposable> LockAsync(this SemaphoreSlim semaphore, CancellationToken ct = default)
    {
        await semaphore.WaitAsync(ct);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
