using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Netcorext.Extensions.Threading;

public class KeyLocker
{
    public const long DEFAULT_DEAD_LOCK_TIMES = 100;

    private readonly long _deadLockTimes = DEFAULT_DEAD_LOCK_TIMES;
    private readonly ILogger? _logger;
    private static readonly ConcurrentDictionary<string, KeyLockerState<bool>> Lockers = new();

    public KeyLocker()
    { }

    public KeyLocker(long deadLockTimes = DEFAULT_DEAD_LOCK_TIMES, ILogger? logger = null)
    {
        _deadLockTimes = deadLockTimes;
        _logger = logger;
    }

    public async Task WaitAsync(string key, CancellationToken cancellationToken = default)
    {
        await WaitAsync(key, null, false, cancellationToken);
    }

    public async Task WaitAsync(string key, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await WaitAsync(key, timeout, false, cancellationToken);
    }

    public async Task WaitAsync(string key, TimeSpan? timeout = null, bool releaseAll = false, CancellationToken cancellationToken = default)
    {
        var stopwatch = new Stopwatch();

        stopwatch.Start();

        while (!Lockers.TryAdd(key, new KeyLockerState<bool>
                                    {
                                        State = true,
                                        Expires = timeout.HasValue ? DateTimeOffset.UtcNow.Add(timeout.Value) : null
                                    }) && Lockers.TryGetValue(key, out var locker) && locker.State)
        {
            if (locker.IsExpired)
            {
                if (releaseAll)
                {
                    ReleaseAll(key);
                }
                else
                {
                    Release(key);
                }
            }

            if (_logger != null && stopwatch.ElapsedMilliseconds >= _deadLockTimes && stopwatch.ElapsedMilliseconds % _deadLockTimes == 0)
                _logger.LogWarning("'{Key}' locked for too long, elapsed: {StopwatchElapsed}", key, stopwatch.Elapsed);

            await Task.Delay(1, cancellationToken);
        }

        stopwatch.Stop();
    }

    public bool Release(string key)
    {
        return Lockers.TryRemove(key, out _) || !Lockers.ContainsKey(key);
    }

    public bool ReleaseAll(string key)
    {
        var newLocker = new KeyLockerState<bool>
                        {
                            State = false
                        };

        if (!Lockers.TryGetValue(key, out var oldLocker))
            return Lockers.TryAdd(key, newLocker);

        return Lockers.TryUpdate(key, newLocker, oldLocker);
    }

    public void Prune(params string[] keys)
    {
        var keysToRemove = keys.Length == 0 ? Lockers.Keys : keys;

        foreach (var key in keysToRemove)
        {
            if (Lockers.TryGetValue(key, out var locker) && locker.IsExpired)
                Lockers.TryRemove(key, out _);
        }
    }
}
