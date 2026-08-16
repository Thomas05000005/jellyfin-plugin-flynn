using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>What one music library's per-track cover art costs on disk.</summary>
/// <param name="LibraryId">The library's id.</param>
/// <param name="LibraryName">Its display name.</param>
/// <param name="TracksWithImage">Tracks carrying a cover in the server's own metadata folder.</param>
/// <param name="TotalBytes">What those covers occupy.</param>
/// <param name="RedundantImages">Copies beyond the first within one album.</param>
/// <param name="RedundantBytes">What those copies occupy.</param>
public sealed record LibraryTrackImages(
    Guid LibraryId,
    string LibraryName,
    int TracksWithImage,
    long TotalBytes,
    int RedundantImages,
    long RedundantBytes)
{
    /// <summary>Gets the share of the stored bytes that is duplication, from 0 to 1.</summary>
    public double RedundantShare => TotalBytes <= 0 ? 0 : (double)RedundantBytes / TotalBytes;
}

/// <summary>
/// Measures what the server's per-track cover art occupies, and how much of it is the same image
/// stored again and again.
/// <para>
/// The waste is not an accident of one install, it is what the code does. Three steps of the
/// server, each reasonable alone:
/// </para>
/// <para>
/// <c>AudioImageProvider</c> extracts a track's embedded cover into the cache under a filename
/// derived from <c>MD5(Album + "-" + AlbumArtist)</c>. That is a deliberate deduplication: every
/// track of one album resolves to a single cache file.
/// </para>
/// <para>
/// <c>ItemImageProvider</c> then hands that path to <c>ProviderManager.SaveImage</c>, and
/// <c>ImageSaver</c> opens with <c>saveLocally = ... &amp;&amp; item is not Audio</c>. For an audio
/// track that is always false, so it always falls through to
/// <c>Path.Combine(item.GetInternalMetadataPath(), ...)</c> -- the item's own folder.
/// </para>
/// <para>
/// The deduplication of the first step is therefore undone by the third: one cache file is copied
/// into every track's private metadata folder. A fifteen-track album stores the same cover fifteen
/// times, plus once more for the album itself.
/// </para>
/// <para>
/// That same line has a second consequence worth knowing before an administrator goes looking for a
/// setting: because the test excludes <c>Audio</c> outright, "save artwork into media folders" has
/// no effect at all on music tracks. Turning it on or off changes nothing here.
/// </para>
/// </summary>
public sealed class TrackImageAudit : ITrackCollector
{
    /// <summary>How many albums are asked about at once. See <see cref="MusicWalk"/>.</summary>
    internal const int AlbumBatchSize = MusicWalk.AlbumBatchSize;

    private readonly ILibraryManager _library;
    private readonly IServerApplicationPaths _paths;
    private readonly IFileSystem _files;
    private readonly ILogger<TrackImageAudit> _logger;

    private readonly List<LibraryTrackImages> _results = [];
    private readonly Dictionary<Guid, List<(long Length, int Width, int Height)>> _byFolder = [];
    private string _metadataRoot = string.Empty;
    private int _count;
    private long _bytes;

