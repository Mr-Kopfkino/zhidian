using Qiban.Core;

namespace Qiban.Desktop;

/// <summary>Clock and desktop observations used by the dispatcher-driven scheduler.</summary>
public sealed class InitiativeEnvironment
{
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<bool> IsSessionAvailable { get; init; }
    public Func<ActivityObservation?> ObserveActivity { get; init; }
    public Random Random { get; init; } = System.Random.Shared;

    public InitiativeEnvironment()
    {
        var monitor = new DesktopActivityMonitor();
        IsSessionAvailable = () => monitor.IsSessionAvailable;
        ObserveActivity = monitor.Sample;
    }
}
