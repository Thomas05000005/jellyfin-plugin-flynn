namespace Jellyfin.Plugin.Flynn.Core.Localization;

/// <summary>
/// Every translatable key Flynn uses, as a constant.
/// <para>
/// Keys live here rather than as literals at the call sites so that the set is enumerable, which
/// is what lets a test assert that every catalogue defines every key. A missing translation is
/// otherwise invisible until someone with that language opens the page.
/// </para>
/// <para>
/// Naming: <c>area.thing.part</c>, lowercase kebab within each segment. Placeholders are
/// <c>{0}</c>, <c>{1}</c>... and their meaning is fixed once the key ships — a translator cannot
/// see the call site, so reordering arguments silently corrupts every language but the one you
/// tested.
/// </para>
/// </summary>
public static class StringKeys
{
    /// <summary>Shown on a module card whose module threw while reporting.</summary>
    public const string ModuleUnavailableHeadline = "module.unavailable.headline";

    /// <summary>Detail under <see cref="ModuleUnavailableHeadline"/>.</summary>
    public const string ModuleUnavailableDetail = "module.unavailable.detail";

    /// <summary>Detail shown when a module exceeded its reporting deadline.</summary>
    public const string ModuleTimedOutDetail = "module.timed-out.detail";

    /// <summary>Headline on the card of a module the admin switched off.</summary>
    public const string ModuleDisabledHeadline = "module.disabled.headline";

    /// <summary>Shown when a configuration change was refused as unpersistable.</summary>
    public const string ConfigRejected = "config.rejected";

    /// <summary>Headline on the storage card: how much the libraries hold. {0} = formatted size.</summary>
    public const string StorageHeadline = "storage.headline";

    /// <summary>Detail on the storage card: the tightest device. {0} = percent used, {1} = mount path.</summary>
    public const string StorageTightestDevice = "storage.tightest-device";

    /// <summary>Shown before the first sweep has run.</summary>
    public const string StorageNoSnapshotYet = "storage.no-snapshot-yet";

    /// <summary>Shown when the module cannot reach its own storage.</summary>
    public const string StorageUnavailable = "storage.unavailable";

    /// <summary>Admin page chrome: <c>ui.modules</c>.</summary>
    public const string UiModules = "ui.modules";
    /// <summary>Admin page chrome: <c>ui.attention</c>.</summary>
    public const string UiAttention = "ui.attention";
    /// <summary>Admin page chrome: <c>ui.nothing-to-do</c>.</summary>
    public const string UiNothingToDo = "ui.nothing-to-do";
    /// <summary>Admin page chrome: <c>ui.no-modules</c>.</summary>
    public const string UiNoModules = "ui.no-modules";
    /// <summary>Admin page chrome: <c>ui.unreachable</c>.</summary>
    public const string UiUnreachable = "ui.unreachable";
    /// <summary>Admin page chrome: <c>ui.storage-down</c>.</summary>
    public const string UiStorageDown = "ui.storage-down";
    /// <summary>Admin page chrome: <c>ui.storage-down-detail</c>.</summary>
    public const string UiStorageDownDetail = "ui.storage-down-detail";
    /// <summary>Admin page chrome: <c>ui.state.disabled</c>.</summary>
    public const string UiStateDisabled = "ui.state.disabled";
    /// <summary>Admin page chrome: <c>ui.state.healthy</c>.</summary>
    public const string UiStateHealthy = "ui.state.healthy";
    /// <summary>Admin page chrome: <c>ui.state.degraded</c>.</summary>
    public const string UiStateDegraded = "ui.state.degraded";
    /// <summary>Admin page chrome: <c>ui.state.failed</c>.</summary>
    public const string UiStateFailed = "ui.state.failed";
    /// <summary>Admin page chrome: <c>ui.just-now</c>.</summary>
    public const string UiJustNow = "ui.just-now";
    /// <summary>Admin page chrome: <c>ui.minutes-ago</c>.</summary>
    public const string UiMinutesAgo = "ui.minutes-ago";
    /// <summary>Admin page chrome: <c>ui.hours-ago</c>.</summary>
    public const string UiHoursAgo = "ui.hours-ago";
    /// <summary>Admin page chrome: <c>ui.days-ago</c>.</summary>
    public const string UiDaysAgo = "ui.days-ago";
    /// <summary>Admin page chrome: <c>ui.withheld.dismissed</c>.</summary>
    public const string UiWithheldDismissed = "ui.withheld.dismissed";
    /// <summary>Admin page chrome: <c>ui.withheld.snoozed</c>.</summary>
    public const string UiWithheldSnoozed = "ui.withheld.snoozed";
    /// <summary>Admin page chrome: <c>ui.withheld.resolved</c>.</summary>
    public const string UiWithheldResolved = "ui.withheld.resolved";

