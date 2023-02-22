namespace Netcorext.Extensions.Threading;

public class KeyLockerState<T>
{
    public T? State { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public bool IsExpired => Expires.HasValue && Expires.Value < DateTimeOffset.UtcNow;
}