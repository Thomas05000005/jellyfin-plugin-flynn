using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>Why a track has no usable loudness value.</summary>
public enum LoudnessGap
{
    /// <summary>It has one. Either read from the file's tag or measured by the server.</summary>
    None,

    /// <summary>
    /// The library's loudness scan is switched off, so nothing was ever attempted.
    /// <para>
    /// This is an administrator setting, not a fault, and it is off by default -- which makes it
    /// overwhelmingly the most likely reason a library has no values at all.
    /// </para>
    /// </summary>
    ScanDisabled,

    /// <summary>The scan is on and this track still has nothing: it has not run, or it failed.</summary>
    NotMeasuredYet,
}

/// <summary>What one music library holds, from a loudness point of view.</summary>
/// <param name="LibraryId">The library's id.</param>
/// <param name="LibraryName">Its display name.</param>
/// <param name="ScanEnabled">Whether the server's loudness scan is switched on for it.</param>
/// <param name="Tracks">How many audio tracks were examined.</param>
/// <param name="FromTag">How many carry a gain read out of the file's own tag.</param>
/// <param name="Measured">How many carry a value the server measured itself.</param>
public sealed record LibraryLoudness(
    Guid LibraryId,
    string LibraryName,
    bool ScanEnabled,
    int Tracks,
    int FromTag,
    int Measured)
{
    /// <summary>Gets how many tracks have a usable value, from either source.</summary>
    public int Covered => FromTag + Measured;

    /// <summary>Gets how many have nothing.</summary>
    public int Missing => Math.Max(0, Tracks - Covered);

    /// <summary>Gets the share covered, from 0 to 1. One when the library is empty.</summary>
    public double Coverage => Tracks == 0 ? 1 : (double)Covered / Tracks;

    /// <summary>Gets why the uncovered tracks are uncovered.</summary>
    public LoudnessGap Gap =>
        Missing == 0 ? LoudnessGap.None
        : ScanEnabled ? LoudnessGap.NotMeasuredYet
        : LoudnessGap.ScanDisabled;
}

/// <summary>
/// Counts how much of the music library actually has a loudness value, and says why the rest does
/// not.
/// <para>
/// Jellyfin normalises playback from one of two places: a gain it read out of the file's tags at
/// scan time, or a value it measured itself with ffmpeg. Neither is guaranteed, and the failure is
/// silent -- playback simply is not normalised, and nothing anywhere says so.
/// </para>
/// <para>
/// Three facts from the server (12.0) decide what this can honestly report:
/// </para>
/// <para>
/// The measuring task only visits libraries whose <c>EnableLUFSScan</c> is set, and that property
/// has no initialiser -- it is <b>off by default</b>, and it appears in exactly one place in the
/// whole server besides its own declaration. "No library has any measured value" is therefore the
/// normal state of an untouched install, and reporting it as a fault would be wrong.
/// </para>
/// <para>
/// The server reads exactly one loudness tag, <c>REPLAYGAIN_TRACK_GAIN</c> -- a single occurrence
/// in the entire codebase. Files tagged the Opus way (<c>R128_TRACK_GAIN</c>), or carrying only an
/// album gain, are read as having nothing at all.
/// </para>
/// <para>
/// And the task skips any track that already has either value, so a file whose tag was read is
/// never measured. Coverage is the union of the two, never their sum over one track.
/// </para>
/// </summary>
public sealed class ReplayGainAudit
{
    /// <summary>
    /// How many albums are asked about at once.
    /// <para>
    /// Tracks are fetched by album rather than paged with a start index, and that is the whole
    /// difference between this finishing and not. <c>StartIndex</c> becomes a real SQL
    /// <c>OFFSET</c>, applied after a <c>DISTINCT</c> over the full entity and an <c>ORDER BY</c>,
    /// so the engine produces and throws away every row before the offset: walking a library in
    /// pages costs O(n squared). At four hundred thousand tracks in pages of a thousand that is
    /// tens of billions of discarded rows. Batching by album ids is one linear pass instead.
    /// </para>
    /// <para>
    /// Kept well under SQLite's bound parameter limit, since each id is one variable.
    /// </para>
    /// </summary>
    internal const int AlbumBatchSize = 200;

