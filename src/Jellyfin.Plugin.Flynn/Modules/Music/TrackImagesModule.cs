using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// What per-track cover art costs on the server's own disk, and how much of it is the same image
/// stored again and again.
/// <para>
/// Read-only, and it will stay read-only until the write kernel can show an administrator exactly
/// what would go and take it back afterwards. A figure like "twenty gigabytes can be reclaimed"
/// leads somewhere destructive, which is the reason the audit is built to leave things out rather
/// than to find as much as possible: a cover the administrator put next to their music never
/// appears in it, whatever it weighs.
/// </para>
/// </summary>
public sealed class TrackImagesModule : IFlynnModule
{
    /// <summary>
    /// Below this, duplication is not worth an administrator's attention.
    /// <para>
    /// An absolute floor rather than a share, because a share is meaningless at the bottom: a
    /// library whose two stored covers happen to be identical is a hundred percent duplicated and
    /// costs nothing. A gigabyte is the point where the answer changes what someone would do.
    /// </para>
    /// </summary>
    internal const long WorthReporting = 1024L * 1024 * 1024;

    private readonly MusicRepository _repository;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<TrackImagesModule> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrackImagesModule"/> class.</summary>
    /// <param name="repository">Stores what the nightly audit found.</param>
    /// <param name="readiness">Whether Flynn's database came up.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public TrackImagesModule(
        MusicRepository repository,
        DatabaseReadiness readiness,
        TimeProvider clock,
        ILogger<TrackImagesModule> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Id => "music-images";

    /// <inheritdoc />
    public string NameKey => StringKeys.MusicImagesName;

    /// <inheritdoc />
    public string SummaryKey => StringKeys.MusicImagesSummary;

    /// <inheritdoc />
    public ModuleCategory Category => ModuleCategory.Music;

    /// <inheritdoc />
    public bool EnabledByDefault => true;

    /// <inheritdoc />
    public async Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
    {
        if (!_readiness.IsReady)
        {
            return new ModuleCard(
                Id, ModuleState.Degraded, LocalizedText.Of(StringKeys.StorageUnavailable), null,
                _clock.GetUtcNow());
        }

        var latest = await _repository.GetLatestImagesAsync(cancellationToken).ConfigureAwait(false);
        var takenAt = await _repository.GetLatestImagesTimeAsync(cancellationToken).ConfigureAwait(false);

        if (latest.Count == 0)
        {
            return new ModuleCard(
                Id, ModuleState.Degraded, LocalizedText.Of(StringKeys.MusicImagesNoAudit), null,
                _clock.GetUtcNow());
        }

        var stored = latest.Sum(l => (long)l.TracksWithImage);
        if (stored == 0)
        {
            return new ModuleCard(
                Id, ModuleState.Healthy, LocalizedText.Of(StringKeys.MusicImagesNone), null,
                takenAt ?? _clock.GetUtcNow());
        }

        // Bytes across libraries are summed rather than averaged, and that is not the mistake the
        // loudness card once made: a coverage percentage of two libraries in opposite states
        // describes neither, while disk space genuinely adds up. The per-library breakdown still
        // exists -- it is what the issues carry, so the admin knows where to look.
        var copies = latest.Sum(l => (long)l.RedundantImages);
        var wasted = latest.Sum(l => l.RedundantBytes);
        var total = latest.Sum(l => l.TotalBytes);

        if (copies == 0)
        {
            var (size, sizeUnit) = ByteScale.Of(total);
            return new ModuleCard(
                Id,
                ModuleState.Healthy,
                LocalizedText.Of(
                    StringKeys.MusicImagesClean, stored, size, LocalizedText.Of(sizeUnit)),
                null,
                takenAt ?? _clock.GetUtcNow());
        }

        var (value, unit) = ByteScale.Of(wasted);

        return new ModuleCard(
            Id,
            wasted >= WorthReporting ? ModuleState.Degraded : ModuleState.Healthy,
            LocalizedText.Of(StringKeys.MusicImagesHeadline, value, LocalizedText.Of(unit)),
            LocalizedText.Of(StringKeys.MusicImagesDetail, copies),
            takenAt ?? _clock.GetUtcNow());
    }

    /// <summary>
    /// Raises one issue per library that is wasting enough to matter.
    /// <para>
    /// Per library rather than one for the server, because the libraries are what an administrator
    /// can act on separately -- one may have the image extractor switched on and another not.
    /// </para>
    /// </summary>
    /// <param name="issues">The issue registry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the scope matches what was found.</returns>
    public async Task ReportIssuesAsync(IssueRegistry issues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var latest = await _repository.GetLatestImagesAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<Issue>();

        foreach (var library in latest.Where(l => l.RedundantBytes >= WorthReporting))
        {
            var (value, unit) = ByteScale.Of(library.RedundantBytes);

            found.Add(new Issue(
                Id,
                "copies",
                library.LibraryId.ToString("N"),
                IssueSeverity.Info,
                LocalizedText.Of(
                    StringKeys.IssueTrackImages,
                    library.LibraryName,
                    value,
                    LocalizedText.Of(unit)),
                LocalizedText.Of(StringKeys.IssueTrackImagesDetail)));
        }

        _logger.LogDebug("Cover art sweep found {Count} issue(s).", found.Count);
        await issues.ReconcileAsync(Id, "copies", found, cancellationToken).ConfigureAwait(false);
    }
}
