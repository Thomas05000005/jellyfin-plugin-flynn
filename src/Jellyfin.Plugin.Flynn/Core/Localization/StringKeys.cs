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

    /// <summary>Gets every key defined above, for catalogue completeness checks.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ModuleUnavailableHeadline,
        ModuleUnavailableDetail,
        ModuleTimedOutDetail,
        ModuleDisabledHeadline,
        ConfigRejected,
    ];
}
