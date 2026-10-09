namespace Personicore.DeviceAgent.Agent;

/// <summary>Shared cadence for both trays. UI owns the timer and notification delivery.</summary>
public sealed class ProxyReminder(TimeProvider? clock = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long? _disabledSince;

    // A wanted proxy paused away from home does not need to be switched on again.
    public bool ShouldNotify(bool available, bool wanted)
    {
        if (!available || wanted)
        {
            _disabledSince = null;
            return false;
        }

        var now = _clock.GetTimestamp();
        if (_disabledSince is null)
        {
            _disabledSince = now;
            return false;
        }
        if (_clock.GetElapsedTime(_disabledSince.Value, now) < Interval) return false;
        _disabledSince = now;
        return true;
    }
}
