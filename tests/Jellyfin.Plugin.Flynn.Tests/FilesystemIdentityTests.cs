using Jellyfin.Plugin.Flynn.Modules.Storage;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Grouping on the device id alone is right until someone splits a library into its own ZFS
/// dataset, at which point it silently starts double counting the pool's free space again. These
/// tests pin the case that has not happened yet on the target server but was announced.
/// </summary>
public class FilesystemIdentityTests
{
    private static MountEntry Zfs(string deviceId, string dataset) =>
        new(deviceId, "/data/x", "zfs", dataset);

    /// <summary>Today's layout: subdirectory bind mounts of one dataset, one device id.</summary>
    [Fact]
    public void BindMountsOfOneDataset_ShareAnIdentity()
    {
        var films = Zfs("0:42", "RAID-Z1");
        var musiques = Zfs("0:42", "RAID-Z1");

        Assert.Equal(FilesystemIdentity.Of(films), FilesystemIdentity.Of(musiques));
    }

    /// <summary>
    /// Tomorrow's layout, and the reason this class exists. ZFS gives each dataset its own
    /// anonymous superblock, so the device ids differ while the free space does not.
    /// </summary>
    [Fact]
    public void SeparateDatasetsOfOnePool_StillShareAnIdentity()
    {
        var films = Zfs("0:44", "RAID-Z1/Media/Films");
        var musiques = Zfs("0:45", "RAID-Z1/Media/Musiques");

        Assert.Equal(FilesystemIdentity.Of(films), FilesystemIdentity.Of(musiques));
        Assert.NotEqual(films.DeviceId, musiques.DeviceId);
    }

    [Fact]
    public void DifferentPools_DoNotShareAnIdentity()
    {
        Assert.NotEqual(
            FilesystemIdentity.Of(Zfs("0:42", "RAID-Z1")),
            FilesystemIdentity.Of(Zfs("0:43", "RAIDZ28")));
    }

    /// <summary>
    /// A pool whose name is a prefix of another must not be folded into it: RAID-Z1 and RAID-Z10
    /// are different pools, and cutting at the separator rather than by prefix is what keeps them
    /// apart.
    /// </summary>
    [Fact]
    public void PoolNamesSharingAPrefix_StayApart()
    {
        Assert.NotEqual(
            FilesystemIdentity.Of(Zfs("0:42", "RAID-Z1/Media")),
            FilesystemIdentity.Of(Zfs("0:46", "RAID-Z10/Media")));
    }

    [Theory]
    [InlineData("RAID-Z1", "RAID-Z1")]
    [InlineData("RAID-Z1/Media", "RAID-Z1")]
    [InlineData("RAID-Z1/Media/Films", "RAID-Z1")]
    [InlineData("RAID-Z1/Media@daily-2026-08-05", "RAID-Z1")]
    public void ThePoolName_IsWhatIsShown(string dataset, string expected)
    {
        Assert.Equal(expected, FilesystemIdentity.DisplayNameOf(Zfs("0:42", dataset)));
    }

    /// <summary>
    /// ext4 and friends own their space outright, so the device id already answers the question and
    /// nothing should be folded together.
    /// </summary>
    [Fact]
    public void AnOrdinaryFilesystem_IsIdentifiedByItsDevice()
    {
        var a = new MountEntry("8:1", "/mnt/a", "ext4", "/dev/sda1");
        var b = new MountEntry("8:2", "/mnt/b", "ext4", "/dev/sda2");

        Assert.Equal("8:1", FilesystemIdentity.Of(a));
        Assert.NotEqual(FilesystemIdentity.Of(a), FilesystemIdentity.Of(b));
        Assert.Equal("/dev/sda1", FilesystemIdentity.DisplayNameOf(a));
    }

    /// <summary>
    /// Btrfs subvolumes share a filesystem's space the same way ZFS datasets share a pool's.
    /// </summary>
    [Fact]
    public void BtrfsSubvolumes_ShareAnIdentity()
    {
        var a = new MountEntry("0:50", "/mnt/a", "btrfs", "tank/sub1");
        var b = new MountEntry("0:51", "/mnt/b", "btrfs", "tank/sub2");

        Assert.Equal(FilesystemIdentity.Of(a), FilesystemIdentity.Of(b));
    }

    /// <summary>
    /// A source that is a path rather than a dataset name has no pool to read, so it falls back to
    /// the device id instead of inventing one out of a directory component.
    /// </summary>
    [Theory]
    [InlineData("/dev/sda1")]
    [InlineData("")]
    [InlineData("   ")]
    public void ASourceWithNoPoolName_FallsBackToTheDevice(string source)
    {
        Assert.Equal("0:42", FilesystemIdentity.Of(new MountEntry("0:42", "/data/x", "zfs", source)));
    }
}
