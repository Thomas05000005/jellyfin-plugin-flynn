using Jellyfin.Plugin.Flynn.Modules.Storage;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// A forecast that is confidently wrong is worse than no forecast: it is the number someone
/// deletes a library on. Most of these tests are about refusing to answer.
/// </summary>
public class CapacityForecasterTests
{
    private const long TiB = 1024L * 1024 * 1024 * 1024;
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Day0 = DateTimeOffset.Parse("2026-07-01T03:15:00Z", null);

    /// <summary>Steady consumption, and the arithmetic is the arithmetic.</summary>
    [Fact]
    public void SteadyConsumption_GivesADate()
    {
        // 10 GiB a day, 200 GiB free, ext4 so nothing is held back.
        var readings = Series(30, day => 200 * GiB + (30 - day) * 10 * GiB);

        var forecast = CapacityForecaster.Estimate(readings, 20 * TiB, "ext4");

        // Last reading sits at 210 GiB, not 200: the series runs day 0..29 and the generator
        // leaves one step of headroom. 210 / 10 = 21.
        Assert.Equal(ForecastVerdict.Filling, forecast.Verdict);
        Assert.Equal(21, forecast.DaysRemaining);
    }

    /// <summary>
    /// The reason this is a median and not a least-squares fit. One overnight import in a month of
    /// otherwise calm days must not move the answer.
    /// </summary>
    [Fact]
    public void OneOvernightImport_DoesNotMoveTheForecast()
    {
        var calm = Series(30, day => 200 * GiB + (30 - day) * 10 * GiB);
        var withImport = calm
            .Select((r, i) => i >= 20 ? r with { FreeBytes = r.FreeBytes - (500 * GiB) } : r)
            .ToList();

        var before = CapacityForecaster.Estimate(calm, 20 * TiB, "ext4");
        var after = CapacityForecaster.Estimate(withImport, 20 * TiB, "ext4");

        Assert.Equal(ForecastVerdict.Filling, after.Verdict);
        Assert.Equal(before.DailyChangeBytes, after.DailyChangeBytes);
    }

    /// <summary>Below two weeks of history no figure is shown, because none would mean anything.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(13)]
    public void TooLittleHistory_ProducesNoNumber(int days)
    {
        var forecast = CapacityForecaster.Estimate(Series(days, d => 100 * GiB - (d * GiB)), 20 * TiB, "ext4");

        Assert.Equal(ForecastVerdict.Collecting, forecast.Verdict);
        Assert.Null(forecast.DaysRemaining);
    }

    [Fact]
    public void ExactlyTwoWeeks_IsEnough()
    {
        var forecast = CapacityForecaster.Estimate(
            Series(CapacityForecaster.MinimumReadings, d => 200 * GiB - (d * 10 * GiB)),
            20 * TiB,
            "ext4");

        Assert.NotEqual(ForecastVerdict.Collecting, forecast.Verdict);
    }

    /// <summary>
    /// Someone doing a clear-out. Production tooling returns a sentinel here rather than a date,
    /// and so does this.
    /// </summary>
    [Fact]
    public void SpaceBeingFreed_PredictsNoSaturation()
    {
        var readings = Series(30, day => 100 * GiB + (day * 5 * GiB));

        var forecast = CapacityForecaster.Estimate(readings, 20 * TiB, "ext4");

        Assert.Equal(ForecastVerdict.Steady, forecast.Verdict);
        Assert.Null(forecast.DaysRemaining);
        Assert.True(forecast.DailyChangeBytes < 0, "freeing space should read as negative consumption");
    }

    [Fact]
    public void NoChangeAtAll_PredictsNoSaturation()
    {
        var forecast = CapacityForecaster.Estimate(Series(30, _ => 500 * GiB), 20 * TiB, "ext4");

        Assert.Equal(ForecastVerdict.Steady, forecast.Verdict);
    }

    /// <summary>
    /// A drift smaller than the day-to-day scatter is not a trend. Turning it into a date would be
    /// inventing precision that the data does not carry.
    /// </summary>
    [Fact]
    public void ATrendBuriedInNoise_IsCalledInconclusive()
    {
        // Consumption spread from -14 to +16 GiB a day, weighted just enough that the median
        // lands at +1 GiB while the scatter around it is an order of magnitude larger. An alternating series would NOT do -- its median lands on
        // one of the two modes, which reads as a strong trend rather than as noise.
        var spread = new[] { 16, -14, 3, -9, 11, -6, 1, -2, 14, -12, 7, -4, 9, -11, 5, -7, 13, -13, 2, 1 };
        var readings = new List<CapacityReading> { new(Day0, 500 * GiB) };
        long free = 500 * GiB;
        for (var day = 1; day <= spread.Length; day++)
        {
            free -= spread[day - 1] * GiB;
            readings.Add(new CapacityReading(Day0.AddDays(day), free));
        }

        var forecast = CapacityForecaster.Estimate(readings, 20 * TiB, "ext4");

        Assert.Equal(ForecastVerdict.Inconclusive, forecast.Verdict);
        Assert.Null(forecast.DaysRemaining);
    }

