namespace SvuSessionsLauncher.Services;

/// <summary>
/// Process-wide display preferences that affect how values are rendered
/// (currently the clock format). Kept as a static so lightweight value
/// converters can read it without a service reference. Raises
/// <see cref="Changed"/> so views can re-render when a preference flips.
/// </summary>
public static class DisplayOptions
{
    private static bool _use24HourClock = true;

    /// <summary>When true times are shown on the 24-hour clock, otherwise 12-hour (AM/PM).</summary>
    public static bool Use24HourClock
    {
        get => _use24HourClock;
        set
        {
            if (_use24HourClock == value)
            {
                return;
            }

            _use24HourClock = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised after a display preference changes.</summary>
    public static event Action? Changed;
}
