namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// Whether the database came up, and why not when it did not.
/// <para>
/// A failed migration must not take the server down, and must not be silent either. Modules that
/// need storage ask this first and report a card saying what is wrong, which is the difference
/// between a visibly broken panel and one quietly showing numbers from last week.
/// </para>
/// </summary>
public sealed class DatabaseReadiness
{
    private volatile string? _failure;
    private volatile bool _ready;

    /// <summary>Gets a value indicating whether the schema is up to date and usable.</summary>
    public bool IsReady => _ready;

    /// <summary>Gets a short, log-safe reason the database is unusable, or null when it is fine.</summary>
    public string? Failure => _failure;

    /// <summary>Records that the database is usable.</summary>
    internal void MarkReady()
    {
        _failure = null;
        _ready = true;
    }

    /// <summary>Records that the database could not be brought up.</summary>
    /// <param name="reason">Short reason, safe to show an admin. Never raw exception text.</param>
    internal void MarkFailed(string reason)
    {
        _failure = reason;
        _ready = false;
    }
}
