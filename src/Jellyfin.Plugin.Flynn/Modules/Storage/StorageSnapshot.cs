namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>What one library held at one moment.</summary>
/// <param name="LibraryId">The library's Jellyfin id.</param>
/// <param name="LibraryName">Its name at the time of the snapshot.</param>
/// <param name="ItemCount">How many playable items it held.</param>
/// <param name="Bytes">How much they took up.</param>
public sealed record LibrarySnapshot(string LibraryId, string LibraryName, long ItemCount, long Bytes);

/// <summary>What one storage device looked like at one moment.</summary>
/// <param name="DeviceId">
/// Stable identity for the device. Two libraries on the same disk share one, which is what stops
/// the same free space being counted twice.
/// </param>
/// <param name="MountPath">Where it is mounted.</param>
/// <param name="TotalBytes">Capacity.</param>
/// <param name="FreeBytes">What is left.</param>
public sealed record DeviceSnapshot(string DeviceId, string MountPath, long TotalBytes, long FreeBytes)
{
    /// <summary>Gets how much of the device is in use, from 0 to 1.</summary>
    public double UsedFraction => TotalBytes <= 0 ? 0 : 1d - ((double)FreeBytes / TotalBytes);
}

/// <summary>Everything one storage sweep found.</summary>
/// <param name="TakenAt">When the sweep ran.</param>
/// <param name="Libraries">One entry per library.</param>
/// <param name="Devices">One entry per distinct device.</param>
public sealed record StorageSnapshot(
    DateTimeOffset TakenAt,
    IReadOnlyList<LibrarySnapshot> Libraries,
    IReadOnlyList<DeviceSnapshot> Devices)
{
    /// <summary>Gets the total bytes held across every library.</summary>
    public long TotalLibraryBytes => Libraries.Sum(l => l.Bytes);

    /// <summary>Gets the device with the least headroom, or null when none were readable.</summary>
    public DeviceSnapshot? TightestDevice =>
        Devices.Count == 0 ? null : Devices.MaxBy(d => d.UsedFraction);
}
