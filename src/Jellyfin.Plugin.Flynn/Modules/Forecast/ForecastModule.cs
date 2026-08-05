using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Storage;

namespace Jellyfin.Plugin.Flynn.Modules.Forecast;

/// <summary>
/// Says when the disks run out of room, or says that it cannot yet.
/// <para>
/// Reads the history the storage sweep has been building. Nothing is projected from fewer than two
/// weeks of nights, so this reports as collecting for a fortnight after a fresh install. That is
/// the honest answer: a date drawn from three readings is a guess wearing a number.
/// </para>
/// </summary>
public sealed class ForecastModule : IFlynnModule
{
    /// <summary>
    /// Day thresholds at which a capacity issue is raised, worst first.
    /// </summary>
    internal static readonly (int Days, IssueSeverity Severity)[] Thresholds =
    [
        (7, IssueSeverity.Critical),
        (30, IssueSeverity.Warning),
        (90, IssueSeverity.Info),
    ];

    /// <summary>
    /// How full a device must already be before a forecast is worth raising as an issue.
    /// <para>
    /// A projection on its own is a noisy alarm. A nearly empty disk filling quickly produces a
    /// perfectly real "full in eighty days" that nobody needs to act on, and the alert that fires
    /// for that is the alert people learn to ignore. Production practice is to require both, and
    /// this does.
    /// </para>
    /// </summary>
    internal const double MinimumFillToWarn = 0.70;

    private readonly StorageRepository _storage;
    private readonly DatabaseReadiness _readiness;

    /// <summary>Initializes a new instance of the <see cref="ForecastModule"/> class.</summary>
    /// <param name="storage">Where the readings live.</param>
    /// <param name="readiness">Whether storage came up.</param>
    public ForecastModule(StorageRepository storage, DatabaseReadiness readiness)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
    }

    /// <inheritdoc />
    public string Id => "capacity";

    /// <inheritdoc />
    public string NameKey => StringKeys.ForecastName;

    /// <inheritdoc />
    public string SummaryKey => StringKeys.ForecastSummary;

    /// <inheritdoc />
    public ModuleCategory Category => ModuleCategory.System;

    /// <inheritdoc />
    public bool EnabledByDefault => true;

    /// <inheritdoc />
    public async Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
    {
        if (!_readiness.IsReady)
        {
            return new ModuleCard(
                Id, ModuleState.Degraded, LocalizedText.Of(StringKeys.StorageUnavailable), null,
                DateTimeOffset.UtcNow);
        }

        var snapshot = await _storage.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Devices.Count == 0)
        {
            return new ModuleCard(
                Id, ModuleState.Degraded, LocalizedText.Of(StringKeys.ForecastNoDevices), null,
                DateTimeOffset.UtcNow);
        }

        var forecasts = await ForecastAllAsync(snapshot, cancellationToken).ConfigureAwait(false);

        // The tightest device is the only one worth putting on a card. Reporting an average across
        // pools would hide the one that matters behind the ones that do not.
        var worst = forecasts
            .Where(f => f.Forecast.Verdict == ForecastVerdict.Filling)
            .MinBy(f => f.Forecast.DaysRemaining);

        if (worst is not null)
        {
            var days = worst.Forecast.DaysRemaining!.Value;
            var rate = StorageModule.ScaleBytes(worst.Forecast.DailyChangeBytes);
            return new ModuleCard(
                Id,
                days <= 30 ? ModuleState.Degraded : ModuleState.Healthy,
                LocalizedText.Of(StringKeys.ForecastFilling, worst.Device.MountPath, days),
                LocalizedText.Of(
                    StringKeys.ForecastRate, rate.Value, LocalizedText.Of(rate.UnitKey), worst.Device.MountPath),
                snapshot.TakenAt);
        }

        // Nothing is filling. Say which of the two reasons applies rather than merging them: still
        // gathering data and genuinely stable look identical on a card and mean opposite things.
        var collecting = forecasts.FirstOrDefault(f => f.Forecast.Verdict == ForecastVerdict.Collecting);
        if (collecting is not null)
        {
            return new ModuleCard(
                Id,
                ModuleState.Degraded,
                LocalizedText.Of(
                    StringKeys.ForecastCollecting,
                    collecting.Forecast.ReadingCount,
                    CapacityForecaster.MinimumReadings),
                null,
                snapshot.TakenAt);
        }

        var unclear = forecasts.Any(f => f.Forecast.Verdict == ForecastVerdict.Inconclusive);
        return new ModuleCard(
            Id,
            ModuleState.Healthy,
            LocalizedText.Of(unclear ? StringKeys.ForecastInconclusive : StringKeys.ForecastSteady),
            null,
            snapshot.TakenAt);
    }

    /// <summary>
    /// Turns the forecasts into issues, as one sweep of the capacity scope.
    /// <para>
    /// A sweep rather than individual reports, so a pool that stops filling closes its own issue
    /// without anything having to remember it was raised.
    /// </para>
    /// </summary>
    /// <param name="issues">The issue registry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the capacity scope matches reality.</returns>
    public async Task ReportIssuesAsync(IssueRegistry issues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (!_readiness.IsReady)
        {
            return;
        }

        var snapshot = await _storage.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        var forecasts = await ForecastAllAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var found = new List<Issue>();

        foreach (var entry in forecasts)
        {
            var severity = SeverityFor(entry.Forecast, entry.Device);
            if (severity is null)
            {
                continue;
            }

            found.Add(new Issue(
                Id,
                "capacity",
                entry.Device.DeviceId,
                severity.Value,
                LocalizedText.Of(
                    StringKeys.IssueCapacityTitle,
                    entry.Device.MountPath,
                    entry.Forecast.DaysRemaining!.Value),
                LocalizedText.Of(StringKeys.IssueCapacityDetail, entry.Forecast.ReadingCount)));
        }

        await issues.ReconcileAsync(Id, "capacity", found, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides whether a forecast deserves an issue, and how loud.
    /// </summary>
    /// <param name="forecast">The projection.</param>
    /// <param name="device">The device it is about.</param>
    /// <returns>The severity, or null when nothing should be raised.</returns>
    internal static IssueSeverity? SeverityFor(CapacityForecast forecast, DeviceSnapshot device)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(device);

        if (forecast.Verdict != ForecastVerdict.Filling || device.UsedFraction < MinimumFillToWarn)
        {
            return null;
        }

        foreach (var (days, severity) in Thresholds)
        {
            if (forecast.DaysRemaining <= days)
            {
                return severity;
            }
        }

        return null;
    }

    private async Task<List<DeviceForecast>> ForecastAllAsync(
        StorageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var results = new List<DeviceForecast>(snapshot.Devices.Count);

        foreach (var device in snapshot.Devices)
        {
            var history = await _storage.GetHistoryAsync(device.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new DeviceForecast(
                device,
                CapacityForecaster.Estimate(history, device.TotalBytes, device.FileSystemType)));
        }

        return results;
    }

    private sealed record DeviceForecast(DeviceSnapshot Device, CapacityForecast Forecast);
}
