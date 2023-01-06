using System.Collections.Concurrent;
using System.Diagnostics;

namespace Netcorext.Extensions.Threading;

public class KeyLocker
{
    private static readonly ConcurrentDictionary<string, bool> CacheLockers = new();

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
        Stopwatch stopwatch = null!;
        
        if (timeout.HasValue)
            stopwatch = Stopwatch.StartNew();

        while (!CacheLockers.TryAdd(key, true) && CacheLockers.TryGetValue(key, out var locker) && locker)
        {
            if (timeout.HasValue && stopwatch.Elapsed > timeout)
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
        return CacheLockers.TryRemove(key, out _) || !CacheLockers.ContainsKey(key);
    }
    
    public bool ReleaseAll(string key)
    {
        return CacheLockers.TryUpdate(key, false, true);
    }
}