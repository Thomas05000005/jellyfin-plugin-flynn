using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>
/// Measures what the libraries hold and how much room is left.
/// <para>
/// Split out from the scheduled task so the measuring can be tested without a task runner, and so
/// the task stays a thin wrapper around it.
/// </para>
/// </summary>
public sealed class StorageSweep
{
    /// <summary>
    /// How many items to pull per query.
    /// <para>
    /// The size of a library is only knowable by adding up its items, and this server has four
    /// hundred thousand of them. Asking for them all at once would materialise every one into
    /// memory at the same time; paging keeps the peak flat and costs a few more round trips
    /// against a local database.
    /// </para>
    /// </summary>
    internal const int PageSize = 1000;

    private readonly ILibraryManager _libraries;
    private readonly IDriveProbe _drives;
    private readonly TimeProvider _clock;
    private readonly ILogger<StorageSweep> _logger;

    /// <summary>Initializes a new instance of the <see cref="StorageSweep"/> class.</summary>
    /// <param name="libraries">Jellyfin's library manager.</param>
    /// <param name="drives">How devices are inspected.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger.</param>
    public StorageSweep(
        ILibraryManager libraries,
        IDriveProbe drives,
        TimeProvider clock,
        ILogger<StorageSweep> logger)
    {
        _libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one sweep.</summary>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found.</returns>
    public async Task<StorageSnapshot> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var folders = _libraries.GetVirtualFolders();
        var libraries = new List<LibrarySnapshot>(folders.Count);
        var paths = new List<string>();

        for (var i = 0; i < folders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders[i];

            if (folder.Locations is not null)
            {
                paths.AddRange(folder.Locations);
            }

            if (!Guid.TryParse(folder.ItemId, out var id) || id == Guid.Empty)
            {
                // A library with no resolvable id cannot be queried. Skipping it loses one row
                // rather than the whole sweep.
                _logger.LogWarning(
                    "Skipping library {Library}: its item id {ItemId} is not usable.",
                    folder.Name,
                    folder.ItemId);
                continue;
            }

            var measured = Measure(id, cancellationToken);
            libraries.Add(new LibrarySnapshot(
                id.ToString("N"),
                folder.Name ?? "(unnamed)",
                measured.Count,
                measured.Bytes));

            progress?.Report((i + 1) * 90d / Math.Max(1, folders.Count));
        }

        var devices = _drives.Probe(paths);
        progress?.Report(100);

        _logger.LogInformation(
            "Storage sweep: {Libraries} library/libraries, {Devices} device(s).",
            libraries.Count,
            devices.Count);

        return await Task.FromResult(
            new StorageSnapshot(_clock.GetUtcNow(), libraries, devices)).ConfigureAwait(false);
    }

    private (long Count, long Bytes) Measure(Guid libraryId, CancellationToken cancellationToken)
    {
        long count = 0;
        long bytes = 0;
        var start = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = _libraries.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [libraryId],
                Recursive = true,
                IsFolder = false,
                StartIndex = start,
                Limit = PageSize,
                // Nothing here needs the total, and asking for it makes the database count the
                // whole set again on every page.
                EnableTotalRecordCount = false,
            });

            if (page.Count == 0)
            {
                break;
            }

            count += page.Count;
            foreach (var item in page)
            {
                // Null for anything the server has not measured yet. Treating that as zero is the
                // honest reading: it is unknown, not empty, and it will fill in on the next scan.
                bytes += item.Size ?? 0;
            }

            if (page.Count < PageSize)
            {
                break;
            }

            start += PageSize;
        }

        return (count, bytes);
    }
}
