namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>What the history says about a device running out of room.</summary>
public enum ForecastVerdict
{
    /// <summary>Not enough history yet. No figure is shown, because none would mean anything.</summary>
    Collecting = 0,

    /// <summary>Filling at a rate that gives a date.</summary>
    Filling = 1,

    /// <summary>Flat or emptying. There is no saturation to predict.</summary>
    Steady = 2,

    /// <summary>Growing, but the trend sits inside the noise. Saying a number would invent one.</summary>
    Inconclusive = 3,
}

/// <summary>One stored reading of a device's free space.</summary>
/// <param name="TakenAt">When it was measured.</param>
/// <param name="FreeBytes">What was free.</param>
public sealed record CapacityReading(DateTimeOffset TakenAt, long FreeBytes);

/// <summary>What the forecast concluded.</summary>
/// <param name="Verdict">Which of the four situations applies.</param>
/// <param name="DaysRemaining">Days until the usable space runs out. Null unless filling.</param>
/// <param name="UsableFreeBytes">
/// What can actually still be written, which on ZFS is less than the free space reported.
/// </param>
/// <param name="DailyChangeBytes">The robust daily consumption. Negative when space is being freed.</param>
/// <param name="ReadingCount">How many readings the conclusion rests on.</param>
public sealed record CapacityForecast(
    ForecastVerdict Verdict,
    int? DaysRemaining,
    long UsableFreeBytes,
    long DailyChangeBytes,
    int ReadingCount);

/// <summary>
/// Works out when a device runs out of room.
/// <para>
/// Not a least-squares fit, which is what most tooling reaches for. Least squares has no
/// resistance to a single outlier, and one overnight import of half a terabyte is exactly that
/// outlier; it would drag the slope wherever it likes. The daily deltas are reduced with a median
/// instead, in the spirit of the Theil-Sen estimator, which tolerates roughly a third of the
/// points being corrupted before its answer moves. One import in a month of readings then changes
/// nothing.
/// </para>
/// <para>
/// It also refuses to answer more often than tooling usually does. Prometheus will happily return
/// a date from two samples, where no interval is even computable; Netdata and Zabbix return a
/// sentinel rather than a date when the trend is flat or negative. This follows the second habit
/// and not the first.
/// </para>
/// </summary>
public static class CapacityForecaster
{
    /// <summary>
    /// Readings needed before any figure is shown.
    /// <para>
    /// Two weeks, so that two full weekly cycles are covered. Weekend imports and Sunday backups
    /// make a five-day sample say whatever the week happened to look like.
    /// </para>
    /// </summary>
    internal const int MinimumReadings = 14;

    /// <summary>
    /// How far the noise may exceed the trend before the trend is called inconclusive.
    /// <para>
    /// Compared against the median absolute deviation, which is the robust counterpart of the
    /// median used for the slope itself. A trend smaller than the day-to-day scatter is not a
    /// trend, and turning it into a date would be inventing precision.
    /// </para>
    /// </summary>
    internal const double NoiseTolerance = 3.0;

    /// <summary>Beyond this, "filling" stops being a useful thing to say.</summary>
    internal const int HorizonDays = 3650;

    /// <summary>
    /// Shortest gap between two readings that says anything about a daily trend, in days.
    /// <para>
    /// Six hours. Below that, dividing by the elapsed time turns ordinary noise into a headline
    /// rate: a 1.4 GB move measured over three hours reads as eleven gigabytes a day.
    /// </para>
    /// </summary>
    internal const double MinimumInterval = 0.25;

    /// <summary>
    /// How many long intervals are needed before the short ones can be discarded.
    /// <para>
    /// Below this the short gaps are kept, because a noisy answer beats no answer -- and a server
    /// whose task only ever runs by hand would otherwise never get a forecast at all.
    /// </para>
    /// </summary>
    internal const int MinimumLongIntervals = 5;

    /// <summary>
    /// Estimates when a device runs out of usable room.
    /// </summary>
    /// <param name="readings">Readings for one device, in any order. Duplicated days are tolerated.</param>
    /// <param name="totalBytes">The device's capacity.</param>
    /// <param name="fileSystemType">
    /// Used to decide how much of the reported free space is actually writable.
    /// </param>
    /// <returns>The forecast.</returns>
    public static CapacityForecast Estimate(
        IReadOnlyList<CapacityReading> readings,
        long totalBytes,
        string fileSystemType)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var ordered = readings.OrderBy(r => r.TakenAt).ToList();
        var usableFree = UsableFree(ordered.Count > 0 ? ordered[^1].FreeBytes : 0, totalBytes, fileSystemType);

        if (ordered.Count < MinimumReadings)
        {
            return new CapacityForecast(ForecastVerdict.Collecting, null, usableFree, 0, ordered.Count);
        }

