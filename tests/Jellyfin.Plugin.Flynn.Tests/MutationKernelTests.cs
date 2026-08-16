using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Mutations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The kernel's promises: nothing above the configured level, nothing that cannot be reversed, and
/// the manifest on disk before the change is made rather than after.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class MutationKernelTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

    private FlynnDatabase _database = null!;
    private MutationKernel _kernel = null!;

    public async Task InitializeAsync()
    {
        _database = new FlynnDatabase(Path.Combine(_directory, "flynn.db"));
        await new SchemaMigrator(_database, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(Migrations.All, CancellationToken.None);
        _kernel = new MutationKernel(_database, TimeProvider.System, NullLogger<MutationKernel>.Instance);
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
    public async Task AMutationAboveTheAllowedLevel_IsRefusedWithoutRunning()
    {
        var mutation = new SpyMutation(WriteLevel.Files);

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            _kernel.ApplyAsync(mutation, WriteLevel.Server, CancellationToken.None));

        Assert.False(mutation.PreviewCalled);
        Assert.False(mutation.PrepareCalled);
        Assert.False(mutation.ApplyCalled);
    }

    /// <summary>Report-only has to be a real mode, not a promise.</summary>
    [Fact]
    public async Task AtWriteLevelNone_NothingIsEverApplied()
    {
        var mutation = new SpyMutation(WriteLevel.Server);

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            _kernel.ApplyAsync(mutation, WriteLevel.None, CancellationToken.None));

        Assert.False(mutation.ApplyCalled);
    }

    [Fact]
    public async Task AnEmptyPreview_DoesNothingAndRecordsNothing()
    {
        var mutation = new SpyMutation(WriteLevel.Server) { Steps = 0 };

        var receipt = await _kernel.ApplyAsync(mutation, WriteLevel.Files, CancellationToken.None);

        Assert.Null(receipt);
        Assert.False(mutation.PrepareCalled);
        Assert.False(mutation.ApplyCalled);
    }

    /// <summary>
    /// The rule that makes level 2 survivable. A file change with no way back does not happen at
    /// all, rather than happening and being discovered afterwards.
    /// </summary>
    [Fact]
    public async Task AFileMutationWithNoUndoManifest_IsRefusedBeforeItRuns()
    {
        var mutation = new SpyMutation(WriteLevel.Files) { UndoEntries = 0 };

        var refusal = await Assert.ThrowsAsync<MutationRefusedException>(() =>
            _kernel.ApplyAsync(mutation, WriteLevel.Files, CancellationToken.None));

        Assert.Contains("undo manifest", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(mutation.PrepareCalled, "the manifest must be attempted before the refusal");
        Assert.False(mutation.ApplyCalled, "nothing may be applied once the refusal is decided");
    }

    /// <summary>
    /// Ordering is the whole design. Capturing undo as a by-product of applying means it can only
    /// ever be checked once the damage is done.
    /// </summary>
    [Fact]
    public async Task TheUndoManifest_IsStoredBeforeTheChangeIsApplied()
    {
        var mutation = new SpyMutation(WriteLevel.Files);

        await _kernel.ApplyAsync(mutation, WriteLevel.Files, CancellationToken.None);

        Assert.Equal(["preview", "prepare-undo", "apply"], mutation.Calls);
    }

    [Fact]
    public async Task AFailureWhileApplying_LeavesTheRecordUnapplied()
    {
        var mutation = new SpyMutation(WriteLevel.Files) { ThrowOnApply = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _kernel.ApplyAsync(mutation, WriteLevel.Files, CancellationToken.None));

        // The manifest survives even though the change did not happen: harmless, and the reverse
        // ordering would have left a change nothing knows how to reverse.
        var record = await _kernel.LoadAsync(1, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Null(record.AppliedAt);
    }

    [Fact]
    public async Task AnAppliedMutation_CanBeUndoneOnce()
    {
        var mutation = new SpyMutation(WriteLevel.Files);
        var receipt = await _kernel.ApplyAsync(mutation, WriteLevel.Files, CancellationToken.None);
        Assert.NotNull(receipt);

        await _kernel.UndoAsync(receipt.Value, mutation, CancellationToken.None);

        Assert.Equal(2, mutation.UndoneEntries);

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            _kernel.UndoAsync(receipt.Value, mutation, CancellationToken.None));
    }

    [Fact]
    public async Task UndoingSomethingThatWasNeverApplied_IsRefused()
    {
        var mutation = new SpyMutation(WriteLevel.Files) { ThrowOnApply = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _kernel.ApplyAsync(mutation, WriteLevel.Files, CancellationToken.None));

        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            _kernel.UndoAsync(1, mutation, CancellationToken.None));
    }

    [Fact]
    public async Task UndoingAnUnknownRecord_IsRefused()
    {
        await Assert.ThrowsAsync<MutationRefusedException>(() =>
            _kernel.UndoAsync(999, new SpyMutation(WriteLevel.Server), CancellationToken.None));
    }

    [Fact]
    public async Task AServerLevelMutationWithNoManifest_IsAllowed()
    {
        // The server keeps its own copy of what it owns, so this level is recoverable without one.
        var mutation = new SpyMutation(WriteLevel.Server) { UndoEntries = 0 };

        var receipt = await _kernel.ApplyAsync(mutation, WriteLevel.Server, CancellationToken.None);

        Assert.NotNull(receipt);
        Assert.True(mutation.ApplyCalled);
    }

    private sealed class SpyMutation(WriteLevel level) : IMutation
    {
        public List<string> Calls { get; } = [];

        public int Steps { get; init; } = 2;

        public int UndoEntries { get; init; } = 2;

        public bool ThrowOnApply { get; init; }

        public int UndoneEntries { get; private set; }

        public bool PreviewCalled => Calls.Contains("preview");

        public bool PrepareCalled => Calls.Contains("prepare-undo");

        public bool ApplyCalled => Calls.Contains("apply");

        public string ModuleId => "spy";

        public string Kind => "spy-change";

        public WriteLevel Level => level;

        public Task<MutationPreview> PreviewAsync(CancellationToken cancellationToken)
        {
            Calls.Add("preview");
            var steps = Enumerable.Range(0, Steps)
                .Select(i => new MutationStep(LocalizedText.Of("test.step"), $"target-{i}"))
                .ToList();
            return Task.FromResult(new MutationPreview(steps, []));
        }

        public Task<IReadOnlyList<UndoEntry>> PrepareUndoAsync(CancellationToken cancellationToken)
        {
            Calls.Add("prepare-undo");
            IReadOnlyList<UndoEntry> entries = Enumerable.Range(0, UndoEntries)
                .Select(i => new UndoEntry($"target-{i}", $"original-{i}"))
                .ToList();
            return Task.FromResult(entries);
        }

        public Task ApplyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("apply");
            return ThrowOnApply
                ? throw new InvalidOperationException("the disk went away")
                : Task.CompletedTask;
        }

        public Task UndoAsync(IReadOnlyList<UndoEntry> entries, CancellationToken cancellationToken)
        {
            UndoneEntries = entries.Count;
            return Task.CompletedTask;
        }
    }
}
