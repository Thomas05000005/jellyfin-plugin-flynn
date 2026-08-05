using System.Globalization;

namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>One line of the kernel's mount table.</summary>
/// <param name="DeviceId">
/// The filesystem's <c>major:minor</c>. This is the identity that matters: every mount of the same
/// filesystem carries the same pair, however many places it is bound to.
/// </param>
/// <param name="MountPoint">Where it is mounted, as this process sees it.</param>
/// <param name="FileSystemType">
/// <c>zfs</c>, <c>ext4</c>, <c>overlay</c>... Used to skip the pseudo-filesystems that would
/// otherwise show up as devices with meaningless sizes.
/// </param>
/// <param name="Source">
/// What was mounted: a ZFS dataset name, a block device path. Far better to show an admin than a
/// container-internal mount path, since "RAID-Z1" is what they called it and "/data/Films" is
/// something Docker invented.
/// </param>
public sealed record MountEntry(string DeviceId, string MountPoint, string FileSystemType, string Source);

/// <summary>
/// Reads the kernel's view of what is mounted.
/// <para>
/// <see cref="DriveInfo"/> alone cannot answer the question that matters inside a container. Bind
/// mounting one filesystem at six paths produces six drives as far as .NET is concerned, each
/// reporting the same free space, and summing them tells you six times more headroom than exists.
/// The kernel knows better, and says so in <c>/proc/self/mountinfo</c>.
/// </para>
/// </summary>
public static class MountTable
{
    /// <summary>Where the kernel publishes this process's mounts.</summary>
    internal const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>
    /// Filesystems that are not storage. Counting them would put tmpfs and overlay volumes in a
    /// capacity report next to the actual disks.
    /// </summary>
    private static readonly HashSet<string> Pseudo = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devpts", "tmpfs", "devtmpfs", "cgroup", "cgroup2", "mqueue",
        "securityfs", "debugfs", "tracefs", "pstore", "bpf", "configfs", "fusectl",
        "hugetlbfs", "binfmt_misc", "autofs", "ramfs", "nsfs", "overlay", "squashfs",
    };

    /// <summary>Gets a value indicating whether this platform publishes a mount table.</summary>
    public static bool IsAvailable => OperatingSystem.IsLinux() && File.Exists(MountInfoPath);

    /// <summary>Reads the mount table, keeping only real filesystems.</summary>
    /// <returns>One entry per mount, or an empty list where the table is unavailable.</returns>
    public static IReadOnlyList<MountEntry> Read()
    {
        if (!IsAvailable)
        {
            return [];
        }

        try
        {
            return Parse(File.ReadAllLines(MountInfoPath));
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses mountinfo lines.
    /// <para>
    /// Format, from the kernel's <c>filesystems/proc.rst</c>:
    /// <c>36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue</c>.
    /// The fields before the <c>-</c> separator are of variable length because the optional tags
    /// are unbounded, so everything after the separator has to be found by locating the separator
    /// rather than by counting from the start. Getting that wrong reads the filesystem type out of
    /// a mount option on any system that uses shared subtrees, which Docker always does.
    /// </para>
    /// </summary>
    /// <param name="lines">Raw lines.</param>
    /// <returns>The real filesystems among them.</returns>
    internal static IReadOnlyList<MountEntry> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<MountEntry>();

        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");

            // Need the five fixed fields before the separator, and type plus source after it.
            if (separator < 5 || separator + 2 >= fields.Length)
            {
                continue;
            }

            var type = fields[separator + 1];
            if (Pseudo.Contains(type))
            {
                continue;
            }

            entries.Add(new MountEntry(
                DeviceId: fields[2],
                MountPoint: Unescape(fields[4]),
                FileSystemType: type,
                Source: Unescape(fields[separator + 2])));
        }

        return entries;
    }

    /// <summary>
    /// Undoes the octal escaping the kernel applies to paths.
    /// <para>
    /// Space, tab, newline and backslash are written as <c>\040</c>, <c>\011</c>, <c>\012</c> and
    /// <c>\134</c>, because the format is space separated. A real library called
    /// <c>Famille priver</c> arrives as <c>Famille\040priver</c>, and treating that literally means
    /// never matching the path it belongs to.
    /// </para>
    /// </summary>
    /// <param name="value">A path as the kernel wrote it.</param>
    /// <returns>The path as it really is.</returns>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length && TryOctal(value.AsSpan(i + 1, 3), out var code))
            {
                result.Append((char)code);
                i += 3;
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Reads three octal digits.
    /// <para>
    /// Octal, not decimal, and that distinction was a real bug. Validating the digits as a decimal
    /// number and then converting them as octal accepts the escaped space by luck, since forty is
    /// under a hundred and twenty-eight, and silently rejects the escaped backslash along with
    /// everything above octal 177. The rejected sequence is then copied through literally, so a
    /// path quietly stops matching itself.
    /// </para>
    /// </summary>
    /// <param name="digits">Exactly three characters.</param>
    /// <param name="value">The decoded character.</param>
    /// <returns>True when the three characters were valid octal.</returns>
    private static bool TryOctal(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        foreach (var digit in digits)
        {
            if (digit is < '0' or > '7')
            {
                return false;
            }

            value = (value * 8) + (digit - '0');
        }

        return value is > 0 and < 256;
    }
}
