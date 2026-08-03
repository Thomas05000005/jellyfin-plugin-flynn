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

/// <summary>The real probe, backed by <see cref="DriveInfo"/>.</summary>
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

        // Keyed by the device's root, which is what makes several libraries on one disk collapse
        // into a single entry. Without it a pool holding films, series and music would report its
        // free space three times, and any forecast built on that would be three times too
        // optimistic.
        var byDevice = new Dictionary<string, DeviceSnapshot>(StringComparer.Ordinal);

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                var drive = new DriveInfo(path);
                if (!drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                if (byDevice.ContainsKey(root))
                {
                    continue;
                }

                byDevice[root] = new DeviceSnapshot(root, root, drive.TotalSize, drive.AvailableFreeSpace);
            }
#pragma warning disable CA1031 // One unreadable path must not lose the whole sweep.
            catch (Exception ex)
            {
                // A disconnected network share or a permissions problem on one library. Everything
                // else still gets measured.
                _logger.LogWarning(ex, "Could not read device information for a library path.");
            }
#pragma warning restore CA1031
        }

        return [.. byDevice.Values];
    }
}
