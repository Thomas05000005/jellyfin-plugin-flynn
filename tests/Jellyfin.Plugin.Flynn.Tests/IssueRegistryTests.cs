using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The behaviours that decide whether the inbox stays useful: problems that go away close
/// themselves, the same finding two nights running is one entry, and a dismissal actually holds.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class IssueRegistryTests : IAsyncLifetime
{
    private const string Module = "storage";
    private const string Kind = "low-space";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-08-03T12:00:00Z", null));

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

    [Fact]
    public async Task ASweep_RecordsWhatItFound()
    {
        await Sweep(Problem("/mnt/films"), Problem("/mnt/music"));

        var open = await _registry.GetOpenAsync(CancellationToken.None);

        Assert.Equal(2, open.Count);
        Assert.Contains(open, i => i.Issue.Subject == "/mnt/films");
    }

    /// <summary>
    /// The same finding on two consecutive nights is one entry, not two. Without a stable identity
    /// a nightly sweep turns the inbox into a log.
    /// </summary>
    [Fact]
    public async Task TheSameFindingTwice_IsOneIssue()
    {
        await Sweep(Problem("/mnt/films"));
        var firstSeen = (await _registry.GetOpenAsync(CancellationToken.None))[0].FirstSeen;

        _clock.Advance(TimeSpan.FromDays(1));
        await Sweep(Problem("/mnt/films"));

        var open = Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));
        Assert.Equal(firstSeen, open.FirstSeen);
        Assert.True(open.LastSeen > firstSeen, "the second sighting should move last_seen");
    }

    /// <summary>
    /// The reason sweeps exist. A module that stops reporting something must not have to remember
    /// to close it.
    /// </summary>
    [Fact]
    public async Task AProblemThatGoesAway_ClosesItself()
    {
        await Sweep(Problem("/mnt/films"), Problem("/mnt/music"));

        await Sweep(Problem("/mnt/films"));

        var open = Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));
        Assert.Equal("/mnt/films", open.Issue.Subject);
    }

    [Fact]
    public async Task AnEmptySweep_ClearsTheScope()
    {
        await Sweep(Problem("/mnt/films"), Problem("/mnt/music"));

        await Sweep();

        Assert.Empty(await _registry.GetOpenAsync(CancellationToken.None));
    }

    /// <summary>Scoping: one module's sweep must not close another's findings.</summary>
    [Fact]
    public async Task ASweep_OnlyClosesItsOwnScope()
    {
        await Sweep(Problem("/mnt/films"));
        await _registry.ReconcileAsync(
            "resources",
            "cpu-throttling",
            [new Issue("resources", "cpu-throttling", "host", IssueSeverity.Warning, LocalizedText.Of("k"), null)],
            CancellationToken.None);

        await Sweep();

        var open = Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));
        Assert.Equal("resources", open.Issue.ModuleId);
    }

    [Fact]
    public async Task ASweepCarryingAnotherScope_IsRefused()
    {
        var foreign = new Issue("resources", "cpu", "host", IssueSeverity.Info, LocalizedText.Of("k"), null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _registry.ReconcileAsync(Module, Kind, [foreign], CancellationToken.None));
    }

    /// <summary>
    /// The subtle one. Dismissal has to survive re-detection, or it means nothing after the next
    /// nightly run.
    /// </summary>
    [Fact]
    public async Task ADismissedIssue_StaysDismissedWhenDetectedAgain()
    {
        await Sweep(Problem("/mnt/films"));
        var fingerprint = (await _registry.GetOpenAsync(CancellationToken.None))[0].Issue.Fingerprint;
        Assert.True(await _registry.DismissAsync(fingerprint, CancellationToken.None));

        _clock.Advance(TimeSpan.FromDays(1));
        await Sweep(Problem("/mnt/films"));

        Assert.Empty(await _registry.GetOpenAsync(CancellationToken.None));
        var counts = await _registry.CountByStateAsync(CancellationToken.None);
        Assert.Equal(1, counts[IssueState.Dismissed]);
    }

    /// <summary>
    /// Dismissal is permanent but counted, so the pile is visible as a number with a way back
    /// rather than becoming a blind spot.
    /// </summary>
    [Fact]
    public async Task ADismissedIssue_StaysCountedAndCanBeRestored()
    {
        await Sweep(Problem("/mnt/films"));
        var fingerprint = (await _registry.GetOpenAsync(CancellationToken.None))[0].Issue.Fingerprint;
        await _registry.DismissAsync(fingerprint, CancellationToken.None);

        Assert.Equal(1, (await _registry.CountByStateAsync(CancellationToken.None))[IssueState.Dismissed]);

        Assert.True(await _registry.RestoreAsync(fingerprint, CancellationToken.None));
        Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));
    }

    /// <summary>A dismissed issue must not be swept into Resolved, which would drop it out of the count.</summary>
    [Fact]
    public async Task ADismissedIssue_IsNotResolvedByASweepThatNoLongerSeesIt()
    {
        await Sweep(Problem("/mnt/films"));
        var fingerprint = (await _registry.GetOpenAsync(CancellationToken.None))[0].Issue.Fingerprint;
        await _registry.DismissAsync(fingerprint, CancellationToken.None);

        await Sweep();

        var counts = await _registry.CountByStateAsync(CancellationToken.None);
        Assert.Equal(1, counts[IssueState.Dismissed]);
        Assert.False(counts.ContainsKey(IssueState.Resolved));
    }

    [Fact]
    public async Task ASnoozedIssue_ComesBackOnItsOwnAfterTheDate()
    {
        await Sweep(Problem("/mnt/films"));
        var fingerprint = (await _registry.GetOpenAsync(CancellationToken.None))[0].Issue.Fingerprint;
        await _registry.SnoozeAsync(fingerprint, _clock.GetUtcNow().AddDays(7), CancellationToken.None);

        Assert.Empty(await _registry.GetOpenAsync(CancellationToken.None));

        _clock.Advance(TimeSpan.FromDays(8));

        // No sweep needed: the date passing is enough.
        Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AResolvedIssueThatComesBack_Reopens()
    {
        await Sweep(Problem("/mnt/films"));
        await Sweep();
        Assert.Empty(await _registry.GetOpenAsync(CancellationToken.None));

        await Sweep(Problem("/mnt/films"));

        Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));
    }

    /// <summary>
    /// Arguments survive as numbers rather than as pre-formatted text, so the reader's culture
    /// decides the separators at render time instead of the nightly task's.
    /// </summary>
    [Fact]
    public async Task NumericArguments_SurviveAsNumbers()
    {
        var issue = new Issue(
            Module,
            Kind,
            "/mnt/films",
            IssueSeverity.Warning,
            LocalizedText.Of("issue.storage.low-space.title", 1234567L, "/mnt/films"),
            null);
        await _registry.ReconcileAsync(Module, Kind, [issue], CancellationToken.None);

        var stored = Assert.Single(await _registry.GetOpenAsync(CancellationToken.None));

        Assert.Equal(1234567L, Assert.IsType<long>(stored.Issue.Title.Args[0]));
        Assert.Equal("/mnt/films", stored.Issue.Title.Args[1]);
    }

    [Fact]
    public async Task OpenIssues_ComeBackWorstFirst()
    {
        await _registry.ReconcileAsync(
            Module,
            Kind,
            [
                Problem("/mnt/a", IssueSeverity.Info),
                Problem("/mnt/b", IssueSeverity.Critical),
                Problem("/mnt/c", IssueSeverity.Warning),
            ],
            CancellationToken.None);

        var open = await _registry.GetOpenAsync(CancellationToken.None);

        Assert.Equal(
            [IssueSeverity.Critical, IssueSeverity.Warning, IssueSeverity.Info],
            open.Select(i => i.Issue.Severity));
    }

    [Fact]
    public async Task ActingOnAnUnknownFingerprint_ReportsThatNothingHappened()
    {
        Assert.False(await _registry.DismissAsync("nope/none/none", CancellationToken.None));
    }

    private static Issue Problem(string subject, IssueSeverity severity = IssueSeverity.Warning) =>
        new(Module, Kind, subject, severity, LocalizedText.Of("issue.storage.low-space.title", subject), null);

    private Task Sweep(params Issue[] found) =>
        _registry.ReconcileAsync(Module, Kind, found, CancellationToken.None);

}
