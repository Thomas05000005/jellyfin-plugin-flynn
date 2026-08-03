using Jellyfin.Plugin.Flynn.Core.Localization;

namespace Jellyfin.Plugin.Flynn.Core.Mutations;

/// <summary>How far a change reaches, and therefore how much it can cost when it is wrong.</summary>
public enum WriteLevel
{
    /// <summary>Changes nothing. Reports only.</summary>
    None = 0,

    /// <summary>
    /// Goes through the Jellyfin API: disabling a user, editing metadata, creating a collection.
    /// Recoverable, because the server keeps its own copy of what it owns.
    /// </summary>
    Server = 1,

    /// <summary>
    /// Touches files on disk. Where the value is, and where the damage is.
    /// <para>
    /// Two rules hold at this level and are enforced by <see cref="MutationKernel"/>: Flynn never
    /// renames or moves a media file, and anything at this level must produce an undo manifest
    /// before it runs.
    /// </para>
    /// </summary>
    Files = 2,
}

/// <summary>One concrete change a mutation intends to make, as shown in the preview.</summary>
/// <param name="Description">What will change, in the admin's language.</param>
/// <param name="Target">
/// What it applies to — a path, an item id, a user id. Shown verbatim, so it must be something the
/// admin recognises.
/// </param>
public sealed record MutationStep(LocalizedText Description, string Target);

/// <summary>
/// What a mutation would do, produced before anything happens.
/// <para>
/// A preview that is expensive to produce is a preview people skip, so building one must not be
/// meaningfully slower than applying the change.
/// </para>
/// </summary>
/// <param name="Steps">Every individual change, in the order they would be applied.</param>
/// <param name="Warnings">
/// Things the admin should know before saying yes. An empty list means the mutation believes it is
/// safe, which is a claim worth being careful with.
/// </param>
public sealed record MutationPreview(IReadOnlyList<MutationStep> Steps, IReadOnlyList<LocalizedText> Warnings)
{
    /// <summary>Gets a value indicating whether there is anything to do.</summary>
    public bool IsEmpty => Steps.Count == 0;
}

/// <summary>
/// A change Flynn can make, expressed so that it can be shown, applied and undone.
/// <para>
/// Modules never write directly. Everything goes through this shape, because a codebase where
/// some writes are previewable and some are not is one where nobody trusts the preview.
/// </para>
/// </summary>
public interface IMutation
{
    /// <summary>Gets the module this belongs to.</summary>
    string ModuleId { get; }

    /// <summary>Gets a stable identifier for the kind of change, lowercase kebab.</summary>
    string Kind { get; }

    /// <summary>Gets how far this change reaches.</summary>
    WriteLevel Level { get; }

    /// <summary>
    /// Works out what would change, without changing it.
    /// <para>
    /// Must have no side effects at all: this runs whenever the admin looks at the mutation, and
    /// something that half-applies while previewing is worse than no preview.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What would happen.</returns>
    Task<MutationPreview> PreviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Captures everything needed to reverse the change, <b>before</b> the change is made.
    /// <para>
    /// This ordering is the whole point. Producing the undo manifest as a by-product of applying
    /// means it can only be checked once the damage is done, and a mutation that returns an empty
    /// manifest is then discovered too late to refuse it. Capturing first turns "we cannot undo
    /// this" into a refusal instead of a regret.
    /// </para>
    /// <para>
    /// So this reads: original bytes, previous values, a copy set aside. It must not change
    /// anything itself.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One entry per step the mutation intends to apply. An empty list for a non-empty preview is
    /// a refusal at <see cref="WriteLevel.Files"/>.
    /// </returns>
    Task<IReadOnlyList<UndoEntry>> PrepareUndoAsync(CancellationToken cancellationToken);

    /// <summary>Applies the change. Called only after its undo manifest is safely stored.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the change is made.</returns>
    Task ApplyAsync(CancellationToken cancellationToken);

    /// <summary>Reverses a previously applied change from its stored manifest.</summary>
    /// <param name="entries">The manifest captured by <see cref="PrepareUndoAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the change is reversed.</returns>
    Task UndoAsync(IReadOnlyList<UndoEntry> entries, CancellationToken cancellationToken);
}

/// <summary>
/// What it takes to reverse one applied step.
/// <para>
/// Deliberately opaque to the kernel: only the mutation that produced it knows how to read it
/// back. The kernel's job is to make sure it exists and is stored before the change is considered
/// done.
/// </para>
/// </summary>
/// <param name="Target">What the step applied to.</param>
/// <param name="Payload">
/// Whatever the mutation needs to reverse this step, serialised. Original bytes, a previous value,
/// a path to a copy.
/// </param>
public sealed record UndoEntry(string Target, string Payload);