    /// <summary>
    /// ZFS holds back a slop reserve that ordinary writes cannot touch, so a forecast must
    /// extrapolate towards it rather than towards zero.
    /// <para>
    /// The size of that bite is easy to overstate. One thirty-second of the pool is capped at
    /// 128 GiB, so on a twenty terabyte pool the reserve is 128 GiB and not the 640 GiB the
    /// fraction alone suggests. At 96% full that is around a sixth of what remains -- worth
    /// subtracting, and nowhere near the whole remainder.
    /// </para>
    /// </summary>
    [Fact]
    public void OnZfs_TheSlopReserveIsNotCountedAsFree()
    {
        const long Total = 20 * TiB;
        var free = (long)(Total * 0.04); // 96% full, as on the server this was written for.

        var usable = CapacityForecaster.UsableFree(free, Total, "zfs");
        var naive = CapacityForecaster.UsableFree(free, Total, "ext4");

        Assert.Equal(free, naive);
        Assert.Equal(free - (128L * 1024 * 1024 * 1024), usable);
        Assert.InRange((double)usable / free, 0.80, 0.87);
    }

    [Fact]
    public void OnZfs_AForecastIsShorterThanTheNaiveOne()
    {
        const long Total = 20 * TiB;
        var readings = Series(30, day => (long)(Total * 0.04) + ((30 - day) * 10 * GiB));

        var zfs = CapacityForecaster.Estimate(readings, Total, "zfs");
        var ext4 = CapacityForecaster.Estimate(readings, Total, "ext4");

        Assert.Equal(ForecastVerdict.Filling, zfs.Verdict);
        Assert.True(
            zfs.DaysRemaining < ext4.DaysRemaining,
            $"zfs {zfs.DaysRemaining}d should be shorter than naive {ext4.DaysRemaining}d");
    }

    /// <summary>
    /// One thirty-second of the pool, clamped. The clamps matter at both ends: a small pool must
    /// not lose a fixed 128 GiB, and a huge one must not lose 3% of a hundred terabytes.
    /// </summary>
    [Theory]
    [InlineData(1024L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024)]      // 1 TiB -> 32 GiB
    [InlineData(100L * 1024 * 1024 * 1024 * 1024, 128L * 1024 * 1024 * 1024)] // 100 TiB -> capped
    [InlineData(1024L * 1024 * 1024, 128L * 1024 * 1024)]                    // 1 GiB -> floor
    public void TheZfsReserve_IsClamped(long total, long expected)
    {
        Assert.Equal(expected, CapacityForecaster.Reserve(total, "zfs"));
    }

    [Fact]
    public void TheReserve_NeverExceedsHalfThePool()
    {
        // A pool smaller than twice the minimum slop: the floor would otherwise take everything.
        Assert.Equal(100L * 1024 * 1024, CapacityForecaster.Reserve(200L * 1024 * 1024, "zfs"));
    }

    [Theory]
    [InlineData("ext4")]
    [InlineData("xfs")]
    [InlineData("")]
    public void NonZfsFilesystems_HoldNothingBack(string type)
    {
        Assert.Equal(0, CapacityForecaster.Reserve(20 * TiB, type));
    }

    /// <summary>
    /// A missed night is one ordinary reading two days apart, not one day that looks like double
    /// consumption.
    /// </summary>
    [Fact]
    public void AMissedNight_DoesNotLookLikeASpike()
    {
        var complete = Series(30, day => 500 * GiB - (day * 10 * GiB));
        var withGap = complete.Where((_, i) => i != 15).ToList();

        var a = CapacityForecaster.Estimate(complete, 20 * TiB, "ext4");
        var b = CapacityForecaster.Estimate(withGap, 20 * TiB, "ext4");

        Assert.Equal(a.DailyChangeBytes, b.DailyChangeBytes);
    }

