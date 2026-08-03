namespace Jellyfin.Plugin.Flynn.Core.Modules;

/// <summary>Health of a module, as rendered on its dashboard card.</summary>
public enum ModuleState
{
    /// <summary>The admin switched this module off. It consumes nothing and reports nothing.</summary>
    Disabled = 0,

    /// <summary>Working as intended.</summary>
    Healthy = 1,

    /// <summary>Running, but something is missing or stale. The card explains what.</summary>
    Degraded = 2,

    /// <summary>
    /// The module threw and was isolated. The rest of the suite is unaffected.
    /// A failed module never silently reports plausible numbers — that is worse than an error.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// What a module shows on the dashboard: a state, a headline number or phrase, and optional
/// detail. Cards are read-only summaries; anything actionable belongs to the issue registry.
/// </summary>
/// <param name="ModuleId">The reporting module's <see cref="IFlynnModule.Id"/>.</param>
/// <param name="State">Current health.</param>
/// <param name="Headline">The single most useful value, e.g. "12 days until full".</param>
/// <param name="Detail">Optional secondary line. Null when there is nothing to add.</param>
/// <param name="GeneratedAt">
/// When the underlying data was computed — not when the card was built. The UI shows this so a
/// stale panel is visibly stale instead of quietly wrong.
/// </param>
public sealed record ModuleCard(
    string ModuleId,
    ModuleState State,
    string Headline,
    string? Detail,
    DateTimeOffset GeneratedAt)
{
    /// <summary>Builds the card shown when a module threw while reporting.</summary>
    /// <param name="moduleId">The module that failed.</param>
    /// <param name="reason">Short, already-sanitised reason. Never include raw exception text.</param>
    /// <returns>A card in the <see cref="ModuleState.Failed"/> state.</returns>
    public static ModuleCard Failed(string moduleId, string reason) =>
        new(moduleId, ModuleState.Failed, "Unavailable", reason, DateTimeOffset.UtcNow);
}
