using System.Collections.Concurrent;

namespace Netcorext.Extensions.Threading;

public class KeyCountLocker
{
    private readonly long _minimum;
    private readonly long? _maximum;
    private static readonly ConcurrentDictionary<string, long> CountLockers = new();
    private static readonly KeyLocker Locker = new();

    public KeyCountLocker(long minimum = 1, long? maximum = null)
    {
        _minimum = minimum;
        _maximum = maximum;
    }
    
    public async Task<bool> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await Locker.WaitAsync(key, cancellationToken);
        
            if (CountLockers.TryAdd(key, _minimum)) return true;
        
            if (!CountLockers.TryGetValue(key, out var count))
                if (CountLockers.TryAdd(key, _minimum)) return true;

            if (_maximum.HasValue && count + 1 > _maximum) return false;
            
            if (CountLockers.TryUpdate(key, count + 1, count)) return true;

            return false;
        }
        finally
        {
            Locker.Release(key);
        }
    }
    
    public async Task<bool> DecrementAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await Locker.WaitAsync(key, cancellationToken);
        
            if (!CountLockers.TryGetValue(key, out var count)) return true;

            if (count - 1 > _minimum && CountLockers.TryUpdate(key, count - 1, count))
                return true;
            
            if (count - 1 <= _minimum && CountLockers.TryRemove(key, out _))
                return true;

            return false;
        }
        finally
        {
            Locker.Release(key);
        }
    }

    public Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!CountLockers.TryGetValue(key, out var count))
            return Task.FromResult(_minimum);

        return Task.FromResult(count);
    }
}