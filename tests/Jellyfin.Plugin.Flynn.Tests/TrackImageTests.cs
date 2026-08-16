using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Flynn.Modules.Music;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The per-track cover audit.
/// <para>
/// The number this produces leads somewhere destructive -- an administrator reading "twenty
/// gigabytes can be reclaimed" will eventually want them reclaimed. So the tests that matter most
/// are not the ones checking the arithmetic; they are the ones checking what is left out. A cover
/// the administrator put next to their music must never appear in that figure, whatever it costs.
/// </para>
/// </summary>
public sealed class TrackImageTests
{
    private const string MetadataRoot = "/config/metadata";

    /// <summary>
    /// The one that guards against data loss. An image whose path is in the media folder is the
    /// administrator's own file; the server did not create it and Flynn must not offer it.
    /// </summary>
    [Fact]
    public void ACoverSittingNextToTheMusic_IsNeverCounted()
    {
        var track = TrackWithCover("/data/Musiques28/Neffex/This Is Our Calling (2023)/cover.jpg");

        Assert.Null(TrackImageAudit.StoredCover(track, MetadataRoot));
    }

    [Fact]
    public void ACoverInTheServersOwnFolder_IsCounted()
    {
        var track = TrackWithCover("/config/metadata/library/a1/a1b2/Primary.jpg");

        Assert.NotNull(TrackImageAudit.StoredCover(track, MetadataRoot));
    }

    [Fact]
    public void ATrackWithNoCoverAtAll_IsSkipped()
    {
        Assert.Null(TrackImageAudit.StoredCover(new Audio { Id = Guid.NewGuid() }, MetadataRoot));
    }

    /// <summary>
    /// A remote image has no bytes on this disk to reclaim, so it is out of scope even though its
    /// recorded path is not a media path either.
    /// </summary>
    [Fact]
    public void ACoverHeldAsAUrl_IsNotCounted()
    {
        var track = TrackWithCover("https://example.invalid/cover.jpg");

        Assert.Null(TrackImageAudit.StoredCover(track, MetadataRoot));
    }

    [Fact]
    public void FifteenIdenticalCovers_CountFourteenAsCopies()
    {
        var covers = Enumerable.Repeat((120_000L, 1000, 1000), 15).ToList();

        var (images, bytes) = TrackImageAudit.CopiesWithin(covers);

        Assert.Equal(14, images);
        Assert.Equal(14 * 120_000L, bytes);
    }

    [Fact]
    public void CoversOfDifferentSizes_AreNotCalledCopies()
    {
        var covers = new List<(long, int, int)> { (100L, 500, 500), (200L, 500, 500), (300L, 500, 500) };

        Assert.Equal((0, 0L), TrackImageAudit.CopiesWithin(covers));
    }

    /// <summary>
    /// Equal byte length is not enough on its own. Two covers of the same weight but different
    /// dimensions are two different images, and calling them copies would promise bytes that
    /// deleting one would not free without losing something.
    /// </summary>
    [Fact]
    public void CoversOfEqualLengthButDifferentDimensions_AreNotCopies()
    {
        var covers = new List<(long, int, int)> { (120_000L, 1000, 1000), (120_000L, 1400, 1400) };

        Assert.Equal((0, 0L), TrackImageAudit.CopiesWithin(covers));
    }

    [Fact]
    public void ThreeTracksSharingOneCover_ReportTwoRedundantCopies()
    {
        var album = Guid.NewGuid();
        var audit = new Fixture()
            .Track(album, "/config/metadata/library/01/one/Primary.jpg", 120_000)
            .Track(album, "/config/metadata/library/02/two/Primary.jpg", 120_000)
            .Track(album, "/config/metadata/library/03/three/Primary.jpg", 120_000)
            .Build();

        var library = Assert.Single(audit.Run(CancellationToken.None));

        Assert.Equal(3, library.TracksWithImage);
        Assert.Equal(360_000L, library.TotalBytes);
        Assert.Equal(2, library.RedundantImages);
        Assert.Equal(240_000L, library.RedundantBytes);
    }

    /// <summary>
    /// Two discs of one release, each with its own art of the same weight. Counting across the
    /// whole album would call three of the four a copy; counting per folder calls two, which is the
    /// answer that survives the discs really being different.
    /// </summary>
    [Fact]
    public void TwoDiscFoldersOfOneRelease_AreCountedSeparately()
    {
        var discOne = Guid.NewGuid();
        var discTwo = Guid.NewGuid();
        var audit = new Fixture()
            .Track(discOne, "/config/metadata/library/01/a/Primary.jpg", 90_000)
            .Track(discOne, "/config/metadata/library/02/b/Primary.jpg", 90_000)
            .Track(discTwo, "/config/metadata/library/03/c/Primary.jpg", 90_000)
            .Track(discTwo, "/config/metadata/library/04/d/Primary.jpg", 90_000)
            .Build();

        var library = Assert.Single(audit.Run(CancellationToken.None));

        Assert.Equal(2, library.RedundantImages);
        Assert.Equal(180_000L, library.RedundantBytes);
    }

