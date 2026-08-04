namespace Jellyfin.Plugin.Flynn.Core.Modules;

/// <summary>
/// One self-contained feature area of the suite (maintenance, storage, forecast, ...).
/// <para>
/// Modules are registered in DI at startup and can be switched off individually by the admin.
/// A module must never assume another module is present or enabled.
/// </para>
/// </summary>
public interface IFlynnModule
{
    /// <summary>
    /// Gets the stable machine identifier: lowercase kebab-case, e.g. <c>storage</c>.
    /// <para>
    /// It is the persisted config key and the API route segment, so renaming it orphans every
    /// existing installation's settings. Choose it once.
    /// </para>
    /// </summary>
    string Id { get; }

    /// <summary>Gets the name shown on the module's dashboard card.</summary>
    string DisplayName { get; }

    /// <summary>Gets the one-line description shown under <see cref="DisplayName"/>.</summary>
    string Summary { get; }

    /// <summary>Gets the shelf this module belongs on, which is how the dashboard groups it.</summary>
    ModuleCategory Category { get; }

    /// <summary>
    /// Gets a value indicating whether this module is on before the admin has touched anything.
    /// <para>
    /// A module the admin has never configured falls back to this rather than to off. Defaulting
    /// everything to off ships a plugin that does nothing on a fresh install, which reads as
    /// broken rather than as cautious.
    /// </para>
    /// <para>
    /// True for anything that only reads. Anything that could write starts false regardless, since
    /// the write level gates it as well.
    /// </para>
    /// </summary>
    bool EnabledByDefault { get; }

    /// <summary>
    /// Builds the module's dashboard card.
    /// <para>
    /// Called on page load, so it must be cheap: read pre-computed values, never scan the library
    /// or query an external service here. Throwing is survivable — the registry degrades this one
    /// card and leaves the rest of the suite running — but it is still a bug.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The card to render for this module.</returns>
    Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken);
}
