using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Netcorext.Extensions.Threading;

public class KeyLocker : IDisposable
{
    private bool _disposed;
    private readonly int _maxConcurrent;
    private readonly TimeSpan? _timeout;
    private readonly bool _throwTimeoutException;
    private readonly TimeSpan? _expired;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, KeyState> _locks = new();
    private readonly ConcurrentDictionary<string, Timer> _timers = new();

    public KeyLocker(ILogger logger, TimeSpan? timeout = null, bool throwTimeoutException = false, TimeSpan? expired = null, int maxConcurrent = 1)
    {
        _maxConcurrent = maxConcurrent;
        _timeout = timeout;
        _throwTimeoutException = throwTimeoutException;
        _expired = expired ?? TimeSpan.FromMilliseconds(10 * 60 * 1000);
        _logger = logger;
    }

    public void Wait(string key)
    {
        var keyState = _locks.AddOrUpdate(key, CreateLockItem, (k, state) =>
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

            if (_expired.HasValue && _timers.TryAdd(key, new Timer(HandleExpired, key, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)))
                _timers[key].Change(_expired.Value, _expired.Value);

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
        var keyState = _locks.AddOrUpdate(key, CreateLockItem, (k, state) =>
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

            if (_expired.HasValue && _timers.TryAdd(key, new Timer(HandleExpired, key, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)))
                _timers[key].Change(_expired.Value, _expired.Value);

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

            keyState.ReleaseAll = true;

            var releaseCount = 0;

            try
            {
                while (true)
                {
                    try
                    {
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
        if (!_locks.TryGetValue(key, out var keyState))
            return;

        lock (keyState)
        {
            keyState.LastWaitingTime = DateTimeOffset.UtcNow;
            keyState.Cancellation = new CancellationTokenSource();
            keyState.ReleaseAll = false;
        }

        if (!_timers.TryRemove(key, out var timer))
            return;

        lock (timer)
        {
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            timer.Dispose();
        }
    }

    public int GetWaitingCount(string key)
    {
        return _locks.TryGetValue(key, out var keyState) ? keyState.WaitingConcurrent : 0;
    }

    private void HandleTimeout(KeyState keyState)
    {
        _logger.LogWarning("Lock on key '{Key}' timed out", keyState.Key);

        Release(keyState.Key);

        if (_throwTimeoutException)
            throw new TimeoutException($"Lock on key '{keyState.Key}' timed out.");
    }

    private void HandleExpired(object? state)
    {
        if (state is not string key || !_locks.TryGetValue(key, out var keyState))
            return;

        var elapsed = DateTimeOffset.UtcNow.Subtract(keyState.LastWaitingTime);

        if (!_expired.HasValue || !(elapsed >= _expired))
            return;

        lock (keyState)
        {
            if (!_locks.TryRemove(keyState.Key, out _))
                return;

            _logger.LogWarning("Key '{Key}' expired({Elapsed}), has been removed", keyState.Key, elapsed);

            if (_timers.TryRemove(keyState.Key, out var timer))
            {
                lock (timer)
                {
                    timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    timer.Dispose();
                }
            }

            keyState.Dispose();
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
        if (_disposed)
            return;

        _disposed = true;

        foreach (var key in _locks.Keys)
        {
            if (_locks.TryRemove(key, out var keyState))
                keyState.Dispose();
        }

        foreach (var key in _timers.Keys)
        {
            if (_timers.TryRemove(key, out var timer))
                timer.Dispose();
        }
    }
}
