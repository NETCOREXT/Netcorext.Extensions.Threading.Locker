using System.Collections.Concurrent;

namespace Netcorext.Extensions.Threading;

public class KeyCountLocker
{
    private readonly long _minimum;
    private readonly long? _maximum;
    private static readonly ConcurrentDictionary<string, KeyLockerState<long>> CountLockers = new();
    private static readonly KeyLocker Locker = new();

    public KeyCountLocker(long minimum = 1, long? maximum = null)
    {
        _minimum = minimum;
        _maximum = maximum;
    }

    public async Task<bool> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        var newLocker = new KeyLockerState<long>
                        {
                            State = _minimum
                        };
        try
        {
            await Locker.WaitAsync(key, cancellationToken);

            if (!CountLockers.TryGetValue(key, out var oldLocker))
                return CountLockers.TryAdd(key, newLocker);

            if (oldLocker.State + 1 > _maximum)
                return false;
            
            newLocker.State = oldLocker.State + 1;
            
            return CountLockers.TryUpdate(key, newLocker, oldLocker);
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

            if (!CountLockers.TryGetValue(key, out var oldLocker))
                return true;
            
            var newLocker = new KeyLockerState<long>
                            {
                                State = oldLocker.State - 1
                            };

            if (newLocker.State > _minimum && CountLockers.TryUpdate(key, newLocker, oldLocker))
                return true;

            return newLocker.State <= _minimum && CountLockers.TryRemove(key, out _);
        }
        finally
        {
            Locker.Release(key);
        }
    }

    public Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!CountLockers.TryGetValue(key, out var locker))
            return Task.FromResult(_minimum);

        return Task.FromResult(locker.State);
    }
}