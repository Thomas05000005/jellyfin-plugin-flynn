using Jellyfin.Plugin.Flynn.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The case worth testing is the database that already holds someone's history and is behind by a
/// version. An empty database migrates correctly almost by accident, so a suite that only ever
/// starts from empty proves very little.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SchemaMigratorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

    private static readonly Migration StepOne = new(
        1,
        "first table",
        "CREATE TABLE thing (id INTEGER PRIMARY KEY, label TEXT NOT NULL);");

    /// <summary>
    /// The realistic second step: it adds a column to a table that already holds rows, then indexes
    /// it. On a fresh database the ordering is forgiving; on a populated one it is not.
    /// </summary>
    private static readonly Migration StepTwo = new(
        2,
        "add a column and index it",
        """
        ALTER TABLE thing ADD COLUMN note TEXT;
        CREATE INDEX ix_thing_label ON thing (label);
        """);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFreshDatabase_ReachesTheLatestVersion()
    {
        var migrator = NewMigrator(out _);

        var version = await migrator.MigrateAsync([StepOne, StepTwo], CancellationToken.None);

        Assert.Equal(2, version);
    }

    /// <summary>
    /// The one that matters: migrate, put real rows in, then upgrade. The rows must still be there
    /// and the new column must exist alongside them.
    /// </summary>
    [Fact]
    public async Task APreExistingDatabaseWithData_UpgradesWithoutLosingIt()
    {
        var migrator = NewMigrator(out var database);

        Assert.Equal(1, await migrator.MigrateAsync([StepOne], CancellationToken.None));

        await using (var seed = await database.OpenAsync(CancellationToken.None))
        await using (var insert = seed.CreateCommand())
        {
            insert.CommandText = "INSERT INTO thing (id, label) VALUES (1, 'kept'), (2, 'also kept')";
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Equal(2, await migrator.MigrateAsync([StepOne, StepTwo], CancellationToken.None));

        await using var check = await database.OpenAsync(CancellationToken.None);
        await using var read = check.CreateCommand();
        read.CommandText = "SELECT id, label, note FROM thing ORDER BY id";
        await using var rows = await read.ExecuteReaderAsync();

        Assert.True(await rows.ReadAsync());
        Assert.Equal("kept", rows.GetString(1));
        Assert.True(rows.IsDBNull(2));
        Assert.True(await rows.ReadAsync());
        Assert.Equal("also kept", rows.GetString(1));
        Assert.False(await rows.ReadAsync());
    }

    [Fact]
    public async Task MigratingAnUpToDateDatabase_DoesNothing()
    {
        var migrator = NewMigrator(out _);
        await migrator.MigrateAsync([StepOne, StepTwo], CancellationToken.None);

        // Re-running must not re-apply StepOne, which would throw "table thing already exists".
        var version = await migrator.MigrateAsync([StepOne, StepTwo], CancellationToken.None);

        Assert.Equal(2, version);
    }

    [Fact]
    public async Task AFailingStep_RollsBackAndLeavesTheVersionWhereItWas()
    {
        var migrator = NewMigrator(out var database);
        var broken = new Migration(
            2,
            "creates a table then fails",
            """
            CREATE TABLE half_done (id INTEGER PRIMARY KEY);
            INSERT INTO does_not_exist (id) VALUES (1);
            """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            migrator.MigrateAsync([StepOne, broken], CancellationToken.None));

        await using var connection = await database.OpenAsync(CancellationToken.None);

        // Version stayed at 1, so the next start retries step 2 instead of skipping it.
        Assert.Equal(1, await SchemaMigrator.ReadVersionAsync(connection, CancellationToken.None));

        // And the table the failing step created before throwing is gone with the transaction.
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='half_done'";
        Assert.Equal(0L, Assert.IsType<long>(await exists.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData(2)]   // does not start at 1
    [InlineData(1)]   // duplicate version
    public async Task AMisnumberedList_IsRejectedBeforeAnythingRuns(int secondVersion)
    {
        var migrator = NewMigrator(out var database);
        var list = secondVersion == 2
            ? new List<Migration> { new(2, "starts at two", "CREATE TABLE a (id INTEGER);") }
            : [StepOne, new Migration(1, "duplicate", "CREATE TABLE b (id INTEGER);")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migrator.MigrateAsync(list, CancellationToken.None));

        Assert.False(File.Exists(database.DatabasePath));
    }

    /// <summary>
    /// Guards the rule that cannot be enforced by types: a shipped migration must never remove or
    /// rename anything, because the database on the other end belongs to someone upgrading.
    /// </summary>
    [Fact]
    public void TheShippedMigrations_AreAdditiveOnly()
    {
        string[] destructive = ["DROP TABLE", "DROP COLUMN", "DROP INDEX", "RENAME TO", "RENAME COLUMN"];

        foreach (var migration in Migrations.All)
        {
            foreach (var statement in destructive)
            {
                Assert.DoesNotContain(
                    statement,
                    migration.Sql.ToUpperInvariant(),
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TheShippedMigrations_ApplyToAnEmptyDatabase()
    {
        var migrator = NewMigrator(out _);

        var version = await migrator.MigrateAsync(Migrations.All, CancellationToken.None);

        Assert.Equal(Migrations.All.Count, version);
    }

    private SchemaMigrator NewMigrator(out FlynnDatabase database)
    {
        database = new FlynnDatabase(Path.Combine(_directory, "flynn.db"));
        return new SchemaMigrator(database, NullLogger<SchemaMigrator>.Instance);
    }
}
