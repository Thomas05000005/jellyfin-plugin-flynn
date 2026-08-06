using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Modules.Forecast;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>
/// Takes the nightly storage reading.
/// <para>
/// All the expensive work lives here rather than in the dashboard card, so the page reads one row
/// and the measuring happens once, at night, when nobody is watching anything.
/// </para>
/// </summary>
public sealed class StorageSnapshotTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly StorageSweep _sweep;
    private readonly StorageRepository _repository;
    private readonly ForecastModule _forecast;
    private readonly IssueRegistry _issues;
    private readonly Retention _retention;
    private readonly DatabaseReadiness _readiness;
    private readonly ILogger<StorageSnapshotTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="StorageSnapshotTask"/> class.</summary>
    /// <param name="sweep">Does the measuring.</param>
    /// <param name="repository">Stores the result.</param>
    /// <param name="forecast">Turns the history into issues.</param>
    /// <param name="issues">The issue registry.</param>
    /// <param name="retention">Prunes what grows without bound.</param>
    /// <param name="readiness">Whether storage is available.</param>
    /// <param name="logger">Logger.</param>
    public StorageSnapshotTask(
        StorageSweep sweep,
        StorageRepository repository,
        ForecastModule forecast,
        IssueRegistry issues,
        Retention retention,
        DatabaseReadiness readiness,
        ILogger<StorageSnapshotTask> logger)
    {
        _sweep = sweep;
        _repository = repository;
        _forecast = forecast;
        _issues = issues;
        _retention = retention;
        _readiness = readiness;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Flynn: storage reading";

    /// <inheritdoc />
    public string Key => "FlynnStorageSnapshot";

    /// <inheritdoc />
    public string Description =>
        "Measures what each library holds and how much room is left on each device. "
        + "Builds the history the capacity forecast is drawn from.";

    /// <inheritdoc />
    public string Category => "Flynn";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // 03:15 rather than on the hour: every other scheduled thing on a media server runs at
        // 03:00, and a disk measurement taken while a library scan is rewriting sizes is a
        // measurement of the scan.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = new TimeSpan(3, 15, 0).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(2).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_readiness.IsReady)
        {
            _logger.LogWarning(
                "Skipping the Flynn storage reading: its database is unavailable. "
                + "Tonight's point will be missing from the history.");
            return;
        }

        var snapshot = await _sweep.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        await _repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);

        // Straight after storing tonight's reading, because the forecast is only as current as
        // the last point it saw. Running it on its own schedule would mean the inbox lags the
        // data it is drawn from by up to a day.
        await _forecast.ReportIssuesAsync(_issues, cancellationToken).ConfigureAwait(false);

        // Last, and after the sweep has already had its say. Pruning first could delete a
        // resolved issue in the same pass that was about to reopen it.
        await _retention.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
