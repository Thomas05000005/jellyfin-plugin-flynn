using Jellyfin.Plugin.Flynn.Modules.Resources;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Built around the counters read from the target server, which had no CPU quota, no memory limit,
/// and 2.9 GB of the 5.1 GB it was charged for sitting in reclaimable page cache. Every one of
/// those three is a way to report a confidently wrong number.
/// </summary>
public class ResourcesTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-05T12:00:00Z", null);

    /// <summary>
    /// Verbatim from the target server. cpu.max reads "max 100000", so there is no quota; the
    /// denominator has to be the machine's cores.
    /// </summary>
    private static readonly string[] RealCpuStat =
    [
        "usage_usec 7959640203",
        "user_usec 5918891513",
        "system_usec 2040748689",
        "nice_usec 45784000",
        "nr_periods 0",
    ];

    [Fact]
    public void TheRealCounters_Parse()
    {
        using var root = new FakeCgroup(
            cpuStat: RealCpuStat,
            cpuMax: "max 100000",
            memoryCurrent: "5111455744",
            memoryMax: "max",
            memoryStat: ["anon 1280385024", "inactive_file 2861187072", "active_file 4141056"]);

        var sample = new CgroupReader(root.Path).Read(T0)!;

        Assert.Equal(7959640203, sample.CpuUsageMicroseconds);
        Assert.Equal(5111455744, sample.MemoryBytes);
        Assert.Equal(2861187072, sample.InactiveFileBytes);
        Assert.Null(sample.MemoryLimitBytes);
        Assert.Null(sample.CpuQuotaCores);
    }

    /// <summary>
    /// The same trap as reading ZFS ARC out of free and calling it used. On the real numbers, the
    /// charged figure is more than double what is actually in use.
    /// </summary>
    [Fact]
    public void PageCache_IsNotCountedAsMemoryInUse()
    {
        var sample = Sample(T0, memoryBytes: 5111455744, inactiveFile: 2861187072);

        Assert.Equal(5111455744 - 2861187072, sample.MemoryInUseBytes);
        Assert.True(sample.MemoryInUseBytes < sample.MemoryBytes / 2);
    }

    /// <summary>
    /// CPU time is charged across every core, so elapsed time alone as a denominator produces
    /// "600%" on a six core box. Six cores fully busy for a second is a hundred percent.
    /// </summary>
    [Fact]
    public void AllCoresBusy_ReadsAsOneHundredPercent()
    {
        var before = Sample(T0, cpuUsec: 0);
        var after = Sample(T0.AddSeconds(1), cpuUsec: 6_000_000);

        var usage = ResourceCalculator.Between(before, after, hostCores: 6);

        Assert.Equal(100, usage.CpuPercent!.Value, 1);
        Assert.Equal(6, usage.CpuCeilingCores);
        Assert.False(usage.CpuCeilingIsQuota);
    }

    [Fact]
    public void OneCoreOfSixBusy_ReadsAsAboutSeventeenPercent()
    {
        var usage = ResourceCalculator.Between(
            Sample(T0, cpuUsec: 0), Sample(T0.AddSeconds(1), cpuUsec: 1_000_000), hostCores: 6);

        Assert.Equal(16.7, usage.CpuPercent!.Value, 1);
    }

    /// <summary>With a quota set, the percentage is a share of the quota and not of the machine.</summary>
    [Fact]
    public void WithAQuota_ThePercentageIsAShareOfIt()
    {
        var before = Sample(T0, cpuUsec: 0, quotaCores: 2);
        var after = Sample(T0.AddSeconds(1), cpuUsec: 2_000_000, quotaCores: 2);

        var usage = ResourceCalculator.Between(before, after, hostCores: 6);

        Assert.Equal(100, usage.CpuPercent!.Value, 1);
        Assert.True(usage.CpuCeilingIsQuota);
    }

    [Fact]
    public void OneSampleAlone_ProducesNoRate()
    {
        var usage = ResourceCalculator.Between(null, Sample(T0, cpuUsec: 5_000_000), hostCores: 6);

        Assert.Null(usage.CpuPercent);
    }

    /// <summary>
    /// Throttling as a share of periods, which cannot exceed one. The obvious alternative,
    /// throttled time over elapsed time, accumulates per CPU and reports over a hundred percent on
    /// a multi-core machine -- a formula I would have shipped had it not been refuted.
    /// </summary>
    [Fact]
    public void Throttling_IsAShareOfPeriodsAndCannotExceedOne()
    {
        var before = Sample(T0, periods: 100, throttled: 10, quotaCores: 2);
        var after = Sample(T0.AddSeconds(10), periods: 200, throttled: 60, quotaCores: 2);

        var usage = ResourceCalculator.Between(before, after, hostCores: 6);

        Assert.Equal(0.5, usage.ThrottledFraction!.Value, 3);
        Assert.InRange(usage.ThrottledFraction!.Value, 0, 1);
    }

    /// <summary>Without a quota there is nothing to be throttled against, so the figure is absent.</summary>
    [Fact]
    public void WithoutAQuota_ThrottlingIsNotReported()
    {
        var usage = ResourceCalculator.Between(
            Sample(T0), Sample(T0.AddSeconds(10)), hostCores: 6);

        Assert.Null(usage.ThrottledFraction);
    }

    [Fact]
    public void ACounterResetOrClockSkew_DoesNotProduceANegativePercentage()
    {
        var usage = ResourceCalculator.Between(
            Sample(T0, cpuUsec: 9_000_000), Sample(T0.AddSeconds(1), cpuUsec: 0), hostCores: 6);

        Assert.Equal(0, usage.CpuPercent!.Value);
    }

    [Theory]
    [InlineData("max 100000", null)]
    [InlineData("200000 100000", 2.0)]
    [InlineData("50000 100000", 0.5)]
    [InlineData("garbage", null)]
    [InlineData("100000 0", null)]
    public void TheCpuQuota_IsParsedOrTreatedAsAbsent(string cpuMax, double? expected)
    {
        using var root = new FakeCgroup(RealCpuStat, cpuMax, "1024", "max", []);

        Assert.Equal(expected, new CgroupReader(root.Path).ReadCpuQuota());
    }

    [Fact]
    public void WithNoCgroupFiles_TheReaderSaysSoRatherThanGuessing()
    {
        using var root = new FakeCgroup([], null, null, null, []);

        Assert.False(new CgroupReader(root.Path).IsAvailable);
        Assert.Null(new CgroupReader(root.Path).Read(T0));
    }

    private static CgroupSample Sample(
        DateTimeOffset at,
        long cpuUsec = 0,
        long periods = 0,
        long throttled = 0,
        long memoryBytes = 2 * GiB,
        long inactiveFile = 0,
        double? quotaCores = null) =>
        new(at, cpuUsec, periods, throttled, memoryBytes, inactiveFile, null, quotaCores);

    /// <summary>A directory laid out like /sys/fs/cgroup, so the reader can be tested off Linux.</summary>
    private sealed class FakeCgroup : IDisposable
    {
        public FakeCgroup(
            string[] cpuStat,
            string? cpuMax,
            string? memoryCurrent,
            string? memoryMax,
            string[] memoryStat)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "flynn-cgroup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);

            if (cpuStat.Length > 0)
            {
                Write("cpu.stat", string.Join('\n', cpuStat));
            }

            Write("cpu.max", cpuMax);
            Write("memory.current", memoryCurrent);
            Write("memory.max", memoryMax);
            if (memoryStat.Length > 0)
            {
                Write("memory.stat", string.Join('\n', memoryStat));
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private void Write(string name, string? content)
        {
            if (content is not null)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, name), content);
            }
        }
    }
}
