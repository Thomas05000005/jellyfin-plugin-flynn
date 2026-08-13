using Jellyfin.Plugin.Flynn.Configuration;
using Jellyfin.Plugin.Flynn.Core.Config;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Flynn.Api;

/// <summary>
/// What the admin page reads: one card per module, and the issue inbox.
/// <para>
/// Text is resolved here rather than where it was produced, so the answer is in the reader's
/// language instead of whatever culture the nightly task happened to run under.
/// </para>
/// </summary>
[ApiController]
[Route("Flynn")]
[Authorize(Policy = "RequiresElevation")]
[Produces("application/json")]
public sealed class DashboardController : ControllerBase
{
    private readonly ModuleRegistry _modules;
    private readonly IssueRegistry _issues;
    private readonly ConfigStore _config;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _clock;

    /// <summary>Initializes a new instance of the <see cref="DashboardController"/> class.</summary>
    /// <param name="modules">The module registry.</param>
    /// <param name="issues">The issue registry.</param>
    /// <param name="config">The configuration store.</param>
    /// <param name="readiness">Whether storage came up.</param>
    /// <param name="clock">
    /// Time source. The same one the registry compares against, which is the point: computing a
    /// snooze deadline from the wall clock while the registry decides expiry from an injected one
    /// leaves the two disagreeing, and a test that moves time cannot see its own snooze expire.
    /// </param>
    public DashboardController(
        ModuleRegistry modules,
        IssueRegistry issues,
        ConfigStore config,
        DatabaseReadiness readiness,
        TimeProvider clock)
    {
        _modules = modules;
        _issues = issues;
        _config = config;
        _readiness = readiness;
        _clock = clock;
    }

    /// <summary>Returns one card per registered module.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dashboard.</returns>
    [HttpGet("modules")]
    public async Task<ActionResult<DashboardDto>> GetModules(CancellationToken cancellationToken)
    {
        var strings = FlynnStrings.ForCulture(RequestCulture.For(Request));
        var enabled = _config.Current.Modules;

        // A module the admin has never touched falls back to its own default, not to off. The
        // absence of a saved preference is "not asked yet", not "asked and declined".
        var cards = await _modules.BuildCardsAsync(
            id => enabled.FirstOrDefault(m => m.Id == id)?.Enabled
                  ?? _modules.Modules.First(m => m.Id == id).EnabledByDefault,
            cancellationToken).ConfigureAwait(false);

        var dtos = cards.Select(card =>
        {
            var module = _modules.Modules.First(m => m.Id == card.ModuleId);
            return new ModuleCardDto(
                card.ModuleId,
                strings.Get(module.NameKey),
                strings.Get(module.SummaryKey),
                card.State.ToString(),
                module.Category.ToString(),
                card.State != ModuleState.Disabled,
                card.Headline.Resolve(strings),
                card.Detail?.Resolve(strings),
                card.GeneratedAt);
        }).ToList();

        return new DashboardDto(dtos, _readiness.IsReady, _readiness.Failure);
    }

    /// <summary>
    /// Returns the issues asking for attention, plus how many are being withheld.
    /// <para>
    /// The counts are not decoration. Dismissal is permanent, and a permanent hide with no visible
    /// trace is how a real problem stays hidden for a year.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inbox.</returns>
    [HttpGet("issues")]
    public async Task<ActionResult<InboxDto>> GetIssues(CancellationToken cancellationToken)
    {
        var down = StorageDown();
        if (down is not null)
        {
            return down;
        }

        var strings = FlynnStrings.ForCulture(RequestCulture.For(Request));
        var open = await _issues.GetOpenAsync(cancellationToken).ConfigureAwait(false);
        var withheld = await _issues.GetWithheldAsync(cancellationToken).ConfigureAwait(false);
        var counts = await _issues.CountByStateAsync(cancellationToken).ConfigureAwait(false);

        IssueDto ToDto(TrackedIssue tracked) => new(
            tracked.Issue.Fingerprint,
            tracked.Issue.ModuleId,
            tracked.Issue.Severity.ToString(),
            tracked.Issue.Title.Resolve(strings),
            tracked.Issue.Detail?.Resolve(strings),
            tracked.FirstSeen,
            tracked.LastSeen,
            tracked.State.ToString(),
            tracked.SnoozedUntil);

        return new InboxDto(
            open.Select(ToDto).ToList(),
            withheld.Select(ToDto).ToList(),
            counts.GetValueOrDefault(IssueState.Dismissed),
            counts.GetValueOrDefault(IssueState.Snoozed),
            counts.GetValueOrDefault(IssueState.Resolved));
    }

