namespace ClaudeMeterTrayIcon;

/// <summary>How far a displayed percentage can be trusted.</summary>
internal enum Freshness
{
    /// <summary>Just read from the API. Shown in full colour.</summary>
    Fresh,

    /// <summary>
    /// The last refresh failed, or the reading is a few minutes old. The number is shown greyed out
    /// with a "?" so nobody mistakes it for a live value.
    /// </summary>
    Stale,

    /// <summary>
    /// Too old, or its window has already reset, so the old percentage is probably wrong.
    /// Shown as "--" rather than a misleading number.
    /// </summary>
    Expired,
}

internal static class FreshnessRules
{
    /// <summary>Polling is every 3 minutes, so a reading older than this means polling is not keeping up.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Past this, usage could have changed a lot, so stop showing the number at all.</summary>
    public static readonly TimeSpan ExpiredAfter = TimeSpan.FromMinutes(30);

    public static Freshness Evaluate(UsageWindow? window, DateTimeOffset? fetchedAt, bool lastRefreshFailed)
    {
        if (window?.Utilization is null || fetchedAt is null)
            return Freshness.Expired;

        // If the window has reset since we read it, usage went back to ~0 and the old number is wrong.
        if (window.ResetsAt is { } resetsAt && resetsAt <= DateTimeOffset.Now)
            return Freshness.Expired;

        TimeSpan age = DateTimeOffset.Now - fetchedAt.Value;
        if (age > ExpiredAfter)
            return Freshness.Expired;

        return lastRefreshFailed || age > StaleAfter ? Freshness.Stale : Freshness.Fresh;
    }
}
