namespace Jellyfin.Plugin.Flynn.Modules.Resources;

/// <summary>What the server is currently costing.</summary>
/// <param name="CpuPercent">
/// Share of the available CPU, ffmpeg children included. Null when only one sample exists so far,
/// since a rate needs two.
/// </param>
/// <param name="CpuCeilingCores">What the percentage is a share of.</param>
/// <param name="CpuCeilingIsQuota">
/// Whether that ceiling is an imposed quota or simply the machine's cores. The distinction matters
/// on screen: at eighty percent of a quota you are about to be throttled, at eighty percent of a
/// whole machine you are merely busy.
/// </param>
/// <param name="MemoryInUseBytes">Memory in use, with reclaimable page cache excluded.</param>
/// <param name="MemoryChargedBytes">What the group is charged for, cache included.</param>
/// <param name="MemoryLimitBytes">The ceiling, or null when there is none.</param>
/// <param name="ThrottledFraction">
/// Share of scheduling periods in which the group was held back, from 0 to 1. Null when no quota
/// exists, because throttling cannot happen without one.
/// </param>
/// <param name="Window">The interval the CPU figure covers.</param>
public sealed record ResourceUsage(
    double? CpuPercent,
    double CpuCeilingCores,
    bool CpuCeilingIsQuota,
    long MemoryInUseBytes,
    long MemoryChargedBytes,
    long? MemoryLimitBytes,
    double? ThrottledFraction,
    TimeSpan Window);

/// <summary>
/// Turns two control group readings into a rate.
/// </summary>
public static class ResourceCalculator
{
    /// <summary>
    /// Works out usage between two samples.
    /// </summary>
    /// <param name="previous">The earlier sample, or null when this is the first.</param>
    /// <param name="current">The later sample.</param>
    /// <param name="hostCores">
    /// Cores the machine has. Used as the denominator when no quota is set, which is the usual
    /// case: a percentage of an imaginary limit would be meaningless.
    /// </param>
    /// <returns>The usage.</returns>
    public static ResourceUsage Between(CgroupSample? previous, CgroupSample current, int hostCores)
    {
        ArgumentNullException.ThrowIfNull(current);

        var ceiling = current.CpuQuotaCores ?? Math.Max(1, hostCores);
        var window = previous is null ? TimeSpan.Zero : current.TakenAt - previous.TakenAt;

        double? cpu = null;
        if (previous is not null && window > TimeSpan.Zero)
        {
            // CPU time is charged in microseconds across every core the group used, so the
            // denominator has to be elapsed time multiplied by the number of cores available.
            // Dividing by elapsed time alone yields "600%" on a six core box and looks like a bug.
            var elapsedMicroseconds = window.TotalSeconds * 1_000_000d;
            var used = current.CpuUsageMicroseconds - previous.CpuUsageMicroseconds;
            cpu = Math.Clamp(used / (elapsedMicroseconds * ceiling) * 100d, 0, 100);
        }

        // Throttling as a share of periods, which is dimensionless and therefore cannot exceed
        // one. The obvious alternative, throttled time over elapsed time, is accumulated per CPU
        // and happily reports more than a hundred percent on a multi-core machine.
        double? throttled = null;
        if (previous is not null && current.CpuQuotaCores is not null)
        {
            var periods = current.Periods - previous.Periods;
            throttled = periods > 0 ? (double)(current.Throttled - previous.Throttled) / periods : 0;
        }

        return new ResourceUsage(
            cpu,
            ceiling,
            current.CpuQuotaCores is not null,
            current.MemoryInUseBytes,
            current.MemoryBytes,
            current.MemoryLimitBytes,
            throttled,
            window);
    }
}
