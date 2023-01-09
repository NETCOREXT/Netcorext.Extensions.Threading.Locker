using System.Collections.Concurrent;
using System.Diagnostics;

namespace Netcorext.Extensions.Threading;

public class KeyLocker
{
    private static readonly ConcurrentDictionary<string, bool> Lockers = new();

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

        while (!Lockers.TryAdd(key, true) && Lockers.TryGetValue(key, out var locker) && locker)
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
        return Lockers.TryRemove(key, out _) || !Lockers.ContainsKey(key);
    }
    
    public bool ReleaseAll(string key)
    {
        return Lockers.TryUpdate(key, false, true);
    }
}