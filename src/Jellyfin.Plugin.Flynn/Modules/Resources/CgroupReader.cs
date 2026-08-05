using System.Globalization;

namespace Jellyfin.Plugin.Flynn.Modules.Resources;

/// <summary>One reading of the control group's counters.</summary>
/// <param name="TakenAt">When it was read.</param>
/// <param name="CpuUsageMicroseconds">
/// Cumulative CPU time for the whole group, so ffmpeg children are included. This is the number
/// <c>Process.GetCurrentProcess()</c> cannot see: on a media server most of the CPU is spent by
/// transcodes that are not the .NET process.
/// </param>
/// <param name="Periods">Scheduling periods elapsed. Zero when no CPU quota is set.</param>
/// <param name="Throttled">Periods in which the group was held back.</param>
/// <param name="MemoryBytes">
/// What the group is charged for, which is not what it is using. Page cache counts here.
/// </param>
/// <param name="InactiveFileBytes">
/// Reclaimable page cache. The kernel gives this back under pressure, so it is not consumption.
/// </param>
/// <param name="MemoryLimitBytes">The memory ceiling, or null when there is none.</param>
/// <param name="CpuQuotaCores">The CPU ceiling in cores, or null when there is none.</param>
public sealed record CgroupSample(
    DateTimeOffset TakenAt,
    long CpuUsageMicroseconds,
    long Periods,
    long Throttled,
    long MemoryBytes,
    long InactiveFileBytes,
    long? MemoryLimitBytes,
    double? CpuQuotaCores)
{
    /// <summary>
    /// Gets memory actually in use, with reclaimable cache taken out.
    /// <para>
    /// The difference is not small. On the server this was written for, the group is charged 5.1 GB
    /// of which 2.9 GB is inactive page cache; reporting the charged figure would have overstated
    /// what Jellyfin needs by more than double. It is the same trap as reading ZFS ARC out of
    /// <c>free</c> and calling it used memory.
    /// </para>
    /// </summary>
    public long MemoryInUseBytes => Math.Max(0, MemoryBytes - InactiveFileBytes);
}

/// <summary>
/// Reads the control group counters the kernel exposes to a container about itself.
/// <para>
/// This is how a plugin measures what Jellyfin actually costs. <c>Process.GetCurrentProcess()</c>
/// reports the .NET process alone, and on a media server the expensive part is the transcodes it
/// spawns. The control group counts the whole tree.
/// </para>
/// </summary>
public sealed class CgroupReader
{
    /// <summary>Where cgroup v2 is mounted inside a container.</summary>
    internal const string DefaultRoot = "/sys/fs/cgroup";

    private readonly string _root;

    /// <summary>Initializes a new instance of the <see cref="CgroupReader"/> class.</summary>
    public CgroupReader()
        : this(DefaultRoot)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CgroupReader"/> class.</summary>
    /// <param name="root">Where to read from. Overridden in tests.</param>
    internal CgroupReader(string root)
    {
        _root = root;
    }

    /// <summary>
    /// Gets a value indicating whether cgroup v2 counters are readable here.
    /// <para>
    /// v1 is deliberately not supported. Its files are laid out differently and it is being retired
    /// everywhere; claiming support and quietly reporting nothing would be worse than saying no.
    /// </para>
    /// </summary>
    public bool IsAvailable =>
        OperatingSystem.IsLinux() && File.Exists(Path.Combine(_root, "cpu.stat"));

    /// <summary>Takes one reading.</summary>
    /// <param name="now">The moment of the reading.</param>
    /// <returns>The sample, or null when the counters are unreadable.</returns>
    public CgroupSample? Read(DateTimeOffset now)
    {
        var cpu = ReadKeyedFile("cpu.stat");
        if (cpu.Count == 0)
        {
            return null;
        }

        var memoryStat = ReadKeyedFile("memory.stat");

        return new CgroupSample(
            now,
            cpu.GetValueOrDefault("usage_usec"),
            cpu.GetValueOrDefault("nr_periods"),
            cpu.GetValueOrDefault("nr_throttled"),
            ReadLong("memory.current") ?? 0,
            memoryStat.GetValueOrDefault("inactive_file"),
            ReadLong("memory.max"),
            ReadCpuQuota());
    }

    /// <summary>
    /// Parses <c>cpu.max</c>, which is a quota and a period, or the word max.
    /// </summary>
    /// <returns>The ceiling in cores, or null when unlimited.</returns>
    internal double? ReadCpuQuota()
    {
        var raw = ReadText("cpu.max");
        if (raw is null)
        {
            return null;
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period)
            || period <= 0)
        {
            // "max 100000" lands here, which is the unlimited case and by far the most common:
            // hardly anyone puts a CPU ceiling on their media server.
            return null;
        }

        return (double)quota / period;
    }

    private long? ReadLong(string file)
    {
        var raw = ReadText(file);
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null; // "max" means no limit, and no limit is not zero.
    }

    private Dictionary<string, long> ReadKeyedFile(string file)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var raw = ReadText(file);
        if (raw is null)
        {
            return result;
        }

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                result[parts[0]] = value;
            }
        }

        return result;
    }

    private string? ReadText(string file)
    {
        try
        {
            var path = Path.Combine(_root, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
