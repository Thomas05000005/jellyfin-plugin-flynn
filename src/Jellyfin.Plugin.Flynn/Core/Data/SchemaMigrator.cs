using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// Brings a database up to the current schema, whatever state it starts in.
/// <para>
/// The case that matters is not the empty database — that one works by accident. It is the
/// database that already holds a user's history and is two versions behind, because that is the
/// only one where getting it wrong is expensive.
/// </para>
/// </summary>
public sealed class SchemaMigrator
{
    /// <summary>
    /// Records which steps have run. Created before anything else and never migrated itself, so
    /// its shape is fixed for good.
    /// </summary>
    internal const string VersionTable = "flynn_schema_version";

    private readonly FlynnDatabase _database;
    private readonly ILogger<SchemaMigrator> _logger;

    /// <summary>Initializes a new instance of the <see cref="SchemaMigrator"/> class.</summary>
    /// <param name="database">The database to migrate.</param>
    /// <param name="logger">Logger.</param>
    public SchemaMigrator(FlynnDatabase database, ILogger<SchemaMigrator> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Applies every migration the database has not seen, in order.
    /// <para>
    /// Safe to call on every start: already-applied steps are skipped, so this is a no-op once the
    /// database is current. Each step commits on its own, so a failure half-way leaves the earlier
    /// steps applied and the failing one fully rolled back — the next start resumes from there
    /// rather than replaying what already worked.
    /// </para>
    /// </summary>
    /// <param name="migrations">The ordered migration list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The schema version the database is on once this returns.</returns>
    public async Task<int> MigrateAsync(
        IReadOnlyList<Migration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        AssertWellFormed(migrations);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                $"CREATE TABLE IF NOT EXISTS {VersionTable} ("
                + "version INTEGER NOT NULL PRIMARY KEY,"
                + "name TEXT NOT NULL,"
                + "applied_at TEXT NOT NULL)";
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var pending = migrations.Where(m => m.Version > current).OrderBy(m => m.Version).ToList();

        if (pending.Count == 0)
        {
            return current;
        }

        _logger.LogInformation(
            "Flynn database is at schema version {Current}; applying {Count} migration(s).",
            current,
            pending.Count);

        foreach (var migration in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var step = connection.CreateCommand())
                {
                    step.Transaction = (SqliteTransaction)transaction;
                    step.CommandText = migration.Sql;
                    await step.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = (SqliteTransaction)transaction;
                    record.CommandText =
                        $"INSERT INTO {VersionTable} (version, name, applied_at) VALUES ($v, $n, $t)";
                    record.Parameters.AddWithValue("$v", migration.Version);
                    record.Parameters.AddWithValue("$n", migration.Name);
                    record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                current = migration.Version;
                _logger.LogInformation(
                    "Applied Flynn schema migration {Version}: {Name}.", migration.Version, migration.Name);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogError(
                    ex,
                    "Flynn schema migration {Version} ({Name}) failed and was rolled back. "
                    + "The database stays at version {Current}.",
                    migration.Version,
                    migration.Name,
                    current);
                throw;
            }
        }

        return current;
    }

    /// <summary>Reads the highest applied version, or 0 for a database that has never been migrated.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current schema version.</returns>
    internal static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM {VersionTable}";
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rejects a migration list that could not behave correctly, before any of it runs.
    /// <para>
    /// Both rules exist because the failure they prevent is invisible on a fresh database and only
    /// shows up on someone's upgrade.
    /// </para>
    /// </summary>
    /// <param name="migrations">The list to check.</param>
    private static void AssertWellFormed(IReadOnlyList<Migration> migrations)
    {
        var expected = 1;
        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            if (migration.Version != expected)
            {
                throw new InvalidOperationException(
                    $"Flynn migrations must be numbered 1, 2, 3... with no gaps or duplicates; "
                    + $"expected version {expected} but found {migration.Version} ({migration.Name}).");
            }

            expected++;
        }
    }
}
