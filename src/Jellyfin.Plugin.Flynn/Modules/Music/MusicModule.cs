using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// What is wrong with the music library, and nothing else.
/// <para>
/// This module reads. It reports what it finds and changes nothing, which is deliberate for a first
/// version: a music library is the one place where a confident wrong answer is expensive, and the
/// only way to earn the right to propose a correction is to be checkable first. Every number here
/// can be recounted by hand on a handful of albums.
/// </para>
/// </summary>
public sealed class MusicModule : IFlynnModule
{
    private readonly MusicRepository _repository;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<MusicModule> _logger;

    /// <summary>Initializes a new instance of the <see cref="MusicModule"/> class.</summary>
    /// <param name="repository">Stores what the nightly audit found.</param>
    /// <param name="readiness">Whether Flynn's database came up.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public MusicModule(
        MusicRepository repository,
        DatabaseReadiness readiness,
        TimeProvider clock,
        ILogger<MusicModule> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>How full a library has to be before a gap is worth raising as an issue.</summary>
    internal const double CoverageToWarn = 0.95;

    /// <inheritdoc />
    public string Id => "music";

    /// <inheritdoc />
    public string NameKey => StringKeys.MusicName;

    /// <inheritdoc />
    public string SummaryKey => StringKeys.MusicSummary;

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

        var latest = await _repository.GetLatestLoudnessAsync(cancellationToken).ConfigureAwait(false);
        if (latest.Count == 0)
        {
            return new ModuleCard(
                Id, ModuleState.Degraded, LocalizedText.Of(StringKeys.MusicNoAudit), null,
                _clock.GetUtcNow());
        }

        var tracks = latest.Sum(l => l.Tracks);
        var covered = latest.Sum(l => l.Covered);
        var takenAt = await _repository.GetLatestLoudnessTimeAsync(cancellationToken).ConfigureAwait(false);

        if (tracks == 0)
        {
            return new ModuleCard(
                Id, ModuleState.Healthy, LocalizedText.Of(StringKeys.MusicEmpty), null,
                takenAt ?? _clock.GetUtcNow());
        }

        // Never average across libraries whose situation differs. On the server this was written
        // for, one library sits at 100% because its scan was switched on and the other at 0%
        // because it was not; the average is 11%, a figure that describes neither and suggests no
        // action. The admin who read it learned nothing, which is how it was reported.
        var worst = latest.Where(l => l.Tracks > 0).OrderBy(l => l.Coverage).FirstOrDefault();
        if (worst is null)
        {
            return new ModuleCard(
                Id, ModuleState.Healthy, LocalizedText.Of(StringKeys.MusicEmpty), null,
                takenAt ?? _clock.GetUtcNow());
        }

        var others = latest.Where(l => l.Tracks > 0 && l.LibraryId != worst.LibraryId).ToList();
        var headline = LocalizedText.Of(
            StringKeys.MusicLoudnessHeadline,
            worst.LibraryName,
            (int)Math.Round(worst.Coverage * 100));

        // The detail says why, and what the other libraries look like -- because "0%" alone reads
        // as a fault when it is a setting, and hides that the rest of the server is fine.
        var reason = worst.Gap == LoudnessGap.ScanDisabled
            ? LocalizedText.Of(StringKeys.MusicScanOff)
            : LocalizedText.Of(StringKeys.MusicLoudnessDetail, worst.Covered, worst.Tracks);

        var detail = others.Count == 0
            ? reason
            : LocalizedText.Of(
                StringKeys.MusicOtherLibraries,
                reason,
                (int)Math.Round(others.Average(l => l.Coverage) * 100));

        return new ModuleCard(
            Id,
            worst.Coverage >= CoverageToWarn ? ModuleState.Healthy : ModuleState.Degraded,
            headline,
            detail,
            takenAt ?? _clock.GetUtcNow());
    }

    /// <summary>
    /// Turns the stored audit into issues.
    /// <para>
    /// A library whose scan is switched off gets an issue that says so and names the setting,
    /// because that is a decision the admin can act on in ten seconds. A library whose scan is on
    /// and still incomplete gets a different one, because the action is not the same.
    /// </para>
    /// </summary>
    /// <param name="issues">The issue registry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the music scope matches what was found.</returns>
    public async Task ReportIssuesAsync(IssueRegistry issues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var latest = await _repository.GetLatestLoudnessAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<Issue>();

        foreach (var library in latest.Where(l => l.Tracks > 0 && l.Coverage < CoverageToWarn))
        {
            var (titleKey, severity) = library.Gap switch
            {
                LoudnessGap.ScanDisabled => (StringKeys.IssueLoudnessScanOff, IssueSeverity.Info),
                _ => (StringKeys.IssueLoudnessIncomplete, IssueSeverity.Info),
            };

            found.Add(new Issue(
                Id,
                "loudness",
                library.LibraryId.ToString("N"),
                severity,
                LocalizedText.Of(titleKey, library.LibraryName, library.Missing),
                LocalizedText.Of(StringKeys.IssueLoudnessDetail)));
        }

        _logger.LogDebug("Music loudness sweep found {Count} issue(s).", found.Count);
        await issues.ReconcileAsync(Id, "loudness", found, cancellationToken).ConfigureAwait(false);
    }
}
