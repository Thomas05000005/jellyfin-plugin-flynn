using Jellyfin.Plugin.Flynn.Core.Data;
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
    private readonly DatabaseReadiness _readiness;
    private readonly ILogger<StorageSnapshotTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="StorageSnapshotTask"/> class.</summary>
    /// <param name="sweep">Does the measuring.</param>
    /// <param name="repository">Stores the result.</param>
    /// <param name="readiness">Whether storage is available.</param>
    /// <param name="logger">Logger.</param>
    public StorageSnapshotTask(
        StorageSweep sweep,
        StorageRepository repository,
        DatabaseReadiness readiness,
        ILogger<StorageSnapshotTask> logger)
    {
        _sweep = sweep;
        _repository = repository;
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
    }
}
