using Jellyfin.Plugin.Flynn.Api;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Hiding an issue, and getting it back.
/// <para>
/// The endpoints existed, were exposed, and were tested at the registry level -- and could not
/// work, because the fingerprint was a route segment and every real fingerprint contains slashes.
/// Nobody noticed because the admin page never called them: two defects hiding each other, and
/// neither visible from the layer the other was tested at. These tests cross that layer.
/// </para>
/// </summary>
public sealed class InboxRoundTripTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-08-06T12:00:00Z", null));

    private FlynnDatabase _database = null!;
    private IssueRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        _database = new FlynnDatabase(Path.Combine(_directory, "flynn.db"));
        await new SchemaMigrator(_database, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(Migrations.All, CancellationToken.None);
        _registry = new IssueRegistry(_database, _clock);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The reason the fingerprint travels in the query string.
    /// <para>
    /// A real subject is a device identity, and on a pooled filesystem that is what
    /// <see cref="FilesystemIdentity"/> produces: <c>zfs:RAID-Z1</c>. The fingerprint wraps it as
    /// module/kind/subject, so it carries slashes and a colon. A <c>{fingerprint}</c> route segment
    /// cannot match that, and percent-encoding does not rescue it either -- the path is decoded
    /// before routing sees it, and the slashes come back as separators.
    /// </para>
    /// <para>
    /// If someone moves it back into the path, this fails.
    /// </para>
    /// </summary>
    [Fact]
    public void ARealFingerprint_CannotBeARouteSegment()
    {
        var device = new DeviceSnapshot("zfs:RAID-Z1", "/data/Films", 1, 1, "zfs");
        var issue = new Issue(
            "capacity",
            "capacity",
            device.DeviceId,
            IssueSeverity.Warning,
            LocalizedText.Of(StringKeys.IssueCapacityTitle, "RAID-Z1", 12),
            null);

        Assert.Equal("capacity/capacity/zfs:RAID-Z1", issue.Fingerprint);
        Assert.Contains("/", issue.Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The actual regression guard.
    /// <para>
    /// Every test below calls the controller methods directly, which is useful for behaviour and
    /// useless for routing -- none of them would have caught the original defect, because none of
    /// them goes through a route table. This one reads the attributes instead: no route template
    /// on this controller may carry a fingerprint, whatever the method does with it afterwards.
    /// </para>
    /// </summary>
    [Fact]
    public void NoRouteTemplate_TriesToCarryAFingerprint()
    {
        var offenders = typeof(DashboardController)
            .GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(HttpMethodAttribute), false)
                .Cast<HttpMethodAttribute>()
                .Select(a => (Method: m.Name, a.Template)))
            .Where(r => r.Template?.Contains("fingerprint", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A fingerprint contains slashes, so a route segment can never match one. "
            + "Pass it in the query string. Offending routes: "
            + string.Join(", ", offenders.Select(o => $"{o.Method} -> {o.Template}")));
    }

    [Fact]
    public async Task DismissingAnIssue_TakesItOutOfTheInboxAndCountsIt()
    {
        var issue = await SweepOne();
        var controller = Controller();

        Assert.IsType<NoContentResult>(await controller.DismissIssue(issue.Fingerprint, default));

        var inbox = await Inbox(controller);
        Assert.Empty(inbox.Open);
        Assert.Equal(1, inbox.Dismissed);
    }

    /// <summary>
    /// The half that makes a permanent hide acceptable. A count with no list is a slightly better
    /// blind spot, not a way back.
    /// </summary>
    [Fact]
    public async Task ADismissedIssue_IsStillListedSoItCanBeBroughtBack()
    {
        var issue = await SweepOne();
        var controller = Controller();
        await controller.DismissIssue(issue.Fingerprint, default);

        var inbox = await Inbox(controller);

        var withheld = Assert.Single(inbox.Withheld);
        Assert.Equal(issue.Fingerprint, withheld.Fingerprint);
        Assert.Equal("Dismissed", withheld.State);
    }

    [Fact]
    public async Task RestoringADismissedIssue_PutsItBack()
    {
        var issue = await SweepOne();
        var controller = Controller();
        await controller.DismissIssue(issue.Fingerprint, default);

        Assert.IsType<NoContentResult>(await controller.RestoreIssue(issue.Fingerprint, default));

        var inbox = await Inbox(controller);
        Assert.Single(inbox.Open);
        Assert.Empty(inbox.Withheld);
        Assert.Equal(0, inbox.Dismissed);
    }

    /// <summary>Dismissal has to outlive the sweep, or it means nothing after the next night.</summary>
    [Fact]
    public async Task ADismissedIssue_StaysDismissedWhenDetectedAgain()
    {
        var issue = await SweepOne();
        var controller = Controller();
        await controller.DismissIssue(issue.Fingerprint, default);

        _clock.Advance(TimeSpan.FromDays(1));
        await SweepOne();

        var inbox = await Inbox(controller);
        Assert.Empty(inbox.Open);
        Assert.Equal(1, inbox.Dismissed);
    }

    [Fact]
    public async Task ASnoozedIssue_ComesBackOnItsOwnWhenTheTimeIsUp()
    {
        var issue = await SweepOne();
        var controller = Controller();

        Assert.IsType<NoContentResult>(await controller.SnoozeIssue(issue.Fingerprint, 7, default));
        Assert.Empty((await Inbox(controller)).Open);

        _clock.Advance(TimeSpan.FromDays(8));

        var inbox = await Inbox(controller);
        Assert.Single(inbox.Open);
        // Nothing is being withheld any more, so nothing to bring back.
        Assert.Empty(inbox.Withheld);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-1)]
    public async Task ASnoozeOutsideTheAllowedRange_IsRefused(int days)
    {
        var issue = await SweepOne();

        var result = await Controller().SnoozeIssue(issue.Fingerprint, days, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("capacity/capacity/does-not-exist")]
    public async Task AnUnknownFingerprint_IsNotFoundRatherThanSilentlyAccepted(string fingerprint)
    {
        await SweepOne();
        var controller = Controller();

        Assert.IsType<NotFoundResult>(await controller.DismissIssue(fingerprint, default));
        Assert.IsType<NotFoundResult>(await controller.RestoreIssue(fingerprint, default));
        Assert.IsType<NotFoundResult>(await controller.SnoozeIssue(fingerprint, 7, default));
    }

    /// <summary>
    /// An expired snooze is open again, and the counts have to agree with the list. They did not:
    /// the list promoted it and the tally read the stored value, so the same issue sat in the inbox
    /// and in the "hidden" count printed underneath it.
    /// </summary>
    [Fact]
    public async Task AnExpiredSnooze_IsNotCountedAsHiddenWhileItIsAlsoInTheInbox()
    {
        var issue = await SweepOne();
        var controller = Controller();
        await controller.SnoozeIssue(issue.Fingerprint, 7, default);

        _clock.Advance(TimeSpan.FromDays(8));

        var inbox = await Inbox(controller);
        Assert.Single(inbox.Open);
        Assert.Equal(0, inbox.Snoozed);
    }

    /// <summary>
    /// Dismissing something that already closed itself used to answer 204 and add it to the
    /// dismissed tally -- inventing a decision nobody made, in the count that exists to record
    /// exactly those.
    /// </summary>
    [Fact]
    public async Task AnIssueThatClosedItself_CannotBeDismissed()
    {
        var issue = await SweepOne();
        await _registry.ReconcileAsync("capacity", "capacity", [], CancellationToken.None);
        var controller = Controller();

        Assert.IsType<NotFoundResult>(await controller.DismissIssue(issue.Fingerprint, default));

        var inbox = await Inbox(controller);
        Assert.Equal(0, inbox.Dismissed);
        Assert.Equal(1, inbox.Resolved);
    }

    /// <summary>
    /// The read answered a clean 503 while the three actions beside it went at a database that was
    /// not there.
    /// </summary>
    [Fact]
    public async Task WithTheDatabaseDown_EveryActionSaysSoRatherThanThrowing()
    {
        var issue = await SweepOne();
        var down = new DatabaseReadiness();
        down.MarkFailed("the database could not be opened");
        var controller = new DashboardController(
            new ModuleRegistry([], NullLogger<ModuleRegistry>.Instance), _registry, null!, down)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        foreach (var result in new[]
        {
            await controller.DismissIssue(issue.Fingerprint, default),
            await controller.RestoreIssue(issue.Fingerprint, default),
            await controller.SnoozeIssue(issue.Fingerprint, 7, default),
        })
        {
            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        }
    }

    /// <summary>A page that says "hidden" without saying until when is telling half the truth.</summary>
    [Fact]
    public async Task ASnoozedIssue_CarriesItsExpiryToThePage()
    {
        var issue = await SweepOne();
        var controller = Controller();
        await controller.SnoozeIssue(issue.Fingerprint, 7, default);

        var withheld = Assert.Single((await Inbox(controller)).Withheld);

        Assert.Equal("Snoozed", withheld.State);
        Assert.NotNull(withheld.SnoozedUntil);
        Assert.True(withheld.SnoozedUntil > _clock.GetUtcNow());
    }

    /// <summary>
    /// An issue that closed itself is not something anybody hid, so it does not belong in a list of
    /// decisions to reconsider -- it would bury the ones that were.
    /// </summary>
    [Fact]
    public async Task AResolvedIssue_IsCountedButNotOfferedForRestoring()
    {
        await SweepOne();
        await _registry.ReconcileAsync("capacity", "capacity", [], CancellationToken.None);

        var inbox = await Inbox(Controller());

        Assert.Empty(inbox.Open);
        Assert.Empty(inbox.Withheld);
        Assert.Equal(1, inbox.Resolved);
    }

    private async Task<Issue> SweepOne()
    {
        var issue = new Issue(
            "capacity",
            "capacity",
            "zfs:RAID-Z1",
            IssueSeverity.Warning,
            LocalizedText.Of(StringKeys.IssueCapacityTitle, "RAID-Z1", 12),
            null);

        await _registry.ReconcileAsync("capacity", "capacity", [issue], CancellationToken.None);
        return issue;
    }

    // The inbox endpoints touch neither the module registry nor the config store, so an empty
    // registry is honest here: passing a populated one would suggest a coupling that is not there.
    // A request is needed though -- the controller resolves text for the caller's Accept-Language,
    // and reaching for a header on a controller with no HttpContext is a null reference.
    private DashboardController Controller() =>
        new(
            new ModuleRegistry([], NullLogger<ModuleRegistry>.Instance),
            _registry,
            null!,
            Ready())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static DatabaseReadiness Ready()
    {
        var readiness = new DatabaseReadiness();
        readiness.MarkReady();
        return readiness;
    }

    private static async Task<InboxDto> Inbox(DashboardController controller)
    {
        var result = await controller.GetIssues(CancellationToken.None);
        return Assert.IsType<InboxDto>(result.Value);
    }
}
