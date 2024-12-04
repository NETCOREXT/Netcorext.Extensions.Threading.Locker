using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Netcorext.Extensions.Threading;

/// <summary>
/// Provides a key-based locking mechanism for controlling concurrent access to resources.
/// </summary>
public class KeyLocker : IDisposable
{
    private volatile int _disposed;
    private readonly int _maxConcurrent;
    private readonly TimeSpan? _timeout;
    private readonly TimeSpan _cleanupInterval;
    private readonly bool _throwTimeoutException;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, KeyState> _locks = new();
    private readonly Timer _cleanupTimer;

    /// <summary>
    /// Initializes a new instance of the KeyLocker class.
    /// </summary>
    /// <param name="logger">The logger instance for recording events.</param>
    /// <param name="timeout">Optional timeout for wait operations.</param>
    /// <param name="throwTimeoutException">Whether to throw an exception on timeout.</param>
    /// <param name="maxConcurrent">Maximum number of concurrent operations allowed.</param>
    /// <param name="cleanupInterval">Interval for cleaning up expired locks. Default is 5 minutes.</param>
    public KeyLocker(ILogger logger, TimeSpan? timeout = null, bool throwTimeoutException = false, int maxConcurrent = 1, TimeSpan? cleanupInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout;
        _throwTimeoutException = throwTimeoutException;
        _maxConcurrent = maxConcurrent > 0 ? maxConcurrent : throw new ArgumentException("maxConcurrent must be greater than 0", nameof(maxConcurrent));
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(5);
        _cleanupTimer = new Timer(CleanupIdleLocks, null, _cleanupInterval, _cleanupInterval);
    }

    public void Wait(string key)
    {
        var keyState = _locks.AddOrUpdate(key, CreateLockItem, (_, state) =>
                                                               {
                                                                   lock (state)
                                                                   {
                                                                       if (!state.ReleaseAll && !state.Cancellation.IsCancellationRequested)
                                                                           state.IncrementConcurrent();

                                                                       state.LastWaitingTime = DateTimeOffset.UtcNow;
                                                                   }

                                                                   return state;
                                                               });

        try
        {
            if (keyState.ReleaseAll)
                return;

            if (_timeout.HasValue)
            {
                if (keyState.Semaphore.Wait(_timeout.Value, keyState.Cancellation.Token))
                    return;

                HandleTimeout(keyState);
            }
            else
            {
                keyState.Semaphore.Wait(keyState.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task WaitAsync(string key)
    {
        var keyState = _locks.AddOrUpdate(key, CreateLockItem, (_, state) =>
                                                               {
                                                                   lock (state)
                                                                   {
                                                                       if (!state.ReleaseAll && !state.Cancellation.IsCancellationRequested)
                                                                           state.IncrementConcurrent();

                                                                       state.LastWaitingTime = DateTimeOffset.UtcNow;
                                                                   }

                                                                   return state;
                                                               });

        try
        {
            if (keyState.ReleaseAll)
                return;

            if (_timeout.HasValue)
            {
                if (await keyState.Semaphore.WaitAsync(_timeout.Value, keyState.Cancellation.Token))
                    return;

                HandleTimeout(keyState);
            }
            else
            {
                await keyState.Semaphore.WaitAsync(keyState.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public int Release(string key)
    {
        if (!_locks.TryGetValue(key, out var keyState))
            return 0;

        lock (keyState)
        {
            if (keyState.ReleaseAll || keyState.Cancellation.IsCancellationRequested)
                return 0;

            try
            {
                keyState.LastWaitingTime = DateTimeOffset.UtcNow;
                keyState.Semaphore.Release();
                keyState.DecrementConcurrent();

                return 1;
            }
            catch (ObjectDisposedException) { }
            catch (ArgumentOutOfRangeException) { }
            catch (SemaphoreFullException) { }
        }

        return 0;
    }

    public int ReleaseAll(string key)
    {
        if (!_locks.TryGetValue(key, out var keyState))
            return 0;

        lock (keyState)
        {
            if (keyState.ReleaseAll || keyState.Cancellation.IsCancellationRequested)
                return 0;

            var releaseCount = 0;

            try
            {
                while (true)
                {
                    try
                    {
                        keyState.ReleaseAll = true;
                        keyState.LastWaitingTime = DateTimeOffset.UtcNow;
                        keyState.Semaphore.Release();
                        keyState.DecrementConcurrent();
                        releaseCount++;
                    }
                    catch (SemaphoreFullException)
                    {
                        keyState.Cancellation.Cancel(true);

                        break;
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (ArgumentOutOfRangeException) { }

            return releaseCount;
        }
    }

    public void Reset(string key)
    {
        if (!_locks.TryRemove(key, out var keyState))
            return;

        lock (keyState)
        {
            try
            {
                while (true)
                {
                    try
                    {
                        keyState.Semaphore.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                        keyState.Cancellation.Cancel(true);

                        break;
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (ArgumentOutOfRangeException) { }

            keyState.Dispose();
        }
    }

    public int GetWaitingCount(string key)
    {
        return _locks.TryGetValue(key, out var keyState) ? keyState.WaitingConcurrent : 0;
    }

    public bool HasLock(string key)
    {
        return _locks.ContainsKey(key);
    }

    private void HandleTimeout(KeyState keyState)
    {
        _logger.LogWarning("Lock on key '{Key}' timed out after {Timeout}", keyState.Key, _timeout);

        Release(keyState.Key);

        if (_throwTimeoutException)
            throw new TimeoutException($"Lock operation timed out for key: {keyState.Key}");
    }

    private void CleanupIdleLocks(object? state)
    {
        var now = DateTimeOffset.UtcNow;

        var idledKeys = _locks
                       .Where(kvp => now - kvp.Value.LastWaitingTime > _cleanupInterval && !kvp.Value.ReleaseAll)
                       .Select(kvp => kvp.Key)
                       .ToArray();

        foreach (var key in idledKeys)
        {
            _logger.LogInformation("Cleaning up idled lock for key '{Key}'", key);

            if (!_locks.TryRemove(key, out var keyState)) continue;

            keyState.Cancellation.Cancel();
            keyState.Semaphore.Dispose();
        }
    }

    private KeyState CreateLockItem(string key)
    {
        var keyState = new KeyState
                       {
                           Key = key,
                           Semaphore = new SemaphoreSlim(_maxConcurrent, _maxConcurrent),
                           Cancellation = new CancellationTokenSource(),
                           LastWaitingTime = DateTimeOffset.UtcNow,
                           ReleaseAll = false
                       };

        return keyState;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer.Change(TimeSpan.Zero, TimeSpan.Zero);
        _cleanupTimer.Dispose();

        foreach (var keyState in _locks.Values)
        {
            try
            {
                keyState.Cancellation.Cancel();
                keyState.Semaphore.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during KeyLocker disposal");
            }
        }

        _locks.Clear();
        GC.SuppressFinalize(this);
    }
}
