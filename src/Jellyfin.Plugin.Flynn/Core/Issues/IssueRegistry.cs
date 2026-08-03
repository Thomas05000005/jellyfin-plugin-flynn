using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Flynn.Core.Issues;

/// <summary>
/// The one inbox. Every module reports what it found here, and the admin deals with it in one
/// place instead of hunting through cards.
/// <para>
/// Modules submit a complete sweep rather than individual findings, because that is what makes a
/// problem going away close itself. A registry built on report-and-resolve calls needs every
/// module to remember what it said last time, and the ones that forget leave stale entries behind
/// until the inbox is noise and gets ignored.
/// </para>
/// </summary>
public sealed class IssueRegistry
{
    private readonly FlynnDatabase _database;
    private readonly TimeProvider _clock;

    /// <summary>Initializes a new instance of the <see cref="IssueRegistry"/> class.</summary>
    /// <param name="database">Flynn's database.</param>
    /// <param name="clock">Time source; injected so snooze expiry is testable.</param>
    public IssueRegistry(FlynnDatabase database, TimeProvider clock)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Replaces everything known about one module-and-kind scope with what the module just found.
    /// <para>
    /// Anything reported is recorded or refreshed; anything previously known in this scope and
    /// absent from the sweep is resolved. Scoping is what keeps one module's sweep from closing
    /// another's findings.
    /// </para>
    /// <para>
    /// Dismissal survives this. An issue the admin dismissed stays dismissed however many times it
    /// is re-detected, because otherwise dismissing would mean nothing after the next nightly run.
    /// </para>
    /// </summary>
    /// <param name="moduleId">The reporting module.</param>
    /// <param name="kind">The family of problem being swept.</param>
    /// <param name="found">Everything currently wrong in that scope. Empty means all clear.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the scope matches the sweep.</returns>
    public async Task ReconcileAsync(
        string moduleId,
        string kind,
        IReadOnlyList<Issue> found,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(found);

        var mismatched = found.FirstOrDefault(i => i.ModuleId != moduleId || i.Kind != kind);
        if (mismatched is not null)
        {
            throw new ArgumentException(
                $"Sweep is scoped to {moduleId}/{kind} but contains an issue from "
                + $"{mismatched.ModuleId}/{mismatched.Kind}. A sweep may only report its own scope, "
                + "otherwise it would resolve findings it cannot see.",
                nameof(found));
        }

        var now = _clock.GetUtcNow();
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var issue in found)
        {
            await UpsertAsync(connection, (SqliteTransaction)transaction, issue, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await ResolveVanishedAsync(
            connection,
            (SqliteTransaction)transaction,
            moduleId,
            kind,
            found.Select(i => i.Fingerprint).ToList(),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks an issue as one the admin never wants to see again.
    /// <para>
    /// Permanent: it will not come back on its own, however often it is re-detected. It stays
    /// counted though, so a dismissed pile is visible as a number with a way back rather than
    /// becoming a blind spot.
    /// </para>
    /// </summary>
    /// <param name="fingerprint">The issue's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if an issue was dismissed; false if the fingerprint is unknown.</returns>
    public Task<bool> DismissAsync(string fingerprint, CancellationToken cancellationToken) =>
        SetStateAsync(fingerprint, IssueState.Dismissed, null, cancellationToken);

    /// <summary>Hides an issue until a date, after which it returns on its own.</summary>
    /// <param name="fingerprint">The issue's identity.</param>
    /// <param name="until">When it should come back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if an issue was snoozed; false if the fingerprint is unknown.</returns>
    public Task<bool> SnoozeAsync(string fingerprint, DateTimeOffset until, CancellationToken cancellationToken) =>
        SetStateAsync(fingerprint, IssueState.Snoozed, until, cancellationToken);

    /// <summary>Brings a dismissed or snoozed issue back into the inbox.</summary>
    /// <param name="fingerprint">The issue's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if an issue was restored; false if the fingerprint is unknown.</returns>
    public Task<bool> RestoreAsync(string fingerprint, CancellationToken cancellationToken) =>
        SetStateAsync(fingerprint, IssueState.Open, null, cancellationToken);

    /// <summary>
    /// Lists the issues currently asking for attention, worst first.
    /// <para>
    /// A snooze whose date has passed counts as open again, and is reported as such without
    /// needing a sweep to run first.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The open issues.</returns>
    public async Task<IReadOnlyList<TrackedIssue>> GetOpenAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT * FROM issue WHERE state = $open OR (state = $snoozed AND snoozed_until <= $now) "
            + "ORDER BY severity DESC, last_seen DESC";
        read.Parameters.AddWithValue("$open", (int)IssueState.Open);
        read.Parameters.AddWithValue("$snoozed", (int)IssueState.Snoozed);
        read.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));

        var results = new List<TrackedIssue>();
        await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(rows) with { State = IssueState.Open });
        }

        return results;
    }

    /// <summary>
    /// Counts issues by state, so the inbox can show what it is not showing.
    /// <para>
    /// The dismissed count is the point: dismissal is permanent, and a permanent hide with no
    /// visible trace is how a real regression stays hidden for a year.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many issues sit in each state. States with none are absent.</returns>
    public async Task<IReadOnlyDictionary<IssueState, int>> CountByStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT state, COUNT(*) FROM issue GROUP BY state";

        var counts = new Dictionary<IssueState, int>();
        await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[(IssueState)rows.GetInt32(0)] = rows.GetInt32(1);
        }

        return counts;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Issue issue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // The excluded.* form updates the facts on every sighting while leaving state alone, which
        // is what makes dismissal sticky. Only a snooze that has run out is promoted back to open.
        command.CommandText =
            """
            INSERT INTO issue (fingerprint, module_id, kind, subject, severity, title_key, title_args,
                               detail_key, detail_args, state, first_seen, last_seen, snoozed_until)
            VALUES ($fp, $module, $kind, $subject, $severity, $tkey, $targs, $dkey, $dargs, $open, $now, $now, NULL)
            ON CONFLICT(fingerprint) DO UPDATE SET
                severity    = excluded.severity,
                title_key   = excluded.title_key,
                title_args  = excluded.title_args,
                detail_key  = excluded.detail_key,
                detail_args = excluded.detail_args,
                last_seen   = excluded.last_seen,
                state = CASE
                    WHEN issue.state = $dismissed THEN $dismissed
                    WHEN issue.state = $snoozed AND issue.snoozed_until > $now THEN $snoozed
                    ELSE $open
                END,
                snoozed_until = CASE
                    WHEN issue.state = $snoozed AND issue.snoozed_until > $now THEN issue.snoozed_until
                    ELSE NULL
                END
            """;

        command.Parameters.AddWithValue("$fp", issue.Fingerprint);
        command.Parameters.AddWithValue("$module", issue.ModuleId);
        command.Parameters.AddWithValue("$kind", issue.Kind);
        command.Parameters.AddWithValue("$subject", issue.Subject);
        command.Parameters.AddWithValue("$severity", (int)issue.Severity);
        command.Parameters.AddWithValue("$tkey", issue.Title.Key);
        command.Parameters.AddWithValue("$targs", SerializeArgs(issue.Title.Args));
        command.Parameters.AddWithValue("$dkey", (object?)issue.Detail?.Key ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$dargs",
            issue.Detail is null ? DBNull.Value : SerializeArgs(issue.Detail.Args));
        command.Parameters.AddWithValue("$open", (int)IssueState.Open);
        command.Parameters.AddWithValue("$dismissed", (int)IssueState.Dismissed);
        command.Parameters.AddWithValue("$snoozed", (int)IssueState.Snoozed);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ResolveVanishedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string moduleId,
        string kind,
        IReadOnlyList<string> stillPresent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var placeholders = string.Join(",", stillPresent.Select((_, i) => $"$fp{i}"));
        var exclusion = stillPresent.Count == 0 ? string.Empty : $" AND fingerprint NOT IN ({placeholders})";

        // Dismissed issues are left alone: resolving one would drop it out of the dismissed count
        // and quietly reopen it the next time it is detected.
        command.CommandText =
            "UPDATE issue SET state = $resolved, snoozed_until = NULL "
            + "WHERE module_id = $module AND kind = $kind AND state <> $resolved AND state <> $dismissed"
            + exclusion;
        command.Parameters.AddWithValue("$module", moduleId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$resolved", (int)IssueState.Resolved);
        command.Parameters.AddWithValue("$dismissed", (int)IssueState.Dismissed);
        for (var i = 0; i < stillPresent.Count; i++)
        {
            command.Parameters.AddWithValue($"$fp{i}", stillPresent[i]);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SetStateAsync(
        string fingerprint,
        IssueState state,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE issue SET state = $state, snoozed_until = $until WHERE fingerprint = $fp";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue(
            "$until",
            until is null ? DBNull.Value : until.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$fp", fingerprint);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static string SerializeArgs(IReadOnlyList<object?> args) => JsonSerializer.Serialize(args);

    /// <summary>
    /// Rebuilds arguments from JSON, keeping numbers as numbers.
    /// <para>
    /// Storing them as pre-formatted strings would freeze the reporter's culture into the text, so
    /// a French reader would see a thousands separator chosen by whatever locale the nightly task
    /// ran under. Keeping the type lets the reader's culture decide at render time.
    /// </para>
    /// </summary>
    /// <param name="json">The serialised argument list.</param>
    /// <returns>The arguments.</returns>
    private static IReadOnlyList<object?> DeserializeArgs(string json)
    {
        var elements = JsonSerializer.Deserialize<JsonElement[]>(json) ?? [];
        return [.. elements.Select(object? (e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            // The cast is load-bearing: without it the conditional's common type is double, long
            // converts implicitly, and every whole number comes back as a float.
            JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        })];
    }

    private static TrackedIssue Read(SqliteDataReader row)
    {
        var detailKey = row["detail_key"] as string;
        var issue = new Issue(
            (string)row["module_id"],
            (string)row["kind"],
            (string)row["subject"],
            (IssueSeverity)Convert.ToInt32(row["severity"], CultureInfo.InvariantCulture),
            new LocalizedText((string)row["title_key"], DeserializeArgs((string)row["title_args"])),
            detailKey is null ? null : new LocalizedText(detailKey, DeserializeArgs((string)row["detail_args"])));

        var snoozed = row["snoozed_until"] as string;
        return new TrackedIssue(
            issue,
            (IssueState)Convert.ToInt32(row["state"], CultureInfo.InvariantCulture),
            DateTimeOffset.Parse((string)row["first_seen"], CultureInfo.InvariantCulture),
            DateTimeOffset.Parse((string)row["last_seen"], CultureInfo.InvariantCulture),
            snoozed is null ? null : DateTimeOffset.Parse(snoozed, CultureInfo.InvariantCulture));
    }
}