    /// <summary>Music module name: <c>music.name</c>.</summary>
    public const string MusicName = "music.name";
    /// <summary>Music module summary: <c>music.summary</c>.</summary>
    public const string MusicSummary = "music.summary";
    /// <summary>Before the first audit has run: <c>music.no-audit</c>.</summary>
    public const string MusicNoAudit = "music.no-audit";
    /// <summary>No music at all: <c>music.empty</c>.</summary>
    public const string MusicEmpty = "music.empty";
    /// <summary>Loudness coverage headline: <c>music.loudness-headline</c>.</summary>
    public const string MusicLoudnessHeadline = "music.loudness-headline";
    /// <summary>Loudness coverage detail: <c>music.loudness-detail</c>.</summary>
    public const string MusicLoudnessDetail = "music.loudness-detail";
    /// <summary>How the other music libraries look: <c>music.other-libraries</c>.</summary>
    public const string MusicOtherLibraries = "music.other-libraries";
    /// <summary>Said when no library has the scan switched on: <c>music.scan-off</c>.</summary>
    public const string MusicScanOff = "music.scan-off";
    /// <summary>Issue title, scan switched off: <c>issue.loudness.scan-off</c>.</summary>
    public const string IssueLoudnessScanOff = "issue.loudness.scan-off";
    /// <summary>Issue title, scan on but incomplete: <c>issue.loudness.incomplete</c>.</summary>
    public const string IssueLoudnessIncomplete = "issue.loudness.incomplete";
    /// <summary>Issue detail: <c>issue.loudness.detail</c>.</summary>
    public const string IssueLoudnessDetail = "issue.loudness.detail";

    /// <summary>Cover-art module name: <c>music-images.name</c>.</summary>
    public const string MusicImagesName = "music-images.name";
    /// <summary>Cover-art module summary: <c>music-images.summary</c>.</summary>
    public const string MusicImagesSummary = "music-images.summary";
    /// <summary>Before the first cover-art audit: <c>music-images.no-audit</c>.</summary>
    public const string MusicImagesNoAudit = "music-images.no-audit";
    /// <summary>No cover stored by the server at all: <c>music-images.none</c>.</summary>
    public const string MusicImagesNone = "music-images.none";
    /// <summary>Reclaimable cover art: <c>music-images.headline</c>.</summary>
    public const string MusicImagesHeadline = "music-images.headline";
    /// <summary>Why the copies exist: <c>music-images.detail</c>.</summary>
    public const string MusicImagesDetail = "music-images.detail";
    /// <summary>Nothing is duplicated: <c>music-images.clean</c>.</summary>
    public const string MusicImagesClean = "music-images.clean";
    /// <summary>Issue title: <c>issue.track-images</c>.</summary>
    public const string IssueTrackImages = "issue.track-images";
    /// <summary>Issue detail: <c>issue.track-images.detail</c>.</summary>
    public const string IssueTrackImagesDetail = "issue.track-images.detail";

