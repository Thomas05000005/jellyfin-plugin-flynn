using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// Prunes what grows without bound, and deliberately leaves alone what does not.
/// <para>
/// "Delete old rows" is the wrong default here. Two of the three tables hold things that are worth
/// more the longer they are kept, and pruning them would quietly degrade a feature rather than
/// save any meaningful space.
/// </para>
/// </summary>
public sealed class Retention
{
    /// <summary>
    /// How long a resolved issue is kept.
    /// <para>
    /// It closed itself, so nobody is going to act on it. It is kept a while so that a problem
    /// which comes and goes is still legible as a pattern rather than vanishing between sweeps.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ResolvedIssues = TimeSpan.FromDays(90);

    /// <summary>
    /// How long a mutation record is kept once its undo window has closed.
    /// <para>
    /// The manifest is the only thing that makes a write reversible, so this is the one number
    /// here that can destroy something: prune too eagerly and an undo the admin was entitled to
    /// stops being possible. A year is long enough that nobody reaches for it and finds it gone.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MutationLog = TimeSpan.FromDays(365);

    /// <summary>
    /// How long a music audit run is kept.
    /// <para>
    /// Unlike the storage readings these ARE pruned, because nothing depends on their depth: every
    /// query against them takes the single most recent run. A year is kept so that "sixty percent
    /// covered, down from ninety" remains possible to build later, and so that a couple of rows a
    /// night cannot grow without bound in the meantime.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MusicSnapshots = TimeSpan.FromDays(365);

    private readonly FlynnDatabase _database;
    private readonly TimeProvider _clock;
    private readonly ILogger<Retention> _logger;

    /// <summary>Initializes a new instance of the <see cref="Retention"/> class.</summary>
    /// <param name="database">Flynn's database.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public Retention(FlynnDatabase database, TimeProvider clock, ILogger<Retention> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Removes what is past keeping.
    /// <para>
    /// Storage snapshots are NOT touched, and that is a decision rather than an omission. One row
    /// per library and per device per night is roughly a hundred kilobytes a year, and the capacity
    /// forecast caps its horizon at twice the history it can see -- so deleting old readings would
    /// shorten how far ahead Flynn is willing to predict, to save nothing.
    /// </para>
    /// <para>
    /// Dismissed issues are not touched either. A dismissal is a decision the admin made, and the
    /// count of them is what keeps a permanent hide from becoming a blind spot. Pruning them would
    /// erase the trace and let a dismissed problem quietly reappear as new.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were removed from each table.</returns>
    public async Task<RetentionOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var issues = await DeleteAsync(
            connection,
            "DELETE FROM issue WHERE state = $resolved AND last_seen < $before",
            [("$resolved", ((int)Issues.IssueState.Resolved).ToString(CultureInfo.InvariantCulture)),
             ("$before", Stamp(now - ResolvedIssues))],
            cancellationToken).ConfigureAwait(false);

        // Only records whose undo has already been used or which are old enough that the offer has
        // lapsed. A prepared-but-never-applied record is left alone: it means something went wrong
        // mid-write, and that is exactly the row somebody will want to read.
        var mutations = await DeleteAsync(
            connection,
            "DELETE FROM mutation_log WHERE applied_at IS NOT NULL AND prepared_at < $before",
            [("$before", Stamp(now - MutationLog))],
            cancellationToken).ConfigureAwait(false);

        // Never the newest run, whatever its age. A server whose music audit has not run for over a
        // year would otherwise have its card emptied by the cleanup rather than by anything real.
        var before = Stamp(now - MusicSnapshots);
        var music = 0;
        foreach (var table in new[] { "music_loudness_snapshot", "music_images_snapshot" })
        {
            music += await DeleteAsync(
                connection,
                $"DELETE FROM {table} WHERE taken_at < $before "
                + $"AND taken_at <> (SELECT MAX(taken_at) FROM {table})",
                [("$before", before)],
                cancellationToken).ConfigureAwait(false);
        }

        if (issues > 0 || mutations > 0 || music > 0)
        {
            _logger.LogInformation(
                "Retention removed {Issues} resolved issue(s), {Mutations} mutation record(s) and "
                + "{Music} music audit row(s).",
                issues,
                mutations,
                music);
        }

        return new RetentionOutcome(issues, mutations, music);
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<int> DeleteAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        IReadOnlyList<(string Name, string Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>What one retention pass removed.</summary>
/// <param name="ResolvedIssues">Resolved issues deleted.</param>
/// <param name="MutationRecords">Mutation records deleted.</param>
/// <param name="MusicSnapshotRows">Music audit rows deleted, across both snapshot tables.</param>
public readonly record struct RetentionOutcome(
    int ResolvedIssues, int MutationRecords, int MusicSnapshotRows);
