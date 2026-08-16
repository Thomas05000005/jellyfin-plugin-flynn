using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Music;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// What the stored cover art card says, and what it refuses to say.
/// </summary>
public sealed class TrackImagesCardTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public async Task BeforeAnyAudit_TheCardSaysNothingHasRunRatherThanZeroBytes()
    {
        await using var fixture = await Fixture.CreateAsync();

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicImagesNoAudit, card.Headline.Key);
    }

    [Fact]
    public async Task WhenTheServerStoresNoCoverAtAll_TheCardSaysSo()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryTrackImages(Guid.NewGuid(), "Musique", 0, 0, 0, 0));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicImagesNone, card.Headline.Key);
        Assert.Equal(ModuleState.Healthy, card.State);
    }

    [Fact]
    public async Task WithCoversButNoDuplication_TheCardIsHealthyAndStillQuotesTheCost()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(
            new LibraryTrackImages(Guid.NewGuid(), "Musique", 500, 60L * 1024 * 1024, 0, 0));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicImagesClean, card.Headline.Key);
        Assert.Equal(500L, card.Headline.Args[0]);
        Assert.Equal(ModuleState.Healthy, card.State);
    }

    /// <summary>
    /// A hundred megabytes of duplication is real and still not worth interrupting anyone about.
    /// The card reports it; it does not colour the dashboard.
    /// </summary>
    [Fact]
    public async Task DuplicationBelowTheFloor_IsReportedWithoutBeingRaised()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryTrackImages(
            Guid.NewGuid(), "Musique", 900, 200L * 1024 * 1024, 800, 100L * 1024 * 1024));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);
        await fixture.Module.ReportIssuesAsync(fixture.Issues, CancellationToken.None);

        Assert.Equal(StringKeys.MusicImagesHeadline, card.Headline.Key);
        Assert.Equal(ModuleState.Healthy, card.State);
        Assert.Empty(await fixture.Issues.GetOpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DuplicationWorthGigabytes_IsDegradedAndRaisesOneIssuePerLibrary()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(
            new LibraryTrackImages(Guid.NewGuid(), "Musiques", 180_000, 22 * Gigabyte, 170_000, 20 * Gigabyte),
            new LibraryTrackImages(Guid.NewGuid(), "Musiques28", 400, 300L * 1024 * 1024, 10, 2L * 1024 * 1024));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);
        await fixture.Module.ReportIssuesAsync(fixture.Issues, CancellationToken.None);

        Assert.Equal(StringKeys.MusicImagesHeadline, card.Headline.Key);
        Assert.Equal(ModuleState.Degraded, card.State);

        // Only the library that is actually wasting gigabytes, so the issue names where to look.
        var issue = Assert.Single(await fixture.Issues.GetOpenAsync(CancellationToken.None));
        Assert.Equal(StringKeys.IssueTrackImages, issue.Issue.Title.Key);
        Assert.Equal("Musiques", issue.Issue.Title.Args[0]);
    }

    /// <summary>
    /// The one that would have bitten on a real server. Twenty-six gigabytes does not fit in a
    /// thirty-two bit integer, and a library this size is exactly where the figure matters.
    /// </summary>
    [Fact]
    public async Task ByteCountsBeyondTwoBillion_SurviveTheRoundTrip()
    {
        var huge = 26 * Gigabyte;
        Assert.True(huge > int.MaxValue);

        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(
            new LibraryTrackImages(Guid.NewGuid(), "Musiques", 223_412, huge, 183_000, huge - Gigabyte));

        var read = Assert.Single(await fixture.Repository.GetLatestImagesAsync(CancellationToken.None));

        Assert.Equal(huge, read.TotalBytes);
        Assert.Equal(huge - Gigabyte, read.RedundantBytes);
    }

    [Fact]
    public async Task WithTheDatabaseDown_TheCardSaysSoRatherThanReportingZero()
    {
        await using var fixture = await Fixture.CreateAsync(ready: false);

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.StorageUnavailable, card.Headline.Key);
        Assert.Equal(ModuleState.Degraded, card.State);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

        private FlynnDatabase _database = null!;

        public MusicRepository Repository { get; private set; } = null!;

        public TrackImagesModule Module { get; private set; } = null!;

        public IssueRegistry Issues { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(bool ready = true)
        {
            var fixture = new Fixture();
            fixture._database = new FlynnDatabase(Path.Combine(fixture._directory, "flynn.db"));
            await new SchemaMigrator(fixture._database, NullLogger<SchemaMigrator>.Instance)
                .MigrateAsync(Migrations.All, CancellationToken.None);

            var readiness = new DatabaseReadiness();
            if (ready)
            {
                readiness.MarkReady();
            }

            fixture.Repository = new MusicRepository(fixture._database);
            fixture.Issues = new IssueRegistry(fixture._database, TimeProvider.System);
            fixture.Module = new TrackImagesModule(
                fixture.Repository,
                readiness,
                TimeProvider.System,
                NullLogger<TrackImagesModule>.Instance);
            return fixture;
        }

        public Task SaveAsync(params LibraryTrackImages[] libraries) =>
            Repository.SaveImagesAsync(DateTimeOffset.UtcNow, libraries, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
