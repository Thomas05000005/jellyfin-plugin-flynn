using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;

namespace Jellyfin.Plugin.Flynn.Modules.Storage;

/// <summary>
/// Reports how much your libraries hold and how much room is left.
/// <para>
/// Jellyfin's own dashboard shows paths but no sizes and no free space, which is the gap this
/// fills. The card reads the last stored sweep and does no work of its own: at four hundred
/// thousand tracks, anything computed on page load is an outage waiting for a busy evening.
/// </para>
/// </summary>
public sealed class StorageModule : IFlynnModule
{
    private readonly StorageRepository _repository;
    private readonly DatabaseReadiness _readiness;

    /// <summary>Initializes a new instance of the <see cref="StorageModule"/> class.</summary>
    /// <param name="repository">Where snapshots live.</param>
    /// <param name="readiness">Whether storage came up at all.</param>
    public StorageModule(StorageRepository repository, DatabaseReadiness readiness)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
    }

    /// <inheritdoc />
    public string Id => "storage";

    /// <inheritdoc />
    public string NameKey => StringKeys.StorageName;

    /// <inheritdoc />
    public string SummaryKey => StringKeys.StorageSummary;

    /// <inheritdoc />
    public ModuleCategory Category => ModuleCategory.System;

    /// <inheritdoc />
    public bool EnabledByDefault => true;

    /// <inheritdoc />
    public async Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
    {
        if (!_readiness.IsReady)
        {
            // Degraded, not Failed: nothing is broken about this module, its storage is simply
            // not there. Saying so beats a card showing last week's numbers as if they were now.
            return new ModuleCard(
                Id,
                ModuleState.Degraded,
                LocalizedText.Of(StringKeys.StorageUnavailable),
                null,
                DateTimeOffset.UtcNow);
        }

        var snapshot = await _repository.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new ModuleCard(
                Id,
                ModuleState.Degraded,
                LocalizedText.Of(StringKeys.StorageNoSnapshotYet),
                null,
                DateTimeOffset.UtcNow);
        }

        var scaled = ScaleBytes(snapshot.TotalLibraryBytes);
        var tightest = snapshot.TightestDevice;
        var detail = tightest is null
            ? null
            : LocalizedText.Of(
                StringKeys.StorageTightestDevice,
                (int)Math.Round(tightest.UsedFraction * 100),
                tightest.MountPath);

        return new ModuleCard(
            Id,
            ModuleState.Healthy,
            LocalizedText.Of(StringKeys.StorageHeadline, scaled.Value, LocalizedText.Of(scaled.UnitKey)),
            detail,
            snapshot.TakenAt);
    }

    /// <summary>
    /// Scales a byte count to the unit a person would read it in, without formatting it.
    /// <para>
    /// Returning a number and a unit rather than a string is the whole point. Formatting here
    /// would use the server process's culture, so a French admin would be shown "4.2 TB" with a
    /// decimal point because that is the locale a background service happened to start under.
    /// Kept as a number, it reaches the reader's culture intact and comes out "4,2".
    /// </para>
    /// <para>
    /// Base 1024 with the short units everyone actually uses. Insisting on TiB would be more
    /// correct and would make the figure disagree with the admin's own file manager.
    /// </para>
    /// </summary>
    /// <param name="bytes">The count.</param>
    /// <returns>
    /// The scaled value, already rounded for display, and the translation KEY for its unit. One decimal from GB upward and
    /// none below: "512.0 MB" reads worse than "512 MB", while "4 TB" hides hundreds of gigabytes.
    /// </returns>
    internal static (double Value, string UnitKey) ScaleBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return (0, StringKeys.UnitBytes);
        }

        string[] unitKeys =
        [
            StringKeys.UnitBytes, StringKeys.UnitKilobytes, StringKeys.UnitMegabytes,
            StringKeys.UnitGigabytes, StringKeys.UnitTerabytes, StringKeys.UnitPetabytes,
        ];
        var order = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), unitKeys.Length - 1);
        var size = bytes / Math.Pow(1024, order);

        return (Math.Round(size, order >= 3 ? 1 : 0), unitKeys[order]);
    }
}
