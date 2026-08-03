namespace Jellyfin.Plugin.Flynn.Core.Issues;

/// <summary>How loudly an issue asks to be dealt with.</summary>
public enum IssueSeverity
{
    /// <summary>Worth knowing, not worth acting on today.</summary>
    Info = 0,

    /// <summary>Will become a problem if left alone.</summary>
    Warning = 1,

    /// <summary>Something is already broken or about to break.</summary>
    Critical = 2,
}

/// <summary>Where an issue stands with the admin.</summary>
public enum IssueState
{
    /// <summary>Detected and waiting.</summary>
    Open = 0,

    /// <summary>Hidden until a date, then it comes back on its own.</summary>
    Snoozed = 1,

    /// <summary>
    /// The admin does not want to hear about this one again. Sticky: re-detecting it must not
    /// reopen it, or dismissal would mean nothing after the next nightly sweep.
    /// </summary>
    Dismissed = 2,

    /// <summary>No longer detected. Closed by the registry, not by the admin.</summary>
    Resolved = 3,
}

/// <summary>
/// Something a module found that the admin may want to act on.
/// <para>
/// Modules report facts; the registry owns state. That is why there is no state field here — a
/// module cannot know whether the admin already dismissed this, and should not have to.
/// </para>
/// </summary>
/// <param name="ModuleId">The reporting module's <c>IFlynnModule.Id</c>.</param>
/// <param name="Kind">
/// The family of problem, lowercase kebab, e.g. <c>orphaned-metadata</c>. It scopes reconciliation:
/// a sweep resolves only the issues of the same module and kind, so one module's sweep can never
/// close another's findings.
/// </param>
/// <param name="Subject">
/// What the issue is about, stable across runs — a library id, a file path, a device id. Together
/// with module and kind it forms the identity used to recognise this issue next time.
/// </param>
/// <param name="Severity">How urgent.</param>
/// <param name="Title">One line, shown in the inbox.</param>
/// <param name="Detail">Optional longer explanation. Null when the title says it all.</param>
public sealed record Issue(
    string ModuleId,
    string Kind,
    string Subject,
    IssueSeverity Severity,
    string Title,
    string? Detail)
{
    /// <summary>
    /// Gets the stable identity of this issue.
    /// <para>
    /// Recomputed from module, kind and subject rather than stored, so the same finding on two
    /// consecutive nights is recognised as one issue instead of piling up duplicates. Change how
    /// a module builds its <see cref="Subject"/> and every issue of that kind is treated as new.
    /// </para>
    /// </summary>
    public string Fingerprint => $"{ModuleId}/{Kind}/{Subject}";
}

/// <summary>An issue as the registry knows it: the reported facts plus the state it has acquired.</summary>
/// <param name="Issue">What the module reported, at its most recent sighting.</param>
/// <param name="State">Where it stands.</param>
/// <param name="FirstSeen">When it was first detected.</param>
/// <param name="LastSeen">When it was last detected.</param>
/// <param name="SnoozedUntil">When a snooze expires, or null.</param>
public sealed record TrackedIssue(
    Issue Issue,
    IssueState State,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset? SnoozedUntil);
