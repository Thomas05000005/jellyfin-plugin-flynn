using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// Something that wants to look at every track, once.
/// <para>
/// Collectors exist so that two audits wanting the same tracks cost one pass rather than two.
/// <c>Audio</c> carries no <c>[RequiresSourceSerialisation]</c>, so every track row costs a JSON
/// deserialisation of its data blob: on a 223 000-track library, walking twice is 447 000 of them.
/// </para>
/// </summary>
internal interface ITrackCollector
{
    /// <summary>Called once before the first library, so results from a previous run are dropped.</summary>
    void Starting();

    /// <summary>Called once per music library, before any of its tracks.</summary>
    /// <param name="library">The library about to be walked.</param>
    void Begin(BaseItem library);

    /// <summary>Called once per distinct track.</summary>
    /// <param name="track">The track.</param>
    void Visit(BaseItem track);

    /// <summary>Called once per music library, after its last track.</summary>
    /// <param name="library">The library just walked.</param>
    /// <param name="tracksSeen">How many distinct tracks the walk reached in it.</param>
    void Finish(BaseItem library, int tracksSeen);
}

/// <summary>
/// One pass over the music libraries, feeding every collector that asked for it.
/// <para>
/// The walk owns the three things that are easy to get wrong and were, once each: the library
/// scope, the deduplication, and where cancellation is checked.
/// </para>
/// </summary>
internal static class MusicWalk
{
    /// <summary>
    /// How many albums are asked about at once.
    /// <para>
    /// Tracks are fetched by album rather than paged with a start index. <c>StartIndex</c> becomes
    /// a real SQL <c>OFFSET</c> applied after a <c>DISTINCT</c> over the full entity, so paging a
    /// library costs O(n squared); batching by album ids is one linear pass. Kept well under
    /// SQLite's bound parameter limit, since each id is one variable.
    /// </para>
    /// </summary>
    internal const int AlbumBatchSize = 200;

    /// <summary>Walks every music library once.</summary>
    /// <param name="library">The server's library manager.</param>
    /// <param name="logger">Logger, for a collector that gives up mid-walk.</param>
    /// <param name="collectors">Who wants to see the tracks. An empty list walks nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static void Run(
        ILibraryManager library,
        ILogger logger,
        IReadOnlyList<ITrackCollector> collectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(collectors);

        if (collectors.Count == 0)
        {
            // Nobody is asking, so nothing is read -- not even the list of music libraries. A
            // disabled module used to pay for its walk anyway, which on a large library is
            // hundreds of thousands of rows a night for a figure nobody will look at.
            return;
        }

        // A collector that throws is dropped rather than allowed to take the pass down with it.
        // Sharing a walk must not cost the failure isolation that running them separately gave.
        var live = new List<ITrackCollector>(collectors);
        Each(live, logger, c => c.Starting());

        foreach (var musicLibrary in MusicLibraries.Of(library))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (live.Count == 0)
            {
                return;
            }

            Each(live, logger, c => c.Begin(musicLibrary));
            var seen = WalkOne(library, logger, live, musicLibrary.Id, cancellationToken);
            Each(live, logger, c => c.Finish(musicLibrary, seen));
        }
    }

    private static int WalkOne(
        ILibraryManager library,
        ILogger logger,
        List<ITrackCollector> live,
        Guid libraryId,
        CancellationToken cancellationToken)
    {
        // MusicAlbum carries RequiresSourceSerialisation, so albums come back without a JSON parse
        // per row. The ids are materialised and the entities dropped immediately: chunking a lazy
        // Select over the list would keep every album entity rooted from the first batch to the
        // last, to read a Guid from each.
        var albumIds = library
            .GetItemList(new InternalItemsQuery
            {
                ParentId = libraryId,
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                Recursive = true,
            })
            .Select(album => album.Id)
            .ToArray();

        // AlbumIds is not the identifier filter its name promises. The server resolves the ids to
        // album NAMES and matches every track whose Album string equals one of them:
        //
        //     var subQuery = context.BaseItems.WhereOneOrMany(filter.AlbumIds, f => f.Id);
        //     baseQuery = baseQuery.Where(e => subQuery.Any(f => f.Name == e.Album));
        //
        // So two albums called "Live" on either side of a batch boundary each bring back the
        // tracks of both. Every track is therefore passed on at most once, whatever the predicate
        // turns out to do.
        var seen = new HashSet<Guid>();

        foreach (var batch in albumIds.Chunk(AlbumBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var track in library.GetItemList(new InternalItemsQuery
            {
                // Scoped, because the name join carries no scope of its own: a track in another
                // music library whose Album tag matches would otherwise be counted here.
                // Recursive is what makes ParentId reach past the artists that are a music
                // library's direct children.
                ParentId = libraryId,
                Recursive = true,
                AlbumIds = batch,
                IncludeItemTypes = [BaseItemKind.Audio],
            }))
            {
                // Checked per track, not per batch. Collectors do blocking work in here -- two
                // hundred albums is around a thousand tracks between two checkpoints, long enough
                // that Cancel does nothing and a task's runtime limit overshoots.
                cancellationToken.ThrowIfCancellationRequested();

                if (!seen.Add(track.Id))
                {
                    continue;
                }

                Each(live, logger, c => c.Visit(track));
            }
        }

        return seen.Count;
    }

    /// <summary>
    /// Runs one step on every live collector, dropping any that throws.
    /// <para>
    /// Cancellation is not a collector fault and is allowed straight through, so a cancelled task
    /// stops rather than quietly losing every collector one by one.
    /// </para>
    /// </summary>
    /// <param name="live">The collectors still taking part.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="step">What to do with each.</param>
    private static void Each(List<ITrackCollector> live, ILogger logger, Action<ITrackCollector> step)
    {
        for (var i = live.Count - 1; i >= 0; i--)
        {
            try
            {
                step(live[i]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One collector's fault must not become the other's.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(
                    ex,
                    "The {Collector} collector failed and has been dropped from this walk. The "
                    + "others continue; its own figures will be missing tonight.",
                    live[i].GetType().Name);
                live.RemoveAt(i);
            }
        }
    }
}
