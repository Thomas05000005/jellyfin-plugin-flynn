using System.Globalization;
using Jellyfin.Plugin.Flynn.Core.Data;

namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>
/// Reads and writes storage snapshots.
/// <para>
/// Snapshots are append-only. Nothing here updates or deletes a past reading, because the whole
/// value of this table is the shape of the curve over months: a corrected past point is a
/// forecast built on a history that never happened.
/// </para>
/// </summary>
public sealed class StorageRepository
{
    private readonly FlynnDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="StorageRepository"/> class.</summary>
    /// <param name="database">Flynn's database.</param>
    public StorageRepository(FlynnDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>Stores one sweep.</summary>
    /// <param name="snapshot">What the sweep found.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the readings are stored.</returns>
    public async Task SaveAsync(StorageSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var takenAt = snapshot.TakenAt.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var library in snapshot.Libraries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO storage_library_snapshot "
                + "(taken_at, library_id, library_name, item_count, bytes) "
                + "VALUES ($at, $id, $name, $count, $bytes)";
            command.Parameters.AddWithValue("$at", takenAt);
            command.Parameters.AddWithValue("$id", library.LibraryId);
            command.Parameters.AddWithValue("$name", library.LibraryName);
            command.Parameters.AddWithValue("$count", library.ItemCount);
            command.Parameters.AddWithValue("$bytes", library.Bytes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var device in snapshot.Devices)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO storage_device_snapshot "
                + "(taken_at, device_id, mount_path, total_bytes, free_bytes) "
                + "VALUES ($at, $id, $path, $total, $free)";
            command.Parameters.AddWithValue("$at", takenAt);
            command.Parameters.AddWithValue("$id", device.DeviceId);
            command.Parameters.AddWithValue("$path", device.MountPath);
            command.Parameters.AddWithValue("$total", device.TotalBytes);
            command.Parameters.AddWithValue("$free", device.FreeBytes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the most recent sweep, or null when none has run yet.
    /// <para>
    /// This is what the dashboard card reads, so it is two indexed lookups and nothing else. The
    /// expensive work belongs to the scheduled task.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest snapshot.</returns>
    public async Task<StorageSnapshot?> GetLatestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string? takenAt;
        await using (var latest = connection.CreateCommand())
        {
            latest.CommandText =
                "SELECT MAX(taken_at) FROM ("
                + "SELECT taken_at FROM storage_library_snapshot "
                + "UNION ALL SELECT taken_at FROM storage_device_snapshot)";
            takenAt = await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (takenAt is null)
        {
            return null;
        }

        var libraries = new List<LibrarySnapshot>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT library_id, library_name, item_count, bytes FROM storage_library_snapshot "
                + "WHERE taken_at = $at ORDER BY bytes DESC";
            read.Parameters.AddWithValue("$at", takenAt);
            await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                libraries.Add(new LibrarySnapshot(
                    rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt64(3)));
            }
        }

        var devices = new List<DeviceSnapshot>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT device_id, mount_path, total_bytes, free_bytes FROM storage_device_snapshot "
                + "WHERE taken_at = $at";
            read.Parameters.AddWithValue("$at", takenAt);
            await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                devices.Add(new DeviceSnapshot(
                    rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt64(3)));
            }
        }

        return new StorageSnapshot(
            DateTimeOffset.Parse(takenAt, CultureInfo.InvariantCulture),
            libraries,
            devices);
    }
}
