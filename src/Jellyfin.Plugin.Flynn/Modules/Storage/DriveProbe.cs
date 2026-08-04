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
    /// <returns>One entry per distinct device.</returns>
    IReadOnlyList<DeviceSnapshot> Probe(IEnumerable<string> paths);
}

/// <summary>The real probe, backed by the mounted filesystems.</summary>
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

        var mounts = ReadMounts();
        if (mounts.Count == 0)
        {
            return [];
        }

        // Keyed by mount point, which is what makes several libraries on one disk collapse into a
        // single entry. Without it a pool holding films, series and music reports its free space
        // three times, and any forecast built on that is three times too optimistic.
        var byMount = new Dictionary<string, DeviceSnapshot>(PathComparison);

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var mount = MountHolding(mounts, path);
            if (mount is null)
            {
                _logger.LogWarning("No mounted filesystem appears to hold a library path; skipping it.");
                continue;
            }

            var root = mount.RootDirectory.FullName;
            if (byMount.ContainsKey(root))
            {
                continue;
            }

            try
            {
                byMount[root] = new DeviceSnapshot(root, root, mount.TotalSize, mount.AvailableFreeSpace);
            }
#pragma warning disable CA1031 // One unreadable device must not lose the whole sweep.
            catch (Exception ex)
            {
                // A share that dropped between the listing and the read. Everything else still
                // gets measured.
                _logger.LogWarning(ex, "Could not read capacity for mount point {Mount}.", root);
            }
#pragma warning restore CA1031
        }

        return [.. byMount.Values];
    }

    /// <summary>
    /// Finds the mounted filesystem that actually holds <paramref name="path"/>.
    /// <para>
    /// The longest matching mount point wins, and that is the whole point. On Linux
    /// <c>/mnt/pool/films</c> is under both <c>/</c> and <c>/mnt/pool</c>; picking the longer one
    /// reports the pool rather than the root filesystem, and makes two libraries on that pool
    /// resolve to the same device.
    /// </para>
    /// <para>
    /// Constructing a <see cref="DriveInfo"/> from the library path directly does not do this. On
    /// Windows it happens to work because only the drive letter is read, which is why this was
    /// invisible until a Linux CI run. On Unix the path becomes the mount name itself, so every
    /// library would look like its own device.
    /// </para>
    /// </summary>
    /// <param name="mounts">Every ready filesystem.</param>
    /// <param name="path">A library location.</param>
    /// <returns>The filesystem holding it, or null when none matches.</returns>
    internal static DriveInfo? MountHolding(IReadOnlyList<DriveInfo> mounts, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return mounts
            .Where(m => IsUnder(full, m.RootDirectory.FullName))
            .MaxBy(m => m.RootDirectory.FullName.Length);
    }

    private static StringComparer PathComparison =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!path.StartsWith(root, comparison))
        {
            return false;
        }

        // "/mnt/poolroom" must not match the mount point "/mnt/pool". Either the root already ends
        // with a separator, as "/" and "C:\" do, or the next character has to be one.
        return root.EndsWith(Path.DirectorySeparatorChar)
            || path.Length == root.Length
            || path[root.Length] == Path.DirectorySeparatorChar;
    }

    private IReadOnlyList<DriveInfo> ReadMounts()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Where(d => d.IsReady)];
        }
#pragma warning disable CA1031 // Losing the device panel beats losing the sweep.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list mounted filesystems; no device readings this sweep.");
            return [];
        }
#pragma warning restore CA1031
    }
}
