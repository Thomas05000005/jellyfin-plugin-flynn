using System.Globalization;
using Jellyfin.Plugin.Flynn.Core.Data;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// One audit run, with the moment it was taken.
/// <para>
/// The timestamp travels with the rows rather than being fetched separately, and that is a
/// correctness point rather than tidiness: two queries each evaluating their own
/// <c>SELECT MAX(taken_at)</c> are two transactions, so a run finishing between them would put one
/// night's figures under another night's date. Read together, they cannot disagree.
/// </para>
/// </summary>
/// <typeparam name="T">What the audit found for one library.</typeparam>
/// <param name="TakenAt">When the run happened, or null when nothing has ever run.</param>
/// <param name="Libraries">One entry per music library.</param>
public sealed record MusicSnapshot<T>(DateTimeOffset? TakenAt, IReadOnlyList<T> Libraries);

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

    /// <summary>Stores one loudness audit run.</summary>
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
            command.Parameters.AddWithValue("$at", Stamp(takenAt));
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

    /// <summary>Reads the most recent loudness audit, with the moment it was taken.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per library, or empty when nothing has run.</returns>
    public async Task<MusicSnapshot<LibraryLoudness>> GetLatestLoudnessAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT taken_at, library_id, library_name, scan_enabled, tracks, from_tag, measured "
            + "FROM music_loudness_snapshot "
            + "WHERE taken_at = (SELECT MAX(taken_at) FROM music_loudness_snapshot) "
            + "ORDER BY library_name";

        DateTimeOffset? takenAt = null;
        var results = new List<LibraryLoudness>();
        await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            takenAt ??= DateTimeOffset.Parse(rows.GetString(0), CultureInfo.InvariantCulture);
            results.Add(new LibraryLoudness(
                Guid.ParseExact(rows.GetString(1), "N"),
                rows.GetString(2),
                rows.GetInt32(3) != 0,
                rows.GetInt32(4),
                rows.GetInt32(5),
                rows.GetInt32(6)));
        }

        return new MusicSnapshot<LibraryLoudness>(takenAt, results);
    }

    /// <summary>Stores one cover-art audit run.</summary>
    /// <param name="takenAt">When it ran.</param>
    /// <param name="libraries">What each music library's stored covers cost.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the rows are written.</returns>
    public async Task SaveImagesAsync(
        DateTimeOffset takenAt,
        IReadOnlyList<LibraryTrackImages> libraries,
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
                "INSERT OR REPLACE INTO music_images_snapshot "
                + "(taken_at, library_id, library_name, tracks_with_image, total_bytes, "
                + "redundant_images, redundant_bytes) "
                + "VALUES ($at, $id, $name, $tracks, $total, $copies, $copyBytes)";
            command.Parameters.AddWithValue("$at", Stamp(takenAt));
            command.Parameters.AddWithValue("$id", library.LibraryId.ToString("N"));
            command.Parameters.AddWithValue("$name", library.LibraryName);
            command.Parameters.AddWithValue("$tracks", library.TracksWithImage);
            command.Parameters.AddWithValue("$total", library.TotalBytes);
            command.Parameters.AddWithValue("$copies", library.RedundantImages);
            command.Parameters.AddWithValue("$copyBytes", library.RedundantBytes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the most recent cover-art audit, with the moment it was taken.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per library, or empty when nothing has run.</returns>
    public async Task<MusicSnapshot<LibraryTrackImages>> GetLatestImagesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT taken_at, library_id, library_name, tracks_with_image, total_bytes, "
            + "redundant_images, redundant_bytes FROM music_images_snapshot "
            + "WHERE taken_at = (SELECT MAX(taken_at) FROM music_images_snapshot) "
            + "ORDER BY library_name";

        DateTimeOffset? takenAt = null;
        var results = new List<LibraryTrackImages>();
        await using var rows = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            takenAt ??= DateTimeOffset.Parse(rows.GetString(0), CultureInfo.InvariantCulture);
            results.Add(new LibraryTrackImages(
                Guid.ParseExact(rows.GetString(1), "N"),
                rows.GetString(2),
                rows.GetInt32(3),
                rows.GetInt64(4),
                rows.GetInt32(5),
                rows.GetInt64(6)));
        }

        return new MusicSnapshot<LibraryTrackImages>(takenAt, results);
    }

    /// <summary>
    /// Formats the moment a run happened.
    /// <para>
    /// Converted to UTC first, because <c>MAX(taken_at)</c> compares TEXT lexicographically and
    /// that only orders correctly while every writer produces the same offset. A caller passing a
    /// local <see cref="DateTimeOffset"/> would otherwise be able to write a row that sorts before
    /// an older one, and the card would quietly show the wrong night.
    /// </para>
    /// </summary>
    /// <param name="takenAt">When the run happened.</param>
    /// <returns>A round-trip UTC stamp.</returns>
    private static string Stamp(DateTimeOffset takenAt) =>
        takenAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
