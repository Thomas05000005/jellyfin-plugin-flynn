using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// Opens connections to Flynn's own SQLite database.
/// <para>
/// Ours, and only ours. Nothing here ever touches Jellyfin's database: that one is EF Core owned
/// and its schema changes between server versions, so reading it directly would break on an
/// upgrade and writing to it could corrupt a library. Library facts come from
/// <c>ILibraryManager</c>; what we derive from them lives here.
/// </para>
/// <para>
/// The SQLite engine comes from the server. The plugin references Microsoft.Data.Sqlite with the
/// runtime and native assets excluded, so no binary ships with us and the load context resolves
/// the server's copy instead.
/// </para>
/// </summary>
public sealed class FlynnDatabase
{
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="FlynnDatabase"/> class.</summary>
    /// <param name="databasePath">
    /// Full path to the database file. Its directory is created if missing.
    /// </param>
    public FlynnDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooled connections keep the file handle warm; the daily tasks open and close often.
            Pooling = true,
        }.ToString();
    }

    /// <summary>Gets the full path to the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Opens a connection with the pragmas Flynn relies on.
    /// <para>
    /// WAL matters here specifically: the server writes to its own database on the same disk while
    /// our scheduled tasks run, and the default rollback journal would have a long-running read
    /// block a write. <c>busy_timeout</c> covers the rest, so a concurrent writer waits instead of
    /// failing outright with SQLITE_BUSY.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText =
                    "PRAGMA journal_mode = WAL;"
                    + "PRAGMA foreign_keys = ON;"
                    + "PRAGMA busy_timeout = 5000;";
                await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