    private readonly ILibraryManager _library;
    private readonly ILogger<ReplayGainAudit> _logger;

    /// <summary>Initializes a new instance of the <see cref="ReplayGainAudit"/> class.</summary>
    /// <param name="library">The server's library manager.</param>
    /// <param name="logger">Logger.</param>
    public ReplayGainAudit(ILibraryManager library, ILogger<ReplayGainAudit> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Audits every music library.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per music library.</returns>
    public IReadOnlyList<LibraryLoudness> Run(CancellationToken cancellationToken)
    {
        var results = new List<LibraryLoudness>();

        foreach (var library in MusicLibraries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (tracks, fromTag, measured) = CountValues(library.Id, cancellationToken);

            // What the server thinks it holds, against what walking the albums actually reached.
            // A track that belongs to no album is invisible to the walk, and a silent shortfall
            // would read as "these are missing a value" when they were never looked at.
            var claimed = _library.GetCount(new InternalItemsQuery
            {
                ParentId = library.Id,
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true,
            });

            if (claimed != tracks)
            {
                _logger.LogInformation(
                    "{Library}: walked {Walked} tracks through its albums, the server counts "
                    + "{Claimed}. The difference is tracks that belong to no album; they are not "
                    + "reported as missing a value, because they were never examined.",
                    library.Name,
                    tracks,
                    claimed);
            }

            results.Add(new LibraryLoudness(
                library.Id, library.Name, ScanEnabledFor(library), tracks, fromTag, measured));
        }

        return results;
    }

    /// <summary>
    /// Finds the libraries a music module should look at.
    /// <para>
    /// Queried rather than read off <c>RootFolder.Children</c>, so this goes through the interface
    /// like everything else and can be exercised without standing up a server.
    /// </para>
    /// </summary>
    /// <returns>The music libraries.</returns>
    internal IReadOnlyList<BaseItem> MusicLibraries() => Music.MusicLibraries.Of(_library);

    /// <summary>Reads whether the server's loudness scan is switched on for a library.</summary>
    /// <param name="library">The library.</param>
    /// <returns>True when the scan will visit it.</returns>
    internal bool ScanEnabledFor(BaseItem library)
    {
        try
        {
            return _library.GetLibraryOptions(library).EnableLUFSScan;
        }
        catch (Exception ex)
        {
            // Reported as scan-off rather than crashing the card. That is the under-promising
            // direction: it says less was measured than may have been, never more.
            _logger.LogWarning(ex, "Could not read library options for {Library}.", library.Name);
            return false;
        }
    }

    private (int Tracks, int FromTag, int Measured) CountValues(
        Guid libraryId, CancellationToken cancellationToken)
    {
        // MusicAlbum carries RequiresSourceSerialisation, so albums come back without a JSON parse
        // per row. Audio does not, which is the other reason to touch tracks once and only once.
        var albums = _library.GetItemList(new InternalItemsQuery
        {
            ParentId = libraryId,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true,
        });

        var tracks = 0;
        var fromTag = 0;
        var measured = 0;

        foreach (var batch in albums.Select(a => a.Id).Chunk(AlbumBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _library.GetItemList(new InternalItemsQuery
            {
                AlbumIds = batch,
                IncludeItemTypes = [BaseItemKind.Audio],
            }))
            {
                tracks++;

                // The order mirrors the server's own: a track whose tag was read is never measured,
                // so counting the tag first is what keeps one track from counting twice.
                if (item.NormalizationGain.HasValue)
                {
                    fromTag++;
                }
                else if (item.LUFS.HasValue)
                {
                    measured++;
                }
            }
        }

        return (tracks, fromTag, measured);
    }
}
