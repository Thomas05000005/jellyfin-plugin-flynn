using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>
/// Reads capacity and free space for the devices holding a set of paths.
/// <para>
/// An interface because the real implementation talks to the filesystem, and a test that has to
/// mount a disk to check deduplication is a test nobody runs.
/// </para>
/// </summary>
public interface IDriveProbe
{
    /// <summary>Inspects the devices behind a set of library paths.</summary>
    /// <param name="paths">Library locations. May contain duplicates and unreadable entries.</param>
    /// <returns>One entry per distinct filesystem.</returns>
    IReadOnlyList<DeviceSnapshot> Probe(IEnumerable<string> paths);
}

/// <summary>
/// The real probe.
/// <para>
/// On Linux it works from the kernel's mount table, because inside a container that is the only
/// place the truth is written down. A media server bind mounting one pool at six paths gives
/// <see cref="DriveInfo"/> six drives, each reporting the same free space; summing them promises
/// six times the headroom that exists, and a capacity forecast built on that is not merely
/// imprecise, it is confidently wrong. The kernel labels every mount of a filesystem with the same
/// <c>major:minor</c>, which settles it.
/// </para>
/// <para>
/// Elsewhere it falls back to matching the longest mount point, which is right on Windows where a
/// path belongs to exactly one drive letter.
/// </para>
/// </summary>
public sealed class DriveProbe : IDriveProbe
{
    private readonly ILogger<DriveProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="DriveProbe"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public DriveProbe(ILogger<DriveProbe> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceSnapshot> Probe(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var wanted = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return MountTable.IsAvailable
            ? ProbeByDevice(wanted, MountTable.Read())
            : ProbeByMountPoint(wanted);
    }

    /// <summary>
    /// Groups paths by the filesystem that actually holds them, using the kernel's device id.
    /// </summary>
    /// <param name="paths">Library locations.</param>
    /// <param name="mounts">The mount table.</param>
    /// <returns>One entry per distinct filesystem.</returns>
    internal IReadOnlyList<DeviceSnapshot> ProbeByDevice(
        IReadOnlyList<string> paths,
        IReadOnlyList<MountEntry> mounts)
    {
        if (mounts.Count == 0)
        {
            return ProbeByMountPoint(paths);
        }

        var byDevice = new Dictionary<string, DeviceSnapshot>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var mount = LongestMatch(mounts, path, m => m.MountPoint);
            if (mount is null)
            {
                _logger.LogWarning("No mounted filesystem appears to hold a library path; skipping it.");
                continue;
            }

            // Keyed by what actually shares the free space, which is the pool on ZFS and the
            // device id everywhere else. Keying on the mount point keeps six bind mounts of one
            // pool as six devices; keying on the device id alone holds until someone splits a
            // library into its own dataset, since ZFS gives every dataset its own superblock.
            var key = FilesystemIdentity.Of(mount);
            if (byDevice.ContainsKey(key))
            {
                continue;
            }

            try
            {
                var info = new DriveInfo(mount.MountPoint);
                if (!info.IsReady)
                {
                    continue;
                }

                // The pool name is what the admin called it -- "RAID-Z1" -- while the mount point
                // is whatever the container was told to call it. Reporting the pool as
                // "/data/Films" reads as though that one folder were full.
                byDevice[key] = new DeviceSnapshot(
                    key,
                    FilesystemIdentity.DisplayNameOf(mount),
                    info.TotalSize,
                    info.AvailableFreeSpace,
                    mount.FileSystemType);
            }
#pragma warning disable CA1031 // One unreadable device must not lose the whole sweep.
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Could not read capacity for {Source}.", mount.Source);
            }
#pragma warning restore CA1031
        }

        return [.. byDevice.Values];
    }

    /// <summary>
    /// The fallback: group by the longest matching mount point.
    /// <para>
    /// Correct where a path belongs to exactly one volume. It cannot see that two mounts share a
    /// filesystem, which is why it is not used on Linux.
    /// </para>
    /// </summary>
    /// <param name="paths">Library locations.</param>
    /// <returns>One entry per distinct mount point.</returns>
    private IReadOnlyList<DeviceSnapshot> ProbeByMountPoint(IReadOnlyList<string> paths)
    {
        List<DriveInfo> drives;
        try
        {
            drives = [.. DriveInfo.GetDrives().Where(d => d.IsReady)];
        }
#pragma warning disable CA1031 // Losing the device panel beats losing the sweep.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list drives; no device readings this sweep.");
            return [];
        }
#pragma warning restore CA1031

        var byRoot = new Dictionary<string, DeviceSnapshot>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var drive = LongestMatch(drives, path, d => d.RootDirectory.FullName);
            if (drive is null)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;
            if (!byRoot.ContainsKey(root))
            {
                byRoot[root] = new DeviceSnapshot(
                    root, root, drive.TotalSize, drive.AvailableFreeSpace, drive.DriveFormat);
            }
        }

        return [.. byRoot.Values];
    }

    /// <summary>
    /// Finds the candidate whose path is the longest one containing <paramref name="path"/>.
    /// <para>
    /// Longest wins because <c>/data/Films</c> sits under both <c>/</c> and <c>/data/Films</c>, and
    /// the deeper one is the filesystem actually holding it.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Candidate type.</typeparam>
    /// <param name="candidates">Mounts or drives.</param>
    /// <param name="path">The path to place.</param>
    /// <param name="rootOf">How to read a candidate's path.</param>
    /// <returns>The best match, or null.</returns>
    internal static T? LongestMatch<T>(IEnumerable<T> candidates, string path, Func<T, string> rootOf)
        where T : class
    {
        // Deliberately not normalised through Path.GetFullPath. Mount table paths are already
        // absolute and POSIX; normalising them on a Windows machine rewrites "/data/Films" as a
        // path on the current drive, which then matches nothing. Production is Linux and the tests
        // are not, and a matcher that only works where it happens to run is worse than none.
        return candidates
            .Where(c => IsUnder(path, rootOf(c)))
            .MaxBy(c => rootOf(c).Length);
    }

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (root.Length == 0 || !path.StartsWith(root, comparison))
        {
            return false;
        }

        // "/data/Films2" must not match the mount point "/data/Films". Either the root already ends
        // with a separator, as "/" and "C:\" do, or the next character has to be one.
        return root[^1] == '/'
            || root[^1] == Path.DirectorySeparatorChar
            || path.Length == root.Length
            || path[root.Length] == '/'
            || path[root.Length] == Path.DirectorySeparatorChar;
    }
}
