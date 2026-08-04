using Jellyfin.Plugin.Flynn.Modules.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The sweep's two ways of being quietly wrong: stopping early on a large library, and counting
/// one disk once per library that sits on it. Both produce a plausible number, which is the worst
/// kind of wrong for something a forecast is drawn from.
/// </summary>
public class StorageSweepTests
{
    /// <summary>
    /// A library bigger than one page is where an off-by-one in the paging shows up, and nowhere
    /// smaller. 2500 items over a page size of 1000 covers full pages and a short final one.
    /// </summary>
    [Fact]
    public async Task ALibraryLargerThanOnePage_IsCountedInFull()
    {
        const int Total = 2500;
        var libraries = LibraryManagerWith(("Music", Total, 1_000_000L));

        var snapshot = await Sweep(libraries).RunAsync(null, CancellationToken.None);

        var measured = Assert.Single(snapshot.Libraries);
        Assert.Equal(Total, measured.ItemCount);
        Assert.Equal(Total * 1_000_000L, measured.Bytes);
    }

    /// <summary>A library that exactly fills a page must not be read a second time or cut short.</summary>
    [Fact]
    public async Task ALibraryThatExactlyFillsAPage_IsCountedOnce()
    {
        var libraries = LibraryManagerWith(("Films", StorageSweep.PageSize, 10L));

        var snapshot = await Sweep(libraries).RunAsync(null, CancellationToken.None);

        Assert.Equal(StorageSweep.PageSize, Assert.Single(snapshot.Libraries).ItemCount);
    }

    /// <summary>
    /// Size is null until the server has measured a file. Counting that as zero is the honest
    /// reading -- unknown, not empty -- and it must not throw.
    /// </summary>
    [Fact]
    public async Task ItemsWithNoKnownSize_CountTowardsTheTallyButNotTheBytes()
    {
        var libraries = LibraryManagerWith(("Mixed", 10, null));

        var snapshot = await Sweep(libraries).RunAsync(null, CancellationToken.None);

        var measured = Assert.Single(snapshot.Libraries);
        Assert.Equal(10, measured.ItemCount);
        Assert.Equal(0, measured.Bytes);
    }

    /// <summary>One unusable library must cost one row, not the whole sweep.</summary>
    [Fact]
    public async Task ALibraryWithAnUnusableId_IsSkippedAndTheRestAreMeasured()
    {
        var libraries = Substitute.For<ILibraryManager>();
        libraries.GetVirtualFolders().Returns(
        [
            new VirtualFolderInfo { Name = "Broken", ItemId = "not-a-guid", Locations = [] },
            new VirtualFolderInfo
            {
                Name = "Fine",
                ItemId = Guid.NewGuid().ToString("N"),
                Locations = [],
            },
        ]);
        libraries.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(Items(5, 100));

        var snapshot = await Sweep(libraries).RunAsync(null, CancellationToken.None);

        Assert.Equal("Fine", Assert.Single(snapshot.Libraries).LibraryName);
    }

    /// <summary>
    /// The one that makes a forecast wrong rather than absent. Three libraries on one pool must
    /// report its free space once; reporting it three times makes the projection three times too
    /// optimistic.
    /// <para>
    /// Uses paths that really exist, because the first version of this test did not and passed on
    /// Windows for the wrong reason: DriveInfo there only reads the drive letter, so a
    /// non-existent path still resolved. On Linux it did not, and that empty result was hiding a
    /// worse bug than the one being tested for.
    /// </para>
    /// </summary>
    [Fact]
    public void SeveralLibrariesOnOneDisk_ReportThatDiskOnce()
    {
        var root = Directory.CreateTempSubdirectory("flynn-probe");
        try
        {
            var probe = new DriveProbe(NullLogger<DriveProbe>.Instance);
            var libraries = new[] { "films", "series", "music" }
                .Select(name => Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName)
                .ToArray();

            var devices = probe.Probe(libraries);

            Assert.Single(devices);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The matching rule, tested without needing a second disk. Picking the longest mount point is
    /// what makes a library on a pool resolve to the pool rather than to the root filesystem, and
    /// therefore what makes two libraries on that pool share one entry.
    /// </summary>
    [Fact]
    public void TheLongestMatchingMountPoint_Wins()
    {
        var mounts = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
        var deepest = mounts.MaxBy(m => m.RootDirectory.FullName.Length)!;

        var holder = DriveProbe.MountHolding(mounts, deepest.RootDirectory.FullName);

        Assert.Equal(deepest.RootDirectory.FullName, holder!.RootDirectory.FullName);
    }

    /// <summary>
    /// A sibling that merely shares a prefix must not be mistaken for something inside the mount
    /// point: "/mnt/poolroom" is not on "/mnt/pool".
    /// </summary>
    [Fact]
    public void APathThatMerelySharesAPrefix_IsNotAMatch()
    {
        var mounts = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
        var shortest = mounts.MinBy(m => m.RootDirectory.FullName.Length)!.RootDirectory.FullName;
        var lookalike = shortest.TrimEnd(Path.DirectorySeparatorChar) + "xyz-not-a-mount";

        var holder = DriveProbe.MountHolding(mounts, lookalike);

        Assert.True(
            holder is null || holder.RootDirectory.FullName.Length <= shortest.Length,
            "a path sharing a prefix with a mount point must not resolve to it");
    }

    [Fact]
    public void UnreadableOrEmptyPaths_DoNotStopTheProbe()
    {
        var probe = new DriveProbe(NullLogger<DriveProbe>.Instance);

        var devices = probe.Probe([string.Empty, "   ", Path.GetTempPath()]);

        Assert.Single(devices);
    }


    private static StorageSweep Sweep(ILibraryManager libraries) =>
        new(libraries, new NoDrives(), TimeProvider.System, NullLogger<StorageSweep>.Instance);

    private static ILibraryManager LibraryManagerWith(params (string Name, int Count, long? Size)[] libraries)
    {
        var manager = Substitute.For<ILibraryManager>();
        var folders = new List<VirtualFolderInfo>();

        foreach (var (name, count, size) in libraries)
        {
            var id = Guid.NewGuid();
            folders.Add(new VirtualFolderInfo
            {
                Name = name,
                ItemId = id.ToString("N"),
                Locations = [],
            });

            // Paged the way the real manager answers: full pages until the remainder.
            manager
                .GetItemList(Arg.Is<InternalItemsQuery>(q => q.AncestorIds.Contains(id)))
                .Returns(call =>
                {
                    var query = call.Arg<InternalItemsQuery>();
                    var start = query.StartIndex ?? 0;
                    var take = Math.Clamp(count - start, 0, query.Limit ?? count);
                    return Items(take, size);
                });
        }

        manager.GetVirtualFolders().Returns(folders);
        return manager;
    }

    private static IReadOnlyList<BaseItem> Items(int count, long? size) =>
        [.. Enumerable.Range(0, count).Select(_ => (BaseItem)new Audio { Size = size })];

    /// <summary>A probe that finds nothing, so library measuring can be tested on its own.</summary>
    private sealed class NoDrives : IDriveProbe
    {
        public IReadOnlyList<DeviceSnapshot> Probe(IEnumerable<string> paths) => [];
    }
}
