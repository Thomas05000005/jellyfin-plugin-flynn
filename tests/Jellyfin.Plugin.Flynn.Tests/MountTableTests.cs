using Jellyfin.Plugin.Flynn.Modules.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Built around one real mount table, taken from the server this plugin was written for. Two ZFS
/// pools, eight bind mounts, one library folder with a space in its name. Synthetic fixtures would
/// not have produced the escaped space, and DriveInfo alone would have reported eight devices
/// where there are two.
/// </summary>
public class MountTableTests
{
    /// <summary>
    /// Verbatim from <c>docker exec jellyfin cat /proc/self/mountinfo</c> on the target server.
    /// Six mounts share device 0:42, two share 0:43.
    /// </summary>
    private static readonly string[] RealMountInfo =
    [
        "1990 1931 0:42 /Media/Films /data/Films rw,noatime - zfs RAID-Z1 rw,xattr,posixacl,casesensitive",
        "1991 1931 0:43 /Media28/Musiques28 /data/Musiques28 rw,noatime - zfs RAIDZ28 rw,xattr,posixacl,casesensitive",
        "1992 1931 0:42 /Media/downloads /data/downloads rw,noatime - zfs RAID-Z1 rw,xattr,posixacl,casesensitive",
        "1993 1931 0:42 /Media/Séries /data/Séries rw,noatime - zfs RAID-Z1 rw,xattr,posixacl,casesensitive",
        "1994 1931 0:42 /Media/Musiques /data/Musiques rw,noatime - zfs RAID-Z1 rw,xattr,posixacl,casesensitive",
        "1995 1931 0:42 /Media/Livre /data/Livre rw,noatime - zfs RAID-Z1 rw,xattr,posixacl,casesensitive",
        "1996 1931 0:43 /Media28/Séries28 /data/Séries28 rw,noatime - zfs RAIDZ28 rw,xattr,posixacl,casesensitive",
        "1997 1931 0:42 /Media/Famille\\040priver /data/Priver rw,noatime - zfs RAID-Z1 rw,xattr,posixacl,casesensitive",
    ];

    [Fact]
    public void TheRealMountTable_ParsesIntoEightMounts()
    {
        var mounts = MountTable.Parse(RealMountInfo);

        Assert.Equal(8, mounts.Count);
        Assert.All(mounts, m => Assert.Equal("zfs", m.FileSystemType));
    }

    /// <summary>
    /// The whole reason this file exists. Eight mounts, two filesystems, and the device id is what
    /// says so.
    /// </summary>
    [Fact]
    public void EightMounts_AreTwoFilesystems()
    {
        var mounts = MountTable.Parse(RealMountInfo);

        var devices = mounts.Select(m => m.DeviceId).Distinct().Order().ToList();

        Assert.Equal(["0:42", "0:43"], devices);
        Assert.Equal(6, mounts.Count(m => m.DeviceId == "0:42"));
        Assert.Equal(2, mounts.Count(m => m.DeviceId == "0:43"));
    }

    /// <summary>
    /// A library really called "Famille priver". The kernel escapes the space because the format is
    /// space separated; taking the field literally means never matching the path it belongs to.
    /// </summary>
    [Fact]
    public void APathWithASpace_IsUnescaped()
    {
        var mounts = MountTable.Parse(RealMountInfo);

        var priver = Assert.Single(mounts, m => m.MountPoint == "/data/Priver");
        Assert.Equal("RAID-Z1", priver.Source);

        Assert.Equal("/Media/Famille priver", MountTable.Unescape("/Media/Famille\\040priver"));
    }

    [Theory]
    [InlineData("/mnt/a b", "/mnt/a\\040b")]
    [InlineData("/mnt/a\tb", "/mnt/a\\011b")]
    [InlineData("/mnt/a\nb", "/mnt/a\\012b")]
    [InlineData("/mnt/a\\b", "/mnt/a\\134b")]
    [InlineData("/mnt/plain", "/mnt/plain")]
    public void OctalEscapes_AreDecoded(string expected, string raw)
    {
        Assert.Equal(expected, MountTable.Unescape(raw));
    }