    /// <summary>
    /// Returns every user-facing string, resolved for the caller's language.
    /// <para>
    /// The admin page has chrome of its own -- section titles, relative times, state labels -- and
    /// hard-coding it in the script would leave half the page in English while the other half
    /// followed the reader. One catalogue, one source of truth, resolved in one place.
    /// </para>
    /// </summary>
    /// <returns>Key to text, for this reader.</returns>
    [HttpGet("strings")]
    public ActionResult<IReadOnlyDictionary<string, string>> GetStrings()
    {
        var strings = FlynnStrings.ForCulture(RequestCulture.For(Request));
        // The lambda is explicit because Get takes params, so a method group binds to the
        // comparer overload of ToDictionary instead of the selector.
        return StringKeys.All.ToDictionary(key => key, key => strings.Get(key));
    }

    /// <summary>
    /// Switches a module on or off.
    /// <para>
    /// Goes through the ConfigStore rather than touching the configuration object, so two admins
    /// toggling different modules at the same time cannot lose each other's change.
    /// </para>
    /// </summary>
    /// <param name="moduleId">The module's id.</param>
    /// <param name="enabled">Whether it should run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, 404 for an unknown module, or 400 when the write was refused.</returns>
    [HttpPost("modules/{moduleId}/enabled")]
    public async Task<ActionResult> SetModuleEnabled(
        string moduleId,
        [FromQuery] bool enabled,
        CancellationToken cancellationToken)
    {
        if (_modules.Modules.All(m => m.Id != moduleId))
        {
            return NotFound();
        }

        try
        {
            await _config.MutateAsync(
                config =>
                {
                    var toggle = config.Modules.FirstOrDefault(m => m.Id == moduleId);
                    if (toggle is null)
                    {
                        config.Modules.Add(new ModuleToggle { Id = moduleId, Enabled = enabled });
                    }
                    else
                    {
                        toggle.Enabled = enabled;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConfigurationRejectedException rejected)
        {
            // The store rehearsed the write and found the result would not survive a round trip.
            // That is the caller being told no, not the server falling over, and a 500 would send
            // the page down the "unreachable" path instead of showing the reason.
            return BadRequest(new ProblemDetails { Title = rejected.Message });
        }

        return NoContent();
    }

    /// <summary>
    /// Stops an issue from ever coming back on its own.
    /// <para>
    /// The fingerprint travels in the query string, never in the path. It is built as
    /// <c>module/kind/subject</c> and the subject is whatever the module names -- a device id like
    /// <c>zfs:RAID-Z1</c>, an album, a path. Those contain slashes, so a <c>{fingerprint}</c> route
    /// segment cannot match one, and percent-encoding does not save it either.
    /// </para>
    /// </summary>
    /// <param name="fingerprint">The issue's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or 404 when the fingerprint is unknown.</returns>
    [HttpPost("issues/dismiss")]
    public async Task<ActionResult> DismissIssue(
        [FromQuery] string fingerprint,
        CancellationToken cancellationToken) =>
        StorageDown()
        ?? (await _issues.DismissAsync(fingerprint, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : NotFound());

    /// <summary>Hides an issue for a while.</summary>
    /// <param name="fingerprint">The issue's identity. In the query string, see DismissIssue.</param>
    /// <param name="days">How many days to hide it for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or 404 when the fingerprint is unknown.</returns>
    [HttpPost("issues/snooze")]
    public async Task<ActionResult> SnoozeIssue(
        [FromQuery] string fingerprint,
        [FromQuery] int days,
        CancellationToken cancellationToken)
    {
        if (days is < 1 or > 365)
        {
            return BadRequest(new ProblemDetails { Title = "Snooze must be between 1 and 365 days." });
        }

        var down = StorageDown();
        if (down is not null)
        {
            return down;
        }

        return await _issues.SnoozeAsync(fingerprint, _clock.GetUtcNow().AddDays(days), cancellationToken)
            .ConfigureAwait(false)
            ? NoContent()
            : NotFound();
    }

    /// <summary>Brings a dismissed or snoozed issue back.</summary>
    /// <param name="fingerprint">The issue's identity. In the query string, see DismissIssue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or 404 when the fingerprint is unknown.</returns>
    [HttpPost("issues/restore")]
    public async Task<ActionResult> RestoreIssue(
        [FromQuery] string fingerprint,
        CancellationToken cancellationToken) =>
        StorageDown()
        ?? (await _issues.RestoreAsync(fingerprint, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : NotFound());

    /// <summary>
    /// The 503 every endpoint that touches the database owes the caller, or null when it is up.
    /// <para>
    /// Shared rather than repeated, because it was repeated once and then not: the read said 503
    /// with a reason while the three actions beside it went straight at a database that was not
    /// there. DatabaseReadiness exists to make that answerable; not asking it is the whole defect.
    /// </para>
    /// </summary>
    /// <returns>A 503 result, or null.</returns>
    private ActionResult? StorageDown() =>
        _readiness.IsReady
            ? null
            : StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = _readiness.Failure ?? "Storage is unavailable." });
}

/// <summary>The dashboard, as the admin page receives it.</summary>
/// <param name="Modules">One card per registered module.</param>
/// <param name="StorageReady">Whether Flynn's own database came up.</param>
/// <param name="StorageFailure">Why it did not, when it did not.</param>
public sealed record DashboardDto(
    IReadOnlyList<ModuleCardDto> Modules,
    bool StorageReady,
    string? StorageFailure);

/// <summary>One module's card, with its text already in the reader's language.</summary>
/// <param name="Id">Module id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Summary">One line about what the module does.</param>
/// <param name="State">Disabled, Healthy, Degraded or Failed.</param>
/// <param name="Category">Which shelf the dashboard groups it under.</param>
/// <param name="Enabled">Whether the module is switched on. Drives the toggle in the UI.</param>
/// <param name="Headline">The single most useful value.</param>
/// <param name="Detail">Secondary line, if any.</param>
/// <param name="GeneratedAt">When the underlying data was computed.</param>
public sealed record ModuleCardDto(
    string Id,
    string Name,
    string Summary,
    string State,
    string Category,
    bool Enabled,
    string Headline,
    string? Detail,
    DateTimeOffset GeneratedAt);

/// <summary>The issue inbox.</summary>
/// <param name="Open">Issues asking for attention, worst first.</param>
/// <param name="Withheld">
/// The dismissed and still-snoozed ones. Sent so a hide can be undone: a count on its own tells
/// the admin that something is hidden without letting them find out what, which is only a slightly
/// better blind spot than hiding it silently.
/// </param>
/// <param name="Dismissed">How many the admin chose never to see again.</param>
/// <param name="Snoozed">How many are hidden until a date.</param>
/// <param name="Resolved">How many closed themselves.</param>
public sealed record InboxDto(
    IReadOnlyList<IssueDto> Open,
    IReadOnlyList<IssueDto> Withheld,
    int Dismissed,
    int Snoozed,
    int Resolved);

/// <summary>One issue, with its text already in the reader's language.</summary>
/// <param name="Fingerprint">
/// Stable identity, passed to the dismiss, snooze and restore endpoints in the query string. It
/// contains slashes, so it can never be a route segment.
/// </param>
/// <param name="ModuleId">Which module found it.</param>
/// <param name="Severity">Info, Warning or Critical.</param>
/// <param name="Title">One line.</param>
/// <param name="Detail">Longer explanation, if any.</param>
/// <param name="FirstSeen">When it was first detected.</param>
/// <param name="LastSeen">When it was last detected.</param>
/// <param name="State">Open, Snoozed or Dismissed, so the page can say why it is not in the inbox.</param>
/// <param name="SnoozedUntil">
/// When a snooze runs out, so the page can say "hidden until Tuesday" rather than just "hidden".
/// Null for anything that is not snoozed.
/// </param>
public sealed record IssueDto(
    string Fingerprint,
    string ModuleId,
    string Severity,
    string Title,
    string? Detail,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string State,
    DateTimeOffset? SnoozedUntil);