    /// <summary>Initializes a new instance of the <see cref="TrackImageAudit"/> class.</summary>
    /// <param name="library">The server's library manager.</param>
    /// <param name="paths">Where the server keeps its own metadata.</param>
    /// <param name="files">Filesystem access, for the size of each stored image.</param>
    /// <param name="logger">Logger.</param>
    public TrackImageAudit(
        ILibraryManager library,
        IServerApplicationPaths paths,
        IFileSystem files,
        ILogger<TrackImageAudit> logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets what the last walk found, one entry per music library.</summary>
    internal IReadOnlyList<LibraryTrackImages> Results => _results;

    /// <summary>Audits every music library, on a walk of its own.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per music library.</returns>
    public IReadOnlyList<LibraryTrackImages> Run(CancellationToken cancellationToken)
    {
        MusicWalk.Run(_library, _logger, [this], cancellationToken);
        return _results;
    }

    /// <inheritdoc />
    public void Starting()
    {
        _results.Clear();
        _metadataRoot = _paths.InternalMetadataPath;
    }

    /// <inheritdoc />
    public void Begin(BaseItem library)
    {
        _byFolder.Clear();
        _count = 0;
        _bytes = 0;
    }

    /// <inheritdoc />
    public void Visit(BaseItem track)
    {
        var cover = StoredCover(track, _metadataRoot);
        if (cover is null)
        {
            return;
        }

        var length = LengthOf(cover.Path);
        if (length <= 0)
        {
            // Recorded on the item but gone from disk, or unreadable. Counting it would promise
            // bytes that cannot be reclaimed.
            return;
        }

        _count++;
        _bytes += length;

        // Grouped by the folder the track sits in rather than by the album, so a multi-disc release
        // counts its copies per disc. That is the conservative direction: two discs that really do
        // carry different art are never merged into one false duplicate.
        if (!_byFolder.TryGetValue(track.ParentId, out var covers))
        {
            covers = [];
            _byFolder[track.ParentId] = covers;
        }

        covers.Add((length, cover.Width, cover.Height));
    }

    /// <inheritdoc />
    public void Finish(BaseItem library, int tracksSeen)
    {
        ArgumentNullException.ThrowIfNull(library);

        var redundant = 0;
        var redundantBytes = 0L;
        foreach (var covers in _byFolder.Values)
        {
            var (images, copyBytes) = CopiesWithin(covers);
            redundant += images;
            redundantBytes += copyBytes;
        }

        _logger.LogDebug(
            "{Library}: {Count} track covers in the metadata folder, {Bytes} bytes, of which "
            + "{Redundant} are copies worth {RedundantBytes} bytes.",
            library.Name,
            _count,
            _bytes,
            redundant,
            redundantBytes);

        _results.Add(new LibraryTrackImages(
            library.Id, library.Name, _count, _bytes, redundant, redundantBytes));

        _byFolder.Clear();
    }

    /// <summary>
    /// Picks out a track's cover only when the server is the one storing it.
    /// <para>
    /// An image pointing at the media folder is the administrator's own file, sitting next to the
    /// music. Flynn has no business counting it, still less proposing to reclaim it: it costs the
    /// server nothing and deleting it would destroy something the server did not create. Only
    /// images written under the internal metadata path are in scope.
    /// </para>
    /// </summary>
    /// <param name="track">The track.</param>
    /// <param name="metadataRoot">The server's internal metadata path.</param>
    /// <returns>The stored cover, or null when there is none to count.</returns>
    internal static ItemImageInfo? StoredCover(BaseItem track, string metadataRoot)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrEmpty(metadataRoot))
        {
            return null;
        }

        var image = track.GetImageInfo(ImageType.Primary, 0);

        return image is not null
            && image.IsLocalFile
            && image.Path.StartsWith(metadataRoot, StringComparison.OrdinalIgnoreCase)
                ? image
                : null;
    }

    /// <summary>
    /// Counts copies within one album folder.
    /// <para>
    /// Two covers are treated as the same image when their byte length and both dimensions match.
    /// That is evidence rather than proof, and the choice is deliberate: hashing tens of gigabytes
    /// every night to confirm what the server's own code already guarantees would cost more than
    /// the answer is worth. Since every track of an album is a copy of one cache file, equal sizes
    /// inside one album are copies. The failure mode is bounded and one-sided in practice -- two
    /// genuinely different covers of identical length and identical dimensions inside the same
    /// album would overcount by one, which cannot move a figure quoted in gigabytes.
    /// </para>
    /// </summary>
    /// <param name="covers">Every stored cover found in one album, as length and dimensions.</param>
    /// <returns>How many are copies, and what they occupy.</returns>
    internal static (int Images, long Bytes) CopiesWithin(
        IReadOnlyList<(long Length, int Width, int Height)> covers)
    {
        ArgumentNullException.ThrowIfNull(covers);

        var images = 0;
        var bytes = 0L;

        foreach (var identical in covers.GroupBy(cover => cover))
        {
            var copies = identical.Count() - 1;
            if (copies > 0)
            {
                images += copies;
                bytes += copies * identical.Key.Length;
            }
        }

        return (images, bytes);
    }

    private long LengthOf(string path)
    {
        try
        {
            var info = _files.GetFileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not size {Path}.", path);
            return 0;
        }
    }
}
