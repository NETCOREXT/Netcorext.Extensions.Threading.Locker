namespace Netcorext.Extensions.Threading;

internal class KeyState : IDisposable
{
    private int _concurrent = 1;
    private bool _disposed;

    public string Key { get; set; } = default!;
    public SemaphoreSlim Semaphore { get; set; } = default!;
    public CancellationTokenSource Cancellation { get; set; } = default!;
    public DateTimeOffset LastWaitingTime { get; set; }
    public int WaitingConcurrent => Interlocked.CompareExchange(ref _concurrent, 0, 0);
    public bool ReleaseAll { get; set; }

    public void IncrementConcurrent() => Interlocked.Increment(ref _concurrent);

    public void DecrementConcurrent() => Interlocked.Decrement(ref _concurrent);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!Cancellation.IsCancellationRequested)
            Cancellation.Cancel();

        Semaphore.Dispose();
        Cancellation.Dispose();
    }
}