    /// <summary>
    /// The optional-fields section before the separator is unbounded, so the filesystem type has to
    /// be found relative to the "-" rather than counted from the start. Docker always uses shared
    /// subtrees, so those tags are always present in practice.
    /// </summary>
    [Fact]
    public void OptionalFields_DoNotShiftTheFilesystemType()
    {
        var withTags = new[]
        {
            "36 35 98:0 / /mnt rw,noatime shared:1 master:2 propagate_from:3 - ext4 /dev/sda1 rw",
        };

        var mount = Assert.Single(MountTable.Parse(withTags));

        Assert.Equal("ext4", mount.FileSystemType);
        Assert.Equal("/dev/sda1", mount.Source);
        Assert.Equal("98:0", mount.DeviceId);
    }

    /// <summary>
    /// tmpfs, overlay and the rest are not storage. Left in, they would sit in a capacity report
    /// beside the real pools with sizes that mean nothing.
    /// </summary>
    [Fact]
    public void PseudoFilesystems_AreLeftOut()
    {
        var mixed = new[]
        {
            "1 0 0:1 / /proc rw - proc proc rw",
            "2 0 0:2 / /sys rw - sysfs sysfs rw",
            "3 0 0:3 / /transcode rw - tmpfs tmpfs rw",
            "4 0 0:4 / / rw - overlay overlay rw",
            "5 0 0:42 /Media /data rw - zfs RAID-Z1 rw",
        };

        var mount = Assert.Single(MountTable.Parse(mixed));

        Assert.Equal("zfs", mount.FileSystemType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1 2 3 4")]
    [InlineData("1 2 0:1 / /mnt rw -")]
    public void MalformedLines_AreSkippedRatherThanThrowing(string line)
    {
        Assert.Empty(MountTable.Parse([line]));
    }

    /// <summary>
    /// End to end on the real table: the six RAID-Z1 library paths and the two RAIDZ28 ones must
    /// come back as two devices, named the way the admin named them.
    /// </summary>
    [Fact]
    public void TheRealLibraryPaths_ResolveToTwoNamedPools()
    {
        var mounts = MountTable.Parse(RealMountInfo);
        var libraries = new[]
        {
            "/data/Films", "/data/Séries", "/data/Musiques",
            "/data/downloads", "/data/Priver", "/data/Livre",
            "/data/Séries28", "/data/Musiques28",
        };

        var resolved = libraries
            .Select(p => DriveProbe.LongestMatch(mounts, p, m => m.MountPoint))
            .ToList();

        Assert.All(resolved, m => Assert.NotNull(m));
        Assert.Equal(["0:42", "0:43"], resolved.Select(m => m!.DeviceId).Distinct().Order());
        Assert.Equal(["RAID-Z1", "RAIDZ28"], resolved.Select(m => m!.Source).Distinct().Order());
    }

    /// <summary>A sibling sharing a prefix is not inside the mount point.</summary>
    [Fact]
    public void APathSharingAPrefix_DoesNotMatch()
    {
        var mounts = MountTable.Parse(["5 0 0:42 /Media /data/Films rw - zfs RAID-Z1 rw"]);

        Assert.Null(DriveProbe.LongestMatch(mounts, "/data/Films2", m => m.MountPoint));
        Assert.NotNull(DriveProbe.LongestMatch(mounts, "/data/Films/sub", m => m.MountPoint));
    }

    [Fact]
    public void WithNoMountTable_TheProbeStillAnswers()
    {
        // Windows, or a Linux without /proc: falls back to matching drives, which is correct there.
        var probe = new DriveProbe(NullLogger<DriveProbe>.Instance);

        var devices = probe.Probe([Path.GetTempPath()]);

        Assert.NotEmpty(devices);
    }
}