    /// <summary>Admin page chrome: <c>ui.toggle-failed</c>.</summary>
    public const string UiToggleFailed = "ui.toggle-failed";
    /// <summary>Issue state, for a row that is not in the inbox: <c>ui.state.snoozed</c>.</summary>
    public const string UiStateSnoozed = "ui.state.snoozed";
    /// <summary>Issue state, for a row that is not in the inbox: <c>ui.state.dismissed</c>.</summary>
    public const string UiStateDismissed = "ui.state.dismissed";
    /// <summary>Inbox action: <c>ui.dismiss</c>.</summary>
    public const string UiDismiss = "ui.dismiss";
    /// <summary>Inbox action: <c>ui.snooze-7</c>.</summary>
    public const string UiSnooze7 = "ui.snooze-7";
    /// <summary>Inbox action: <c>ui.restore</c>.</summary>
    public const string UiRestore = "ui.restore";
    /// <summary>Asked before a permanent hide: <c>ui.dismiss-confirm</c>.</summary>
    public const string UiDismissConfirm = "ui.dismiss-confirm";
    /// <summary>Admin page chrome: <c>ui.issue-action-failed</c>.</summary>
    public const string UiIssueActionFailed = "ui.issue-action-failed";
    /// <summary>Shown when everything withheld closed itself: <c>ui.withheld-resolved-only</c>.</summary>
    public const string UiWithheldResolvedOnly = "ui.withheld-resolved-only";

    /// <summary>Dashboard category heading: <c>ui.cat.operations</c>.</summary>
    public const string UiCatOperations = "ui.cat.operations";
    /// <summary>Dashboard category heading: <c>ui.cat.system</c>.</summary>
    public const string UiCatSystem = "ui.cat.system";
    /// <summary>Dashboard category heading: <c>ui.cat.library</c>.</summary>
    public const string UiCatLibrary = "ui.cat.library";
    /// <summary>Dashboard category heading: <c>ui.cat.music</c>.</summary>
    public const string UiCatMusic = "ui.cat.music";
    /// <summary>Dashboard category heading: <c>ui.cat.people</c>.</summary>
    public const string UiCatPeople = "ui.cat.people";

    /// <summary>The Storage module's display name.</summary>
    public const string StorageName = "storage.name";

    /// <summary>The Storage module's one-line description.</summary>
    public const string StorageSummary = "storage.summary";

    /// <summary>Size unit: bytes.</summary>
    public const string UnitBytes = "unit.bytes";

    /// <summary>Size unit: kilobytes.</summary>
    public const string UnitKilobytes = "unit.kilobytes";

    /// <summary>Size unit: megabytes.</summary>
    public const string UnitMegabytes = "unit.megabytes";

    /// <summary>Size unit: gigabytes.</summary>
    public const string UnitGigabytes = "unit.gigabytes";

    /// <summary>Size unit: terabytes.</summary>
    public const string UnitTerabytes = "unit.terabytes";

    /// <summary>Size unit: petabytes.</summary>
    public const string UnitPetabytes = "unit.petabytes";

    /// <summary>Capacity forecast: <c>forecast.name</c>.</summary>
    public const string ForecastName = "forecast.name";
    /// <summary>Capacity forecast: <c>forecast.summary</c>.</summary>
    public const string ForecastSummary = "forecast.summary";
    /// <summary>Capacity forecast: <c>forecast.collecting</c>.</summary>
    public const string ForecastCollecting = "forecast.collecting";
    /// <summary>Capacity forecast: <c>forecast.filling</c>.</summary>
    public const string ForecastFilling = "forecast.filling";
    /// <summary>Capacity forecast: <c>forecast.steady</c>.</summary>
    public const string ForecastSteady = "forecast.steady";
    /// <summary>Capacity forecast: <c>forecast.inconclusive</c>.</summary>
    public const string ForecastInconclusive = "forecast.inconclusive";
    /// <summary>Capacity forecast: <c>forecast.rate</c>.</summary>
    public const string ForecastRate = "forecast.rate";
    /// <summary>Capacity forecast: <c>forecast.no-devices</c>.</summary>
    public const string ForecastNoDevices = "forecast.no-devices";
    /// <summary>Capacity forecast: <c>issue.capacity.title</c>.</summary>
    public const string IssueCapacityTitle = "issue.capacity.title";
    /// <summary>Capacity forecast: <c>issue.capacity.detail</c>.</summary>
    public const string IssueCapacityDetail = "issue.capacity.detail";