        var deltas = DailyDeltas(ordered);
        if (deltas.Count == 0)
        {
            return new CapacityForecast(ForecastVerdict.Collecting, null, usableFree, 0, ordered.Count);
        }

        // Positive means space is being consumed. Median rather than mean, which is the whole
        // point: a single import night is one value among thirty and does not move it.
        var daily = Median(deltas);

        if (daily <= 0)
        {
            return new CapacityForecast(
                ForecastVerdict.Steady, null, usableFree, (long)daily, ordered.Count);
        }

        var scatter = Median([.. deltas.Select(d => Math.Abs(d - daily))]);
        if (scatter > daily * NoiseTolerance)
        {
            return new CapacityForecast(
                ForecastVerdict.Inconclusive, null, usableFree, (long)daily, ordered.Count);
        }

        var days = usableFree <= 0 ? 0 : (int)Math.Min(HorizonDays, Math.Floor(usableFree / daily));

        return days >= HorizonDays
            ? new CapacityForecast(ForecastVerdict.Steady, null, usableFree, (long)daily, ordered.Count)
            : new CapacityForecast(ForecastVerdict.Filling, days, usableFree, (long)daily, ordered.Count);
    }

    /// <summary>
    /// How much of the reported free space can actually be written.
    /// <para>
    /// On ZFS, not all of it. The pool holds back a slop reserve, and once free space falls into
    /// it ordinary writes fail with ENOSPC. At a pool sitting around ninety-six percent full, the
    /// reserve is most of what remains: extrapolating towards zero rather than towards the reserve
    /// overstates the time left several times over, which is worse than having no forecast.
    /// </para>
    /// </summary>
    /// <param name="freeBytes">Free space as reported.</param>
    /// <param name="totalBytes">Capacity.</param>
    /// <param name="fileSystemType">Filesystem type.</param>
    /// <returns>What is really left to write, never below zero.</returns>
    internal static long UsableFree(long freeBytes, long totalBytes, string fileSystemType) =>
        Math.Max(0, freeBytes - Reserve(totalBytes, fileSystemType));

    /// <summary>
    /// The space a filesystem holds back from ordinary writes.
    /// </summary>
    /// <param name="totalBytes">Capacity.</param>
    /// <param name="fileSystemType">Filesystem type.</param>
    /// <returns>Bytes that exist but cannot be filled.</returns>
    internal static long Reserve(long totalBytes, string fileSystemType)
    {
        if (!string.Equals(fileSystemType, "zfs", StringComparison.OrdinalIgnoreCase) || totalBytes <= 0)
        {
            return 0;
        }

        // spa_slop_shift defaults to 5, so one thirty-second of the pool, clamped between
        // spa_min_slop and spa_max_slop and never more than half the pool.
        const long MinSlop = 128L * 1024 * 1024;
        const long MaxSlop = 128L * 1024 * 1024 * 1024;

        var slop = Math.Clamp(totalBytes >> 5, MinSlop, MaxSlop);
        return Math.Min(slop, totalBytes / 2);
    }

    /// <summary>
    /// Turns readings into consumption per day.
    /// <para>
    /// Normalised by the real gap between readings rather than assumed to be one day apart, so a
    /// missed night produces one ordinary value instead of one that looks like a doubling.
    /// </para>
    /// </summary>
    /// <param name="ordered">Readings, oldest first.</param>
    /// <returns>Bytes consumed per day between consecutive readings.</returns>
    private static List<double> DailyDeltas(IReadOnlyList<CapacityReading> ordered)
    {
        var all = new List<double>(ordered.Count - 1);
        var long_ = new List<double>(ordered.Count - 1);

        for (var i = 1; i < ordered.Count; i++)
        {
            var elapsed = (ordered[i].TakenAt - ordered[i - 1].TakenAt).TotalDays;
            if (elapsed <= 0)
            {
                // Two readings at the same instant: a task run by hand next to the nightly one.
                continue;
            }

            var perDay = (ordered[i - 1].FreeBytes - ordered[i].FreeBytes) / elapsed;
            all.Add(perDay);
            if (elapsed >= MinimumInterval)
            {
                long_.Add(perDay);
            }
        }

        // Short intervals are dropped when there are enough long ones to do without them.
        // Normalising by elapsed time is right, but it turns a small change over a short gap into
        // an enormous daily rate: on the server this was written for, a run three hours after the
        // nightly one produced -10 GB/day from a 1.4 GB move. The median survived it, and still
        // carried it -- keeping the short gaps put the answer 27% away from what the nightly
        // readings alone say. That is the difference between "full in 123 days" and "in 156".
        return long_.Count >= MinimumLongIntervals ? long_ : all;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