    /// <summary>
    /// Recorded on the item, absent from disk. Reclaiming it would free nothing, so it must not
    /// appear in a figure whose only purpose is to say how much would be freed.
    /// </summary>
    [Fact]
    public void ACoverRecordedButMissingFromDisk_PromisesNoBytes()
    {
        var album = Guid.NewGuid();
        var audit = new Fixture()
            .Track(album, "/config/metadata/library/01/one/Primary.jpg", 120_000)
            .Missing(album, "/config/metadata/library/02/gone/Primary.jpg")
            .Build();

        var library = Assert.Single(audit.Run(CancellationToken.None));

        Assert.Equal(1, library.TracksWithImage);
        Assert.Equal(120_000L, library.TotalBytes);
        Assert.Equal(0, library.RedundantImages);
    }

    /// <summary>The same guard as the unit test, but end to end through a whole run.</summary>
    [Fact]
    public void CoversNextToTheMusic_NeverReachTheReclaimableFigure()
    {
        var album = Guid.NewGuid();
        var audit = new Fixture()
            .Track(album, "/data/Musiques28/Artiste/Album (2024)/cover.jpg", 4_000_000)
            .Track(album, "/data/Musiques28/Artiste/Album (2024)/cover.jpg", 4_000_000)
            .Build();

        var library = Assert.Single(audit.Run(CancellationToken.None));

        Assert.Equal(0, library.TracksWithImage);
        Assert.Equal(0L, library.TotalBytes);
        Assert.Equal(0L, library.RedundantBytes);
    }

    [Fact]
    public void ALibraryWithNoStoredCovers_HasNoShareRatherThanDividingByZero()
    {
        var empty = new LibraryTrackImages(Guid.NewGuid(), "Musique", 0, 0, 0, 0);

        Assert.Equal(0, empty.RedundantShare);
    }

    /// <summary>
    /// More albums than one batch holds, so the chunking is actually taken round the loop. The
    /// fixture asserts no batch exceeds the declared size, which keeps this from passing against a
    /// version that quietly asks for all of them at once.
    /// </summary>
    [Fact]
    public void MoreAlbumsThanOneBatch_AreAllCounted()
    {
        var fixture = new Fixture();
        const int Albums = TrackImageAudit.AlbumBatchSize + 40;
        for (var i = 0; i < Albums; i++)
        {
            var album = Guid.NewGuid();
            fixture.Track(album, $"/config/metadata/library/{i}/a/Primary.jpg", 50_000);
            fixture.Track(album, $"/config/metadata/library/{i}/b/Primary.jpg", 50_000);
        }

        var library = Assert.Single(fixture.Build().Run(CancellationToken.None));

        Assert.Equal(Albums * 2, library.TracksWithImage);
        Assert.Equal(Albums, library.RedundantImages);
    }

    /// <summary>
    /// Two albums that happen to share a name -- "Greatest Hits", "Live", an eponymous record --
    /// landing either side of a batch boundary.
    /// <para>
    /// Because the server joins on the album NAME, each batch returns the tracks of both albums.
    /// Without a guard the same file is counted twice, and the figure promises bytes that deleting
    /// anything would not free. Forty thousand albums make two hundred boundaries, so on a real
    /// library this is not an edge case, it is arithmetic.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoAlbumsSharingANameAcrossABatchBoundary_AreCountedOnce()
    {
        var fixture = new Fixture();

        // One album per batch position, so the two homonyms cannot land in the same batch.
        var first = Guid.NewGuid();
        fixture.Track(first, "/config/metadata/library/aa/1/Primary.jpg", 70_000, albumName: "Live");
        for (var i = 0; i < TrackImageAudit.AlbumBatchSize; i++)
        {
            fixture.Track(Guid.NewGuid(), $"/config/metadata/library/f{i}/x/Primary.jpg", 1_000);
        }

        var second = Guid.NewGuid();
        fixture.Track(second, "/config/metadata/library/bb/1/Primary.jpg", 70_000, albumName: "Live");

        var library = Assert.Single(fixture.Build().Run(CancellationToken.None));

        Assert.Equal(TrackImageAudit.AlbumBatchSize + 2, library.TracksWithImage);
        Assert.Equal((TrackImageAudit.AlbumBatchSize * 1_000L) + 140_000L, library.TotalBytes);

        // The two homonyms sit in different folders, so neither is a copy of the other.
        Assert.Equal(0, library.RedundantImages);
    }

