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

        new Migration(
            2,
            "issue registry",
            """
            CREATE TABLE issue (
                fingerprint   TEXT    NOT NULL PRIMARY KEY,
                module_id     TEXT    NOT NULL,
                kind          TEXT    NOT NULL,
                subject       TEXT    NOT NULL,
                severity      INTEGER NOT NULL,
                title_key     TEXT    NOT NULL,
                title_args    TEXT    NOT NULL,
                detail_key    TEXT        NULL,
                detail_args   TEXT        NULL,
                state         INTEGER NOT NULL,
                first_seen    TEXT    NOT NULL,
                last_seen     TEXT    NOT NULL,
                snoozed_until TEXT        NULL
            );

            CREATE INDEX ix_issue_scope ON issue (module_id, kind, state);

            CREATE INDEX ix_issue_state ON issue (state, severity);
            """),

        new Migration(
            3,
            "mutation log",
            """
            CREATE TABLE mutation_log (
                id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                module_id   TEXT    NOT NULL,
                kind        TEXT    NOT NULL,
                write_level INTEGER NOT NULL,
                step_count  INTEGER NOT NULL,
                undo_manifest TEXT  NOT NULL,
                applied_at  TEXT        NULL,
                undone_at   TEXT        NULL,
                prepared_at TEXT    NOT NULL
            );

            CREATE INDEX ix_mutation_log_recent ON mutation_log (prepared_at DESC);
            """),

        new Migration(
            4,
            "device filesystem type",
            """
            ALTER TABLE storage_device_snapshot ADD COLUMN fs_type TEXT NOT NULL DEFAULT '';
            """),
    ];
}
