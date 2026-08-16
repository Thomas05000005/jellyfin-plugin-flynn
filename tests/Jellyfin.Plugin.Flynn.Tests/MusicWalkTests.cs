using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Flynn.Modules.Music;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The shared walk: who reads the library, how often, and what happens when one reader fails.
/// <para>
/// Every track row costs a JSON deserialisation, because <c>Audio</c> carries no
/// <c>[RequiresSourceSerialisation]</c>. So "how many passes" is not a tidiness question on a
/// library of two hundred thousand tracks -- it is the whole cost of the nightly task.
/// </para>
/// </summary>
public sealed class MusicWalkTests
{
    /// <summary>
    /// The one that pays for itself. A switched-off module used to walk the whole library every
    /// night to produce a figure nobody would be shown.
    /// </summary>
    [Fact]
    public void WithNoCollectors_TheLibraryIsNotReadAtAll()
    {
        var world = new World(albums: 3, tracksPerAlbum: 4);

        MusicWalk.Run(world.Manager, NullLogger.Instance, [], CancellationToken.None);

        Assert.Equal(0, world.Queries);
    }

    /// <summary>
    /// Two audits, one pass. Running them separately reads every track twice, which on the library
    /// this was written for is 447 000 deserialisations instead of 223 000.
    /// </summary>
    [Fact]
    public void TwoCollectors_EachSeeEveryTrackOnce_OnOnePass()
    {
        var world = new World(albums: 3, tracksPerAlbum: 4);
        var first = new Counting();
        var second = new Counting();

        MusicWalk.Run(world.Manager, NullLogger.Instance, [first, second], CancellationToken.None);

        Assert.Equal(12, first.Visited.Count);
        Assert.Equal(12, second.Visited.Count);
        Assert.Equal(12, first.Visited.Distinct().Count());

        // Discovery, one album query, one track query -- for one library. Two separate walks
        // would double every one of them.
        Assert.Equal(3, world.Queries);
    }

    [Fact]
    public void OneCollector_CostsTheSameOnePass()
    {
        var world = new World(albums: 3, tracksPerAlbum: 4);
        var only = new Counting();

        MusicWalk.Run(world.Manager, NullLogger.Instance, [only], CancellationToken.None);

        Assert.Equal(12, only.Visited.Count);
        Assert.Equal(3, world.Queries);
    }

    /// <summary>
    /// The price of sharing a pass is that the two audits stop being independent. It must not be
    /// paid: a collector that throws is dropped, and the other finishes its night.
    /// </summary>
    [Fact]
    public void ACollectorThatThrows_IsDroppedAndTheOtherStillFinishes()
    {
        var world = new World(albums: 2, tracksPerAlbum: 3);
        var broken = new Counting { ThrowOnVisit = 2 };
        var sound = new Counting();

        MusicWalk.Run(world.Manager, NullLogger.Instance, [broken, sound], CancellationToken.None);

        Assert.Equal(6, sound.Visited.Count);
        Assert.True(sound.Finished);

        // It saw one track, threw on the second, and was never called again.
        Assert.Single(broken.Visited);
        Assert.False(broken.Finished);
    }

    /// <summary>
    /// The server joins AlbumIds on the album NAME, so two albums sharing one bring back each
    /// other's tracks. The walk owns that guard now, so every collector is spared it.
    /// </summary>
    [Fact]
    public void ATrackReachedTwiceByTheNameJoin_IsPassedOnOnce()
    {
        var world = new World(albums: 2, tracksPerAlbum: 3, sharedAlbumName: "Live");
        var only = new Counting();

        MusicWalk.Run(world.Manager, NullLogger.Instance, [only], CancellationToken.None);

        Assert.Equal(6, only.Visited.Count);
        Assert.Equal(6, only.Visited.Distinct().Count());
    }

    [Fact]
    public void EachLibraryIsOpenedAndClosedOnceAroundItsOwnTracks()
    {
        var world = new World(albums: 2, tracksPerAlbum: 2);
        var only = new Counting();

        MusicWalk.Run(world.Manager, NullLogger.Instance, [only], CancellationToken.None);

        Assert.Equal(1, only.Begun);
        Assert.True(only.Finished);
        Assert.Equal(4, only.TracksSeenAtFinish);
    }

    /// <summary>A collector that records what it was shown, and can be told to fail.</summary>
    private sealed class Counting : ITrackCollector
    {
        public List<Guid> Visited { get; } = [];

        public int Begun { get; private set; }

        public bool Finished { get; private set; }

        public int TracksSeenAtFinish { get; private set; }

        public int ThrowOnVisit { get; init; }

        public void Starting() => Visited.Clear();

        public void Begin(BaseItem library) => Begun++;

        public void Visit(BaseItem track)
        {
            if (ThrowOnVisit > 0 && Visited.Count + 1 == ThrowOnVisit)
            {
                throw new InvalidOperationException("this collector is having a bad night");
            }

            Visited.Add(track.Id);
        }

        public void Finish(BaseItem library, int tracksSeen)
        {
            Finished = true;
            TracksSeenAtFinish = tracksSeen;
        }
    }

    /// <summary>
    /// One music library whose <c>AlbumIds</c> behaves like the server's: the ids are resolved to
    /// album NAMES and every track whose <c>Album</c> matches comes back, whichever batch asked.
    /// </summary>
    private sealed class World
    {
        private readonly List<Audio> _tracks = [];
        private readonly Dictionary<Guid, string> _albumNames = [];
        private readonly Folder _library = new() { Id = Guid.NewGuid(), Name = "Musique" };

        public World(int albums, int tracksPerAlbum, string? sharedAlbumName = null)
        {
            for (var a = 0; a < albums; a++)
            {
                var albumId = Guid.NewGuid();
                var name = sharedAlbumName ?? $"album {a}";
                _albumNames[albumId] = name;

                for (var t = 0; t < tracksPerAlbum; t++)
                {
                    _tracks.Add(new Audio { Id = Guid.NewGuid(), ParentId = albumId, Album = name });
                }
            }

            Manager = Substitute.For<ILibraryManager>();
            Manager.GetContentType(_library).Returns(CollectionType.music);
            Manager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(call =>
            {
                var query = call.Arg<InternalItemsQuery>();
                var kind = query.IncludeItemTypes.FirstOrDefault();

                // Every read counts, discovery included: "nothing was read" has to mean nothing.
                Queries++;

                if (kind == BaseItemKind.CollectionFolder)
                {
                    return new List<BaseItem> { _library };
                }

                if (kind == BaseItemKind.MusicAlbum)
                {
                    return _albumNames
                        .Select(BaseItem (kv) => new MusicAlbum { Id = kv.Key, Name = kv.Value })
                        .ToList();
                }

                var wanted = query.AlbumIds
                    .Select(id => _albumNames[id])
                    .ToHashSet(StringComparer.Ordinal);

                return _tracks
                    .Where(t => t.Album is not null && wanted.Contains(t.Album))
                    .Cast<BaseItem>()
                    .ToList();
            });
        }

        public ILibraryManager Manager { get; }

        /// <summary>Gets how many times the library was read, discovery included.</summary>
        public int Queries { get; private set; }
    }
}