    private static Audio TrackWithCover(string path) => new()
    {
        Id = Guid.NewGuid(),
        ImageInfos =
        [
            new ItemImageInfo { Path = path, Type = ImageType.Primary, Width = 1000, Height = 1000 },
        ],
    };

    /// <summary>
    /// One music library, one album per distinct folder, and a filesystem that knows the size of
    /// every path the fixture was told about.
    /// <para>
    /// The library manager models what the server actually does with <c>AlbumIds</c>, which is not
    /// what its name suggests. <c>BaseItemRepository.TranslateQuery</c> resolves the ids to album
    /// NAMES and then matches every track whose <c>Album</c> string equals one of them:
    /// </para>
    /// <code>
    /// var subQuery = context.BaseItems.WhereOneOrMany(filter.AlbumIds, f =&gt; f.Id);
    /// baseQuery = baseQuery.Where(e =&gt; subQuery.Any(f =&gt; f.Name == e.Album));
    /// </code>
    /// <para>
    /// An earlier version of this fixture filtered by id, which is the semantics the module's own
    /// notes claimed. It agreed with the belief rather than with the server, so the double counting
    /// that belief caused was structurally inexpressible here. A test double that is kinder than
    /// production is not a test.
    /// </para>
    /// </summary>
    private sealed class Fixture
    {
        private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, List<Audio>> _byFolder = [];
        private readonly Dictionary<Guid, string> _albumNames = [];

        public Fixture Track(
            Guid folder, string path, long length, int width = 1000, int height = 1000,
            string? albumName = null)
        {
            _sizes[path] = length;
            return Add(folder, path, width, height, albumName);
        }

        /// <summary>Records a cover on the item without giving it a file on disk.</summary>
        /// <param name="folder">The folder the track sits in.</param>
        /// <param name="path">Where the cover claims to be.</param>
        /// <returns>The fixture.</returns>
        public Fixture Missing(Guid folder, string path) => Add(folder, path, 1000, 1000, null);

        public TrackImageAudit Build()
        {
            var library = new Folder { Id = Guid.NewGuid(), Name = "Musique" };
            var albums = _byFolder.Keys.ToList();
            var everyTrack = _byFolder.Values.SelectMany(t => t).ToList();

            var manager = Substitute.For<ILibraryManager>();
            manager.GetContentType(library).Returns(CollectionType.music);
            manager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(call =>
            {
                var query = call.Arg<InternalItemsQuery>();
                var kind = query.IncludeItemTypes.FirstOrDefault();

                if (kind == BaseItemKind.CollectionFolder)
                {
                    return new List<BaseItem> { library };
                }

                if (kind == BaseItemKind.MusicAlbum)
                {
                    return albums
                        .Select(BaseItem (id) => new MusicAlbum { Id = id, Name = _albumNames[id] })
                        .ToList();
                }

                Assert.True(
                    query.AlbumIds.Length <= TrackImageAudit.AlbumBatchSize,
                    $"asked for {query.AlbumIds.Length} albums at once");

                // The server applies no library scope of its own to a name join, so a walk that
                // does not ask for one silently reaches other libraries.
                Assert.Equal(library.Id, query.ParentId);
                Assert.True(query.Recursive, "a track query must be recursive to be scoped at all");

                var names = query.AlbumIds
                    .Select(id => _albumNames[id])
                    .ToHashSet(StringComparer.Ordinal);

                return everyTrack
                    .Where(t => t.Album is not null && names.Contains(t.Album))
                    .Cast<BaseItem>()
                    .ToList();
            });

            var paths = Substitute.For<IServerApplicationPaths>();
            paths.InternalMetadataPath.Returns(MetadataRoot);

            var files = Substitute.For<IFileSystem>();
            files.GetFileInfo(Arg.Any<string>()).Returns(call =>
            {
                var path = call.Arg<string>();
                return _sizes.TryGetValue(path, out var length)
                    ? new FileSystemMetadata { Exists = true, Length = length }
                    : new FileSystemMetadata { Exists = false };
            });

            return new TrackImageAudit(
                manager, paths, files, NullLogger<TrackImageAudit>.Instance);
        }

        private Fixture Add(Guid folder, string path, int width, int height, string? albumName)
        {
            if (!_byFolder.TryGetValue(folder, out var tracks))
            {
                tracks = [];
                _byFolder[folder] = tracks;
            }

            // Distinct by default, so an album name only collides when a test asks it to.
            _albumNames[folder] = albumName ?? _albumNames.GetValueOrDefault(folder, folder.ToString("N"));

            var track = TrackWithCover(path);
            track.ParentId = folder;
            track.Album = _albumNames[folder];
            track.ImageInfos[0].Width = width;
            track.ImageInfos[0].Height = height;
            tracks.Add(track);
            return this;
        }
    }
}
