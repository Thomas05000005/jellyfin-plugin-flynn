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
    /// <summary>Admin page chrome: <c>ui.toggle-failed</c>.</summary>
    public const string UiToggleFailed = "ui.toggle-failed";

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
        UiWithheldResolved,
        UiToggleFailed,
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
    ];
}
