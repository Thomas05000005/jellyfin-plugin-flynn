using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// Walks the music library once a night and stores what it found.
/// <para>
/// Separate from the storage reading rather than folded into it, because the two have nothing in
/// common but their schedule: one asks the filesystem three questions, the other walks every track
/// on the server. Sharing a task would mean a slow music library delaying a disk measurement, and
/// an admin who wants to switch one off having to switch off both.
/// </para>
/// </summary>
public sealed class MusicAuditTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ReplayGainAudit _loudness;
    private readonly MusicRepository _repository;
    private readonly MusicModule _module;
    private readonly IssueRegistry _issues;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<MusicAuditTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="MusicAuditTask"/> class.</summary>
    /// <param name="loudness">The loudness audit.</param>
    /// <param name="repository">Where the result is stored.</param>
    /// <param name="module">Turns the result into issues.</param>
    /// <param name="issues">The issue registry.</param>
    /// <param name="readiness">Whether Flynn's database is available.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public MusicAuditTask(
        ReplayGainAudit loudness,
        MusicRepository repository,
        MusicModule module,
        IssueRegistry issues,
        DatabaseReadiness readiness,
        TimeProvider clock,
        ILogger<MusicAuditTask> logger)
    {
        _loudness = loudness;
        _repository = repository;
        _module = module;
        _issues = issues;
        _readiness = readiness;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Flynn: music audit";

    /// <inheritdoc />
    public string Key => "FlynnMusicAudit";

    /// <inheritdoc />
    public string Description =>
        "Counts how much of the music library has a usable loudness value, and says why the rest "
        + "does not. Reads only.";

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
        // An hour after the storage reading, and well clear of 03:00 where every other scheduled
        // thing on a media server lands. Walking every track while a library scan is rewriting them
        // measures the scan.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = new TimeSpan(4, 15, 0).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(4).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!_readiness.IsReady)
        {
            _logger.LogWarning(
                "Skipping the Flynn music audit: its database is unavailable. Tonight's figures "
                + "will be missing.");
            return;
        }

        var libraries = _loudness.Run(cancellationToken);
        progress.Report(80);

        await _repository.SaveLoudnessAsync(_clock.GetUtcNow(), libraries, cancellationToken)
            .ConfigureAwait(false);

        foreach (var library in libraries)
        {
            _logger.LogInformation(
                "{Library}: {Covered} of {Tracks} tracks have a loudness value "
                + "({FromTag} from tags, {Measured} measured); scan is {State}.",
                library.LibraryName,
                library.Covered,
                library.Tracks,
                library.FromTag,
                library.Measured,
                library.ScanEnabled ? "on" : "off");
        }

        await _module.ReportIssuesAsync(_issues, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
