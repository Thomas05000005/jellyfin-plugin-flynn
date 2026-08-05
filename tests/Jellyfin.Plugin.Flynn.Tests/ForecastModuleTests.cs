using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Modules.Forecast;
using Jellyfin.Plugin.Flynn.Modules.Storage;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// When a projection becomes an alert. The rule that matters is the one that stops it becoming one
/// too easily: an alert that fires on things nobody needs to act on is the alert people learn to
/// ignore, and then the real one arrives and gets ignored with it.
/// </summary>
public class ForecastModuleTests
{
    private const long TiB = 1024L * 1024 * 1024 * 1024;

    [Theory]
    [InlineData(3, IssueSeverity.Critical)]
    [InlineData(7, IssueSeverity.Critical)]
    [InlineData(8, IssueSeverity.Warning)]
    [InlineData(30, IssueSeverity.Warning)]
    [InlineData(31, IssueSeverity.Info)]
    [InlineData(90, IssueSeverity.Info)]
    public void AFillingDeviceThatIsAlreadyFull_RaisesAnIssue(int days, IssueSeverity expected)
    {
        var severity = ForecastModule.SeverityFor(Filling(days), Device(usedFraction: 0.96));

        Assert.Equal(expected, severity);
    }

    [Fact]
    public void BeyondNinetyDays_NothingIsRaised()
    {
        Assert.Null(ForecastModule.SeverityFor(Filling(91), Device(usedFraction: 0.96)));
    }

    /// <summary>
    /// The gate that keeps the inbox worth reading. A disk at a fifth of its capacity filling
    /// quickly produces a perfectly real "full in sixty days" that nobody needs to act on today.
    /// </summary>
    [Fact]
    public void AMostlyEmptyDeviceFillingFast_RaisesNothing()
    {
        Assert.Null(ForecastModule.SeverityFor(Filling(20), Device(usedFraction: 0.20)));
    }

    [Theory]
    [InlineData(0.69)]
    [InlineData(0.50)]
    public void BelowTheFillGate_NothingIsRaised(double used)
    {
        Assert.Null(ForecastModule.SeverityFor(Filling(5), Device(used)));
    }

    [Fact]
    public void AtTheFillGate_AnIssueIsRaised()
    {
        Assert.NotNull(ForecastModule.SeverityFor(Filling(5), Device(ForecastModule.MinimumFillToWarn)));
    }

    /// <summary>A verdict that is not a date cannot become an alert, however full the disk is.</summary>
    [Theory]
    [InlineData(ForecastVerdict.Collecting)]
    [InlineData(ForecastVerdict.Steady)]
    [InlineData(ForecastVerdict.Inconclusive)]
    public void WithoutADate_NothingIsRaised(ForecastVerdict verdict)
    {
        var forecast = new CapacityForecast(verdict, null, 100, 0, 40);

        Assert.Null(ForecastModule.SeverityFor(forecast, Device(usedFraction: 0.99)));
    }

    /// <summary>
    /// The thresholds must stay ordered worst-first, since the first match wins. Reordering them
    /// would silently downgrade every critical alert to info.
    /// </summary>
    [Fact]
    public void TheThresholds_AreOrderedWorstFirst()
    {
        var days = ForecastModule.Thresholds.Select(t => t.Days).ToList();

        Assert.Equal(days.Order(), days);
    }

    private static CapacityForecast Filling(int days) =>
        new(ForecastVerdict.Filling, days, 500L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024, 40);

    private static DeviceSnapshot Device(double usedFraction) =>
        new("0:42", "RAID-Z1", TiB * 20, (long)(TiB * 20 * (1 - usedFraction)), "zfs");
}
