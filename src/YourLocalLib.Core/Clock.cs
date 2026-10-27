namespace YourLocalLib.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class ManualClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
}
