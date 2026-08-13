using System.Globalization;
using Jellyfin.Plugin.Flynn.Core.Data;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// Stores what the nightly music audit found, so the page reads one row instead of walking the
/// library.
/// <para>
/// Append-only, one row per library per run. The history is small -- a handful of rows a night --
/// and it is what turns "sixty percent covered" into "sixty percent covered, down from ninety",
/// which is the version an admin can act on.
/// </para>
/// </summary>
public sealed class MusicRepository
{
    private readonly FlynnDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="MusicRepository"/> class.</summary>
    /// <param name="database">Flynn's database.</param>
    public MusicRepository(FlynnDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>Stores one audit run.</summary>
    /// <param name="takenAt">When it ran.</param>
    /// <param name="libraries">What each music library holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the rows are written.</returns>
    public async Task SaveLoudnessAsync(
        DateTimeOffset takenAt,
        IReadOnlyList<LibraryLoudness> libraries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var library in libraries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText =
                "INSERT OR REPLACE INTO music_loudness_snapshot "
                + "(taken_at, library_id, library_name, scan_enabled, tracks, from_tag, measured) "
                + "VALUES ($at, $id, $name, $scan, $tracks, $tag, $measured)";
            command.Parameters.AddWithValue("$at", takenAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", library.LibraryId.ToString("N"));
            command.Parameters.AddWithValue("$name", library.LibraryName);
            command.Parameters.AddWithValue("$scan", library.ScanEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$tracks", library.Tracks);
            command.Parameters.AddWithValue("$tag", library.FromTag);
            command.Parameters.AddWithValue("$measured", library.Measured);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the most recent audit.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per library, or empty when nothing has run.</returns>
    public async Task<IReadOnlyList<LibraryLoudness>> GetLatestLoudnessAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT library_id, library_name, scan_enabled, tracks, from_tag, measured "
            + "FROM music_loudness_snapshot "
            + "WHERE taken_at = (SELECT MAX(taken_at) FROM music_loudness_snapshot) "
            + "ORDER BY library_name";

        var results = new List<LibraryLoudness>();
        await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new LibraryLoudness(
                Guid.ParseExact(rows.GetString(0), "N"),
                rows.GetString(1),
                rows.GetInt32(2) != 0,
                rows.GetInt32(3),
                rows.GetInt32(4),
                rows.GetInt32(5)));
        }

        return results;
    }

    /// <summary>When the most recent audit ran.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The time, or null when nothing has run.</returns>
    public async Task<DateTimeOffset?> GetLatestLoudnessTimeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT MAX(taken_at) FROM music_loudness_snapshot";

        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
            : null;
    }
}
