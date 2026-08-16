using Jellyfin.Plugin.Flynn.Core.Config;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Modules;
using MediaBrowser.Controller.Library;
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
/// <para>
/// Within the task the opposite holds. The two audits want the same tracks, so they are gathered on
/// one pass when both are on, and only what is switched on is gathered at all. A disabled module
/// used to pay for its own full walk every night anyway -- hundreds of thousands of rows read to
/// produce a figure nobody would be shown.
/// </para>
/// </summary>
public sealed class MusicAuditTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager _library;
    private readonly ReplayGainAudit _loudness;
    private readonly TrackImageAudit _images;
    private readonly MusicRepository _repository;
    private readonly MusicModule _module;
    private readonly TrackImagesModule _imagesModule;
    private readonly ModuleRegistry _modules;
    private readonly ConfigStore _config;
    private readonly IssueRegistry _issues;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<MusicAuditTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="MusicAuditTask"/> class.</summary>
    /// <param name="library">The server's library manager, for the shared walk.</param>
    /// <param name="loudness">The loudness audit.</param>
    /// <param name="images">The stored cover art audit.</param>
    /// <param name="repository">Where the result is stored.</param>
    /// <param name="module">Turns the loudness result into issues.</param>
    /// <param name="imagesModule">Turns the cover art result into issues.</param>
    /// <param name="modules">Tells which modules are switched on.</param>
    /// <param name="config">The admin's saved module toggles.</param>
    /// <param name="issues">The issue registry.</param>
    /// <param name="readiness">Whether Flynn's database is available.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public MusicAuditTask(
        ILibraryManager library,
        ReplayGainAudit loudness,
        TrackImageAudit images,
        MusicRepository repository,
        MusicModule module,
        TrackImagesModule imagesModule,
        ModuleRegistry modules,
        ConfigStore config,
        IssueRegistry issues,
        DatabaseReadiness readiness,
        TimeProvider clock,
        ILogger<MusicAuditTask> logger)
    {
        _library = library;
        _loudness = loudness;
        _images = images;
        _repository = repository;
        _module = module;
        _imagesModule = imagesModule;
        _modules = modules;
        _config = config;
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
        "Counts how much of the music library has a usable loudness value and says why the rest "
        + "does not, and measures what per-track cover art costs on disk. Reads only, gathers both "
        + "on one pass, and reads nothing at all when both modules are switched off.";

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

        var saved = _config.Current.Modules;
        var wantsLoudness = _modules.IsEnabled(saved, _module.Id);
        var wantsImages = _modules.IsEnabled(saved, _imagesModule.Id);

        var collectors = new List<ITrackCollector>(2);
        if (wantsLoudness)
        {
            collectors.Add(_loudness);
        }

        if (wantsImages)
        {
            collectors.Add(_images);
        }

        if (collectors.Count == 0)
        {
            _logger.LogInformation(
                "Both music modules are switched off, so nothing was read. Switching one on makes "
                + "this task walk the library again.");
            return;
        }

        _logger.LogInformation(
            "Walking the music libraries once for {Count} audit(s): {Which}.",
            collectors.Count,
            string.Join(", ", collectors.Select(c => c.GetType().Name)));

        MusicWalk.Run(_library, _logger, collectors, cancellationToken);
        progress.Report(70);

        var now = _clock.GetUtcNow();

        if (wantsLoudness)
        {
            await _repository.SaveLoudnessAsync(now, _loudness.Results, cancellationToken)
                .ConfigureAwait(false);

            foreach (var library in _loudness.Results)
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
        }

        progress.Report(85);

        if (wantsImages)
        {
            await _repository.SaveImagesAsync(now, _images.Results, cancellationToken)
                .ConfigureAwait(false);

            foreach (var library in _images.Results)
            {
                _logger.LogInformation(
                    "{Library}: {Count} track covers stored by the server, {Bytes} bytes, of which "
                    + "{Copies} are copies worth {CopyBytes} bytes.",
                    library.LibraryName,
                    library.TracksWithImage,
                    library.TotalBytes,
                    library.RedundantImages,
                    library.RedundantBytes);
            }

            await _imagesModule.ReportIssuesAsync(_issues, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
    }
}
