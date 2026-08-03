using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Flynn.Core.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Mutations;

/// <summary>
/// The only route by which Flynn changes anything.
/// <para>
/// It enforces an order that cannot be enforced by good intentions: capture how to undo, store
/// that manifest, then apply. A mutation that cannot say how it would be reversed is refused
/// before it runs rather than discovered afterwards.
/// </para>
/// </summary>
public sealed class MutationKernel
{
    private readonly FlynnDatabase _database;
    private readonly TimeProvider _clock;
    private readonly ILogger<MutationKernel> _logger;

    /// <summary>Initializes a new instance of the <see cref="MutationKernel"/> class.</summary>
    /// <param name="database">Flynn's database.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public MutationKernel(FlynnDatabase database, TimeProvider clock, ILogger<MutationKernel> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Applies a mutation, if the configured write level allows it and it can be undone.
    /// </summary>
    /// <param name="mutation">The change to make.</param>
    /// <param name="allowedLevel">
    /// The highest level the admin has enabled. A mutation above it is refused, which is what makes
    /// report-only a real mode rather than a promise.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A receipt identifying the applied change, or null when there was nothing to do.</returns>
    /// <exception cref="MutationRefusedException">
    /// The mutation exceeds the allowed write level, or could not produce an undo manifest.
    /// </exception>
    public async Task<long?> ApplyAsync(
        IMutation mutation,
        WriteLevel allowedLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        if (mutation.Level > allowedLevel)
        {
            throw new MutationRefusedException(
                $"{mutation.ModuleId}/{mutation.Kind} needs write level {mutation.Level} but the "
                + $"server is configured for at most {allowedLevel}.");
        }

        var preview = await mutation.PreviewAsync(cancellationToken).ConfigureAwait(false);
        if (preview.IsEmpty)
        {
            return null;
        }

        var undo = await mutation.PrepareUndoAsync(cancellationToken).ConfigureAwait(false);

        // Level 2 touches the user's files. Without a manifest there is no way back, so the change
        // simply does not happen. Lower levels are recoverable through the server's own state, so
        // an empty manifest there is allowed but still recorded as empty.
        if (mutation.Level == WriteLevel.Files && undo.Count == 0)
        {
            throw new MutationRefusedException(
                $"{mutation.ModuleId}/{mutation.Kind} writes to files but produced no undo manifest. "
                + "Refusing to make a change that cannot be reversed.");
        }

        // Stored BEFORE applying. A crash between these two lines leaves a manifest for a change
        // that never happened, which is harmless; the reverse order would leave a change nothing
        // knows how to undo.
        var id = await RecordAsync(mutation, preview.Steps.Count, undo, cancellationToken).ConfigureAwait(false);

        try
        {
            await mutation.ApplyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _logger.LogError(
                "Mutation {Module}/{Kind} (record {Id}) failed while applying. Its undo manifest is "
                + "stored and the record is left unapplied.",
                mutation.ModuleId,
                mutation.Kind,
                id);
            throw;
        }

        await StampAsync(id, "applied_at", cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Applied mutation {Module}/{Kind} ({Steps} step(s)), record {Id}.",
            mutation.ModuleId,
            mutation.Kind,
            preview.Steps.Count,
            id);

        return id;
    }

    /// <summary>Reverses a previously applied mutation from its stored manifest.</summary>
    /// <param name="recordId">The receipt returned by <see cref="ApplyAsync"/>.</param>
    /// <param name="mutation">
    /// The mutation that produced the manifest. Only it knows how to read the payloads back.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the change is reversed.</returns>
    /// <exception cref="MutationRefusedException">
    /// The record is unknown, was never applied, or has already been undone.
    /// </exception>
    public async Task UndoAsync(long recordId, IMutation mutation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        var record = await LoadAsync(recordId, cancellationToken).ConfigureAwait(false)
            ?? throw new MutationRefusedException($"No mutation record {recordId}.");

        if (record.AppliedAt is null)
        {
            throw new MutationRefusedException(
                $"Mutation record {recordId} was never applied, so there is nothing to undo.");
        }

        if (record.UndoneAt is not null)
        {
            throw new MutationRefusedException($"Mutation record {recordId} has already been undone.");
        }

        var entries = JsonSerializer.Deserialize<UndoEntry[]>(record.Manifest) ?? [];
        await mutation.UndoAsync(entries, cancellationToken).ConfigureAwait(false);
        await StampAsync(recordId, "undone_at", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Undid mutation record {Id} ({Module}/{Kind}).", recordId, record.ModuleId, record.Kind);
    }

    /// <summary>Reads one record from the log.</summary>
    /// <param name="recordId">The record id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or null when unknown.</returns>
    internal async Task<MutationRecord?> LoadAsync(long recordId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT module_id, kind, write_level, step_count, undo_manifest, applied_at, undone_at "
            + "FROM mutation_log WHERE id = $id";
        read.Parameters.AddWithValue("$id", recordId);

        await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MutationRecord(
            rows.GetString(0),
            rows.GetString(1),
            (WriteLevel)rows.GetInt32(2),
            rows.GetInt32(3),
            rows.GetString(4),
            rows.IsDBNull(5) ? null : rows.GetString(5),
            rows.IsDBNull(6) ? null : rows.GetString(6));
    }

    private async Task<long> RecordAsync(
        IMutation mutation,
        int stepCount,
        IReadOnlyList<UndoEntry> undo,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO mutation_log (module_id, kind, write_level, step_count, undo_manifest, prepared_at) "
            + "VALUES ($module, $kind, $level, $steps, $manifest, $now); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$module", mutation.ModuleId);
        insert.Parameters.AddWithValue("$kind", mutation.Kind);
        insert.Parameters.AddWithValue("$level", (int)mutation.Level);
        insert.Parameters.AddWithValue("$steps", stepCount);
        insert.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(undo));
        insert.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));

        var id = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    private async Task StampAsync(long recordId, string column, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();

        // The column name is a compile-time constant from this file, never caller input.
        update.CommandText = $"UPDATE mutation_log SET {column} = $now WHERE id = $id";
        update.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$id", recordId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>One row of the mutation log.</summary>
/// <param name="ModuleId">The module that made the change.</param>
/// <param name="Kind">The kind of change.</param>
/// <param name="Level">How far it reached.</param>
/// <param name="StepCount">How many steps the preview held.</param>
/// <param name="Manifest">The serialised undo entries.</param>
/// <param name="AppliedAt">When it was applied, or null if it never was.</param>
/// <param name="UndoneAt">When it was undone, or null.</param>
internal sealed record MutationRecord(
    string ModuleId,
    string Kind,
    WriteLevel Level,
    int StepCount,
    string Manifest,
    string? AppliedAt,
    string? UndoneAt);
