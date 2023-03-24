using System.Collections.Concurrent;

namespace Netcorext.Extensions.Threading;

public class KeyLocker
{
    private static readonly ConcurrentDictionary<string, KeyLockerState<bool>> Lockers = new();

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

            await Task.Delay(1, cancellationToken);
        }
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