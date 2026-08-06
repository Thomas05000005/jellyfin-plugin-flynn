using System.Globalization;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Mutations;
using Jellyfin.Plugin.Flynn.Modules.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// What retention removes, and -- the half that actually matters -- what it must not.
/// <para>
/// A pruner that deletes too much does not announce itself. It degrades a feature quietly months
/// later, when the forecast has less history than it should and simply predicts less far ahead, or
/// when a dismissal the admin made stops being counted and the problem comes back looking new.
/// Most of the tests here exist to hold those two lines.
/// </para>
/// </summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-08-06T03:15:00Z", null));

    private FlynnDatabase _database = null!;
    private IssueRegistry _issues = null!;
    private Retention _retention = null!;

    public async Task InitializeAsync()
    {
        _database = new FlynnDatabase(Path.Combine(_directory, "flynn.db"));
        await new SchemaMigrator(_database, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(Migrations.All, CancellationToken.None);
        _issues = new IssueRegistry(_database, _clock);
        _retention = new Retention(_database, _clock, NullLogger<Retention>.Instance);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AResolvedIssue_IsDroppedOnceItIsOldEnough()
    {
        await Sweep("zfs:Films");
        await Sweep();

        _clock.Advance(Retention.ResolvedIssues + TimeSpan.FromDays(1));
        var outcome = await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.ResolvedIssues);
        Assert.Equal(0, await CountAsync("issue"));
    }

    [Fact]
    public async Task AResolvedIssue_IsKeptWhileItIsStillRecent()
    {
        await Sweep("zfs:Films");
        await Sweep();

        _clock.Advance(Retention.ResolvedIssues - TimeSpan.FromDays(1));
        await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("issue"));
    }

    /// <summary>
    /// A dismissal is a decision, and its count is the only thing keeping a permanent hide from
    /// being a blind spot. Ageing one out would erase the trace and let the problem come back
    /// looking new.
    /// </summary>
    [Fact]
    public async Task ADismissedIssue_IsNeverPrunedHoweverOld()
    {
        var issue = await Sweep("zfs:Films");
        await _issues.DismissAsync(issue!.Fingerprint, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(3650));
        var outcome = await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.ResolvedIssues);
        Assert.Equal(1, await CountAsync("issue"));
        var counts = await _issues.CountByStateAsync(CancellationToken.None);
        Assert.Equal(1, counts[IssueState.Dismissed]);
    }

    [Fact]
    public async Task AnOpenIssue_IsNeverPruned()
    {
        await Sweep("zfs:Films");

        _clock.Advance(TimeSpan.FromDays(3650));
        await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("issue"));
    }

    /// <summary>
    /// The forecast caps its horizon at twice the history it can see, so pruning readings would
    /// shorten how far ahead Flynn is willing to predict. A decade of them is about a megabyte.
    /// </summary>
    [Fact]
    public async Task StorageReadings_AreKeptForever()
    {
        var repository = new StorageRepository(_database);
        for (var day = 0; day < 40; day++)
        {
            await repository.SaveAsync(SnapshotOn(_clock.GetUtcNow().AddDays(-day)), CancellationToken.None);
        }

        _clock.Advance(TimeSpan.FromDays(3650));
        await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(40, await CountAsync("storage_device_snapshot"));
        Assert.Equal(40, await CountAsync("storage_library_snapshot"));
    }

    [Fact]
    public async Task AnAppliedMutation_IsDroppedOnceItsUndoWindowHasLongClosed()
    {
        await InsertMutationAsync(applied: true, preparedAt: _clock.GetUtcNow());

        _clock.Advance(Retention.MutationLog + TimeSpan.FromDays(1));
        var outcome = await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.MutationRecords);
        Assert.Equal(0, await CountAsync("mutation_log"));
    }

    /// <summary>
    /// The manifest is the only thing that makes a write reversible, so an undo the admin is still
    /// entitled to must not be deleted out from under them.
    /// </summary>
    [Fact]
    public async Task ARecentMutation_IsKeptSoItCanStillBeUndone()
    {
        await InsertMutationAsync(applied: true, preparedAt: _clock.GetUtcNow());

        _clock.Advance(Retention.MutationLog - TimeSpan.FromDays(1));
        await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("mutation_log"));
    }

    /// <summary>
    /// Prepared but never applied means something went wrong mid-write. That is precisely the row
    /// somebody will come looking for, so age does not entitle anyone to delete it.
    /// </summary>
    [Fact]
    public async Task AMutationThatWasPreparedButNeverApplied_IsKept()
    {
        await InsertMutationAsync(applied: false, preparedAt: _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromDays(3650));
        var outcome = await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.MutationRecords);
        Assert.Equal(1, await CountAsync("mutation_log"));
    }

    /// <summary>
    /// Closes the gap the tests above leave open.
    /// <para>
    /// Every other mutation test here writes its row by hand, which proves the retention query
    /// against the format the TEST chose. Timestamps live in the database as text, so a writer
    /// using a different shape would not fail loudly -- the comparison would simply stop matching,
    /// and a pruner that silently matches nothing looks exactly like a pruner that works. This one
    /// goes through the real kernel, so the format under test is the one production writes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARowWrittenByTheRealKernel_IsSeenByRetention()
    {
        var kernel = new MutationKernel(_database, _clock, NullLogger<MutationKernel>.Instance);
        await kernel.ApplyAsync(new TrivialMutation(), WriteLevel.Server, CancellationToken.None);

        Assert.Equal(1, await CountAsync("mutation_log"));

        _clock.Advance(Retention.MutationLog + TimeSpan.FromDays(1));
        var outcome = await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.MutationRecords);
        Assert.Equal(0, await CountAsync("mutation_log"));
    }

    [Fact]
    public async Task WithNothingToRemove_ItReportsNothingRemoved()
    {
        var outcome = await _retention.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.ResolvedIssues);
        Assert.Equal(0, outcome.MutationRecords);
    }

    private async Task<Issue?> Sweep(params string[] subjects)
    {
        var found = subjects.Select(subject => new Issue(
            "capacity",
            "capacity",
            subject,
            IssueSeverity.Warning,
            LocalizedText.Of(StringKeys.IssueCapacityTitle, subject, 12),
            null)).ToList();

        await _issues.ReconcileAsync("capacity", "capacity", found, CancellationToken.None);
        return found.FirstOrDefault();
    }

    private static StorageSnapshot SnapshotOn(DateTimeOffset when) =>
        new(
            when,
            [new LibrarySnapshot("lib", "Films", 10, 1024)],
            [new DeviceSnapshot("zfs:Films", "/data/Films", 2048, 1024, "zfs")]);

    private async Task InsertMutationAsync(bool applied, DateTimeOffset preparedAt)
    {
        await using var connection = await _database.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO mutation_log (module_id, kind, write_level, step_count, undo_manifest, "
            + "applied_at, undone_at, prepared_at) "
            + "VALUES ('music', 'merge', 1, 3, '[]', $applied, NULL, $prepared)";
        command.Parameters.AddWithValue(
            "$applied",
            applied ? preparedAt.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$prepared", preparedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>The smallest thing the kernel will accept, so the row it writes is a real one.</summary>
    private sealed class TrivialMutation : IMutation
    {
        public string ModuleId => "music";

        public string Kind => "merge";

        public WriteLevel Level => WriteLevel.Server;

        public Task<MutationPreview> PreviewAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MutationPreview(
                [new MutationStep(LocalizedText.Of("test.step"), "target")], []));

        public Task<IReadOnlyList<UndoEntry>> PrepareUndoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UndoEntry>>([new UndoEntry("target", "before")]);

        public Task ApplyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UndoAsync(IReadOnlyList<UndoEntry> entries, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await _database.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