    /// <summary>Resources module: <c>resources.name</c>.</summary>
    public const string ResourcesName = "resources.name";
    /// <summary>Resources module: <c>resources.summary</c>.</summary>
    public const string ResourcesSummary = "resources.summary";
    /// <summary>Resources module: <c>resources.headline</c>.</summary>
    public const string ResourcesHeadline = "resources.headline";
    /// <summary>Resources module: <c>resources.of-cores</c>.</summary>
    public const string ResourcesOfCores = "resources.of-cores";
    /// <summary>Resources module: <c>resources.of-quota</c>.</summary>
    public const string ResourcesOfQuota = "resources.of-quota";
    /// <summary>Resources module: <c>resources.measuring</c>.</summary>
    public const string ResourcesMeasuring = "resources.measuring";
    /// <summary>Resources module: <c>resources.unavailable</c>.</summary>
    public const string ResourcesUnavailable = "resources.unavailable";
    /// <summary>Resources module: <c>resources.cache-note</c>.</summary>
    public const string ResourcesCacheNote = "resources.cache-note";

    /// <summary>Gets every key defined above, for catalogue completeness checks.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ModuleUnavailableHeadline,
        ModuleUnavailableDetail,
        ModuleTimedOutDetail,
        ModuleDisabledHeadline,
        ConfigRejected,
        StorageHeadline,
        StorageTightestDevice,
        StorageNoSnapshotYet,
        StorageUnavailable,
        UiModules,
        UiAttention,
        UiNothingToDo,
        UiNoModules,
        UiUnreachable,
        UiStorageDown,
        UiStorageDownDetail,
        UiStateDisabled,
        UiStateHealthy,
        UiStateDegraded,
        UiStateFailed,
        UiJustNow,
        UiMinutesAgo,
        UiHoursAgo,
        UiDaysAgo,
        UiWithheldDismissed,
        UiWithheldSnoozed,
        MusicName,
        MusicSummary,
        MusicNoAudit,
        MusicEmpty,
        MusicLoudnessHeadline,
        MusicLoudnessDetail,
        MusicOtherLibraries,
        MusicScanOff,
        IssueLoudnessScanOff,
        IssueLoudnessIncomplete,
        IssueLoudnessDetail,
        MusicImagesName,
        MusicImagesSummary,
        MusicImagesNoAudit,
        MusicImagesNone,
        MusicImagesHeadline,
        MusicImagesDetail,
        MusicImagesClean,
        IssueTrackImages,
        IssueTrackImagesDetail,
        UiWithheldResolved,
        UiToggleFailed,
        UiStateSnoozed,
        UiStateDismissed,
        UiDismiss,
        UiSnooze7,
        UiRestore,
        UiDismissConfirm,
        UiIssueActionFailed,
        UiWithheldResolvedOnly,
        UiCatOperations,
        UiCatSystem,
        UiCatLibrary,
        UiCatMusic,
        UiCatPeople,
        StorageName,
        StorageSummary,
        UnitBytes,
        UnitKilobytes,
        UnitMegabytes,
        UnitGigabytes,
        UnitTerabytes,
        UnitPetabytes,
        ForecastName,
        ForecastSummary,
        ForecastCollecting,
        ForecastFilling,
        ForecastSteady,
        ForecastInconclusive,
        ForecastRate,
        ForecastNoDevices,
        IssueCapacityTitle,
        IssueCapacityDetail,
        ResourcesName,
        ResourcesSummary,
        ResourcesHeadline,
        ResourcesOfCores,
        ResourcesOfQuota,
        ResourcesMeasuring,
        ResourcesUnavailable,
        ResourcesCacheNote,
    ];
}