    /// <summary>Two readings at the same instant must not divide by zero.</summary>
    [Fact]
    public void TwoReadingsAtTheSameInstant_AreSurvived()
    {
        var readings = Series(30, day => 500 * GiB - (day * 10 * GiB));
        readings.Add(readings[10]);

        var forecast = CapacityForecaster.Estimate(readings, 20 * TiB, "ext4");

        Assert.Equal(ForecastVerdict.Filling, forecast.Verdict);
    }

    [Fact]
    public void ReadingsInAnyOrder_GiveTheSameAnswer()
    {
        var readings = Series(30, day => 500 * GiB - (day * 10 * GiB));
        var shuffled = readings.OrderBy(r => r.FreeBytes).ToList();

        Assert.Equal(
            CapacityForecaster.Estimate(readings, 20 * TiB, "ext4"),
            CapacityForecaster.Estimate(shuffled, 20 * TiB, "ext4"));
    }

    /// <summary>Already out of usable room: zero days, not a negative number.</summary>
    [Fact]
    public void AlreadyBelowTheReserve_ReportsNoDaysLeft()
    {
        const long Total = 20 * TiB;
        var readings = Series(30, day => (10 * GiB) - (day * GiB / 100));

        var forecast = CapacityForecaster.Estimate(readings, Total, "zfs");

        Assert.Equal(0, forecast.UsableFreeBytes);
        Assert.Equal(0, forecast.DaysRemaining);
    }

    private static List<CapacityReading> Series(int days, Func<int, long> freeAtDay) =>
        [.. Enumerable.Range(0, days).Select(d => new CapacityReading(Day0.AddDays(d), freeAtDay(d)))];

    /// <summary>
    /// Thomas's real series, fourteen intervals from his own nightly task, three of them short
    /// because he ran it by hand next to the schedule.
    /// <para>
    /// Keeping the short gaps put the answer 27% away from what the nightly readings alone say --
    /// 971 MB/day against 765, which is "full in 123 days" against 156. The worst offender is a
    /// 1.4 GB move measured over 3.3 hours, which normalises to eleven gigabytes a day. The median
    /// survived it and still carried it.
    /// </para>
    /// </summary>
    [Fact]
    public void ShortIntervalsFromAManualRun_DoNotDragTheRate()
    {
        // (hours since the previous reading, megabytes per day that gap implies)
        (double Hours, double MbPerDay)[] series =
        [
            (7.3, -2383), (4.5, 6536), (16.1, 1177), (7.9, 7227), (24.0, 3249),
            (24.1, 765), (47.9, 676), (48.1, 506), (23.9, 117), (14.4, 181),
            (9.6, 5230), (15.0, 4996), (5.6, -10412), (3.3, 1592),
        ];

        var at = DateTimeOffset.Parse("2026-08-05T13:33:00Z", null);
        var free = 200L * 1024 * 1024 * 1024;
        var readings = new List<CapacityReading> { new(at, free) };
        foreach (var (hours, mbPerDay) in series)
        {
            at = at.AddHours(hours);
            free -= (long)(mbPerDay * 1024 * 1024 * (hours / 24));
            readings.Add(new CapacityReading(at, free));
        }

        var forecast = CapacityForecaster.Estimate(readings, 218L * 1024 * 1024 * 1024, "ext4");
        var mbPerDayReported = forecast.DailyChangeBytes / 1024.0 / 1024.0;

        // The median of the eleven gaps of six hours or more is 765 MB/day. The median of all
        // fourteen is 971. Anything near 971 means the short gaps are still being counted.
        Assert.Equal(765, mbPerDayReported, 1);
        Assert.Equal(ForecastVerdict.Filling, forecast.Verdict);
    }

    /// <summary>
    /// A server whose task only ever runs by hand still deserves an answer, so the short gaps are
    /// only discarded when enough long ones remain to do without them.
    /// </summary>
    [Fact]
    public void WithOnlyShortIntervals_TheyAreUsedRatherThanRefusingToAnswer()
    {
        var at = DateTimeOffset.Parse("2026-08-05T00:00:00Z", null);
        var free = 200L * 1024 * 1024 * 1024;
        var readings = new List<CapacityReading> { new(at, free) };
        for (var i = 0; i < 20; i++)
        {
            at = at.AddHours(2);
            free -= 100L * 1024 * 1024;
            readings.Add(new CapacityReading(at, free));
        }

        var forecast = CapacityForecaster.Estimate(readings, 218L * 1024 * 1024 * 1024, "ext4");

        Assert.Equal(ForecastVerdict.Filling, forecast.Verdict);
        Assert.True(forecast.DailyChangeBytes > 0);
    }

}
