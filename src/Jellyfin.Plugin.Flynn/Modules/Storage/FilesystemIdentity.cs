namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>
/// Decides which mounts draw on the same free space.
/// <para>
/// The device id is the obvious answer and it is not enough. Two bind mounts of one filesystem do
/// share it, which covers the common Docker case of one dataset exposed at several paths. But ZFS
/// gives every dataset its own anonymous superblock, so two datasets of the SAME pool carry
/// different device ids while competing for exactly the same bytes. Grouping on the device id
/// alone therefore works until someone splits a library into its own dataset, and then quietly
/// starts double counting again.
/// </para>
/// <para>
/// Verified against the ZFS source: <c>zpl_super.c</c> keys superblock reuse on the objset
/// (<c>zpl_test_super</c>) and allocates through <c>set_anon_super_fc</c>, so one dataset means one
/// superblock means one device id. The dataset name comes from <c>.show_devname</c>, which is why
/// the mount source is trustworthy even for a bind mount or after a rename.
/// </para>
/// </summary>
internal static class FilesystemIdentity
{
    /// <summary>
    /// Returns the key under which a mount's free space should be pooled.
    /// </summary>
    /// <param name="mount">A mount table entry.</param>
    /// <returns>
    /// For ZFS, the pool: everything in the source before the first separator, since a ZFS dataset
    /// name is <c>pool[/component]...</c> and a component may not contain a separator. For anything
    /// else, the device id, which is exactly right for a filesystem that owns its own space.
    /// </returns>
    internal static string Of(MountEntry mount)
    {
        ArgumentNullException.ThrowIfNull(mount);

        if (!IsPooled(mount.FileSystemType))
        {
            return mount.DeviceId;
        }

        var pool = PoolOf(mount.Source);
        return pool.Length == 0 ? mount.DeviceId : $"{mount.FileSystemType}:{pool}";
    }

    /// <summary>Returns the display name for a mount: the pool if it has one, else the source.</summary>
    /// <param name="mount">A mount table entry.</param>
    /// <returns>What to show an admin. "RAID-Z1", not "/data/Films".</returns>
    internal static string DisplayNameOf(MountEntry mount)
    {
        ArgumentNullException.ThrowIfNull(mount);

        if (IsPooled(mount.FileSystemType))
        {
            var pool = PoolOf(mount.Source);
            if (pool.Length > 0)
            {
                return pool;
            }
        }

        return string.IsNullOrWhiteSpace(mount.Source) ? mount.MountPoint : mount.Source;
    }

    /// <summary>
    /// Whether this filesystem type shares one free-space budget across several mounts.
    /// <para>
    /// ZFS and Btrfs both carve subvolumes or datasets out of a single pool. Everything else here
    /// owns its space outright, so its device id already answers the question.
    /// </para>
    /// </summary>
    /// <param name="fileSystemType">As reported by the kernel.</param>
    /// <returns>True when several mounts of this type can draw on one budget.</returns>
    private static bool IsPooled(string fileSystemType) =>
        fileSystemType is "zfs" or "btrfs";

    /// <summary>
    /// Extracts the pool from a dataset name.
    /// <para>
    /// A snapshot is <c>pool/dataset@snap</c> and a bookmark <c>pool/dataset#mark</c>; both still
    /// start with the pool, so cutting at the first separator is enough.
    /// </para>
    /// </summary>
    /// <param name="source">The mount source.</param>
    /// <returns>The pool name, or empty when the source does not look like a dataset.</returns>
    private static string PoolOf(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith('/'))
        {
            // A path rather than a dataset name: a block device, or a bind mount whose source the
            // kernel could not resolve. Not something a pool name can be read out of.
            return string.Empty;
        }

        var cut = source.IndexOf('/', StringComparison.Ordinal);
        return cut < 0 ? source : source[..cut];
    }
}
