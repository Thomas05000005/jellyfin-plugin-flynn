namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// Flynn's schema, as an ordered list of steps.
/// <para>
/// Append only. A migration that has shipped has already run on someone's database and cannot be
/// edited, renumbered or removed — it can only be followed by another one. See
/// <see cref="Migration"/> for what a step may and may not contain.
/// </para>
/// </summary>
public static class Migrations
{
    /// <summary>Gets every migration, in order.</summary>
    public static IReadOnlyList<Migration> All { get; } =
    [
        new Migration(
            1,
            "storage snapshots",
            """
            CREATE TABLE storage_library_snapshot (
                taken_at     TEXT    NOT NULL,
                library_id   TEXT    NOT NULL,
                library_name TEXT    NOT NULL,
                item_count   INTEGER NOT NULL,
                bytes        INTEGER NOT NULL,
                PRIMARY KEY (taken_at, library_id)
            );

            CREATE TABLE storage_device_snapshot (
                taken_at    TEXT    NOT NULL,
                device_id   TEXT    NOT NULL,
                mount_path  TEXT    NOT NULL,
                total_bytes INTEGER NOT NULL,
                free_bytes  INTEGER NOT NULL,
                PRIMARY KEY (taken_at, device_id)
            );

            CREATE INDEX ix_storage_library_snapshot_library
                ON storage_library_snapshot (library_id, taken_at);

            CREATE INDEX ix_storage_device_snapshot_device
                ON storage_device_snapshot (device_id, taken_at);
            """),
    ];
}
