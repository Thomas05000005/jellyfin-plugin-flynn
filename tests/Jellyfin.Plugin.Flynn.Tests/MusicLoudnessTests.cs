using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Music;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The loudness audit, and mainly the difference between a fault and a setting.
/// <para>
/// A music library with no loudness values is the normal state of an untouched Jellyfin: the
/// measuring task only visits libraries whose EnableLUFSScan is set, and that property has no
/// initialiser. Reporting that as a problem would make the first thing this module ever says
/// wrong, and the second thing it says ignored.
/// </para>
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class MusicLoudnessTests
{
    [Fact]
    public void ATrackWhoseTagWasRead_CountsOnceAndIsNotAlsoCountedAsMeasured()
    {
        // The server never measures a track that already has a gain, so counting both would report
        // more covered tracks than the library holds.
        var audit = AuditOver(WithTag(-7.2f), WithTag(-9.1f), Measured(-11.0f), Nothing());

        var library = Assert.Single(audit.Run(CancellationToken.None));

        Assert.Equal(4, library.Tracks);
        Assert.Equal(2, library.FromTag);
        Assert.Equal(1, library.Measured);
        Assert.Equal(3, library.Covered);
        Assert.Equal(1, library.Missing);
    }

    /// <summary>
    /// A track carrying both is counted as a tag, because that is the one the server would have
    /// used and the one that explains why it never measured it.
    /// </summary>
    [Fact]
    public void ATrackCarryingBoth_IsCountedOnce()
    {
        var both = WithTag(-7.0f);
        both.LUFS = -14.0f;

        var library = Assert.Single(AuditOver(both).Run(CancellationToken.None));

        Assert.Equal(1, library.Covered);
        Assert.Equal(1, library.FromTag);
        Assert.Equal(0, library.Measured);
    }

    [Theory]
    [InlineData(true, 10, 10, LoudnessGap.None)]
    [InlineData(true, 10, 4, LoudnessGap.NotMeasuredYet)]
    [InlineData(false, 10, 4, LoudnessGap.ScanDisabled)]
    [InlineData(false, 10, 10, LoudnessGap.None)]
    public void TheGapNamesWhyRatherThanJustThatSomethingIsMissing(
        bool scanEnabled, int tracks, int covered, LoudnessGap expected)
    {
        var library = new LibraryLoudness(Guid.NewGuid(), "Musique", scanEnabled, tracks, covered, 0);

        Assert.Equal(expected, library.Gap);
    }

    [Fact]
    public void AnEmptyLibrary_IsFullyCoveredRatherThanDividedByZero()
    {
        var library = new LibraryLoudness(Guid.NewGuid(), "Musique", false, 0, 0, 0);

        Assert.Equal(1, library.Coverage);
        Assert.Equal(0, library.Missing);
        Assert.Equal(LoudnessGap.None, library.Gap);
    }

    /// <summary>
    /// More albums than one batch holds, so the chunking is actually taken round the loop. The
    /// fixture asserts no batch exceeds the declared size, which is what keeps this from passing
    /// against a version that quietly asks for all of them at once.
    /// </summary>
    [Fact]
    public void MoreAlbumsThanOneBatch_AreAllCounted()
    {
        // 150 tracks per album in the fixture, so this spans several batches of 200 albums.
        const int Total = (ReplayGainAudit.AlbumBatchSize + 40) * 150;
        var tracks = Enumerable.Range(0, Total)
            .Select(i => i % 2 == 0 ? WithTag(-7f) : Nothing())
            .ToArray();

        var library = Assert.Single(AuditOver(tracks).Run(CancellationToken.None));

        Assert.Equal(Total, library.Tracks);
        Assert.Equal(Total / 2, library.FromTag);
    }

    /// <summary>
    /// The card has to say "measuring is switched off" rather than "625 tracks are broken". One is
    /// a setting the admin can change in ten seconds; the other is an accusation.
    /// </summary>
    [Fact]
    public async Task WithTheScanOffEverywhere_TheCardSaysSoRatherThanBlamingTheFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryLoudness(Guid.NewGuid(), "Musique", false, 100, 0, 0));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicScanOff, card.Detail!.Key);
        Assert.Equal(ModuleState.Degraded, card.State);
    }

    [Fact]
    public async Task WithTheScanOnAndIncomplete_TheDetailCountsRatherThanExcuses()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryLoudness(Guid.NewGuid(), "Musique", true, 100, 40, 10));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicLoudnessDetail, card.Detail!.Key);
        Assert.Equal(50, card.Detail.Args[0]);
        Assert.Equal(100, card.Detail.Args[1]);
    }

    /// <summary>
    /// The test that was missing, and the reason the card was wrong on a real server.
    /// <para>
    /// Two libraries, one fully covered and one not measured at all. Averaging them gives 11%, a
    /// figure that describes neither and points at nothing to do. The card has to name the library
    /// that needs attention and quote ITS number.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoLibrariesInDifferentStates_AreNotAveragedIntoOneMeaninglessNumber()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(
            new LibraryLoudness(Guid.NewGuid(), "Musiques", true, 22350, 22350, 0),
            new LibraryLoudness(Guid.NewGuid(), "Musiques2", false, 174927, 0, 0));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        // The worst library, by name, with its own coverage -- not the 11% average.
        Assert.Equal("Musiques2", card.Headline.Args[0]);
        Assert.Equal(0, card.Headline.Args[1]);
        Assert.Equal(ModuleState.Degraded, card.State);

        // And the detail says the rest of the server is fine, so 0% does not read as a disaster.
        Assert.Equal(StringKeys.MusicOtherLibraries, card.Detail!.Key);
        Assert.Equal(100, card.Detail.Args[1]);
    }

    [Fact]
    public async Task WithOneLibraryOnly_TheDetailIsJustTheReason()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryLoudness(Guid.NewGuid(), "Musique", false, 100, 0, 0));

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicScanOff, card.Detail!.Key);
    }

    [Fact]
    public async Task AWellCoveredLibrary_IsHealthy()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryLoudness(Guid.NewGuid(), "Musique", true, 100, 98, 0));

        Assert.Equal(ModuleState.Healthy, (await fixture.Module.BuildCardAsync(default)).State);
    }

    [Fact]
    public async Task BeforeAnyAudit_TheCardSaysNothingHasRunRatherThanZeroPercent()
    {
        await using var fixture = await Fixture.CreateAsync();

        var card = await fixture.Module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(StringKeys.MusicNoAudit, card.Headline.Key);
    }

    /// <summary>
    /// The two reasons produce two different issues, because the action is not the same: one is
    /// "turn the setting on", the other is "the scan has not finished or it failed".
    /// </summary>
    [Theory]
    [InlineData(false, StringKeys.IssueLoudnessScanOff)]
    [InlineData(true, StringKeys.IssueLoudnessIncomplete)]
    public async Task TheIssueRaised_NamesTheReason(bool scanEnabled, string expectedKey)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryLoudness(Guid.NewGuid(), "Musique", scanEnabled, 100, 10, 0));

        await fixture.Module.ReportIssuesAsync(fixture.Issues, CancellationToken.None);

        var issue = Assert.Single(await fixture.Issues.GetOpenAsync(CancellationToken.None));
        Assert.Equal(expectedKey, issue.Issue.Title.Key);
    }

    [Fact]
    public async Task AFullyCoveredLibrary_RaisesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync(new LibraryLoudness(Guid.NewGuid(), "Musique", true, 100, 100, 0));

        await fixture.Module.ReportIssuesAsync(fixture.Issues, CancellationToken.None);

        Assert.Empty(await fixture.Issues.GetOpenAsync(CancellationToken.None));
    }

    private static Audio WithTag(float gain) => new() { Id = Guid.NewGuid(), NormalizationGain = gain };

    private static Audio Measured(float lufs) => new() { Id = Guid.NewGuid(), LUFS = lufs };

    private static Audio Nothing() => new() { Id = Guid.NewGuid() };

    /// <summary>
    /// Builds an audit over one music library whose tracks are spread across enough albums to
    /// exercise the batching, since a single-album fixture would never take the loop round twice.
    /// </summary>
    /// <summary>
    /// Builds an audit over one music library.
    /// <para>
    /// The library manager models the server's real <c>AlbumIds</c> semantics, which is a join on
    /// the album NAME rather than a filter on the id:
    /// </para>
    /// <code>
    /// var subQuery = context.BaseItems.WhereOneOrMany(filter.AlbumIds, f =&gt; f.Id);
    /// baseQuery = baseQuery.Where(e =&gt; subQuery.Any(f =&gt; f.Name == e.Album));
    /// </code>
    /// <para>
    /// The earlier version filtered by id AND named every album "album". Faithful to the server,
    /// that fixture would have every batch return every track in the library -- which is exactly
    /// the over-counting the audit now guards against, and exactly what the old double could not
    /// express.
    /// </para>
    /// </summary>
    /// <param name="tracks">The tracks the library holds.</param>
    /// <returns>An audit over them.</returns>
    private static ReplayGainAudit AuditOver(params Audio[] tracks)
    {
        var library = new Folder { Id = Guid.NewGuid(), Name = "Musique" };

        // One album per batch-and-a-bit worth of tracks, so more than one round trip happens.
        const int PerAlbum = 150;
        var albums = tracks
            .Select((track, i) => (track, album: i / PerAlbum))
            .GroupBy(x => x.album)
            .Select(g => (Id: Guid.NewGuid(), Name: $"album {g.Key}", Tracks: g.Select(x => x.track).ToArray()))
            .ToList();

        foreach (var album in albums)
        {
            foreach (var track in album.Tracks)
            {
                track.Album = album.Name;
            }
        }

        var everyTrack = albums.SelectMany(a => a.Tracks).ToList();
        var names = albums.ToDictionary(a => a.Id, a => a.Name);

        var manager = Substitute.For<ILibraryManager>();
        manager.GetContentType(library).Returns(Jellyfin.Data.Enums.CollectionType.music);
        manager.GetLibraryOptions(library)
            .Returns(new MediaBrowser.Model.Configuration.LibraryOptions { EnableLUFSScan = true });
        manager.GetCount(Arg.Any<InternalItemsQuery>()).Returns(tracks.Length);

        manager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(call =>
        {
            var query = call.Arg<InternalItemsQuery>();
            var kind = query.IncludeItemTypes.FirstOrDefault();

            if (kind == BaseItemKind.CollectionFolder)
            {
                return new List<BaseItem> { library };
            }

            if (kind == BaseItemKind.MusicAlbum)
            {
                return albums
                    .Select(BaseItem (a) => new MusicAlbum { Id = a.Id, Name = a.Name })
                    .ToList();
            }

            // A batch bigger than the declared size would mean the chunking silently stopped
            // working, which is the whole point of asking by album.
            Assert.True(
                query.AlbumIds.Length <= ReplayGainAudit.AlbumBatchSize,
                $"asked for {query.AlbumIds.Length} albums at once");

            // The name join carries no scope of its own, so the walk has to ask for one.
            Assert.Equal(library.Id, query.ParentId);
            Assert.True(query.Recursive, "a track query must be recursive to be scoped at all");

            var wanted = query.AlbumIds.Select(id => names[id]).ToHashSet(StringComparer.Ordinal);

            return everyTrack
                .Where(t => t.Album is not null && wanted.Contains(t.Album))
                .Cast<BaseItem>()
                .ToList();
        });

        return new ReplayGainAudit(manager, NullLogger<ReplayGainAudit>.Instance);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

        private FlynnDatabase _database = null!;

        public MusicRepository Repository { get; private set; } = null!;

        public MusicModule Module { get; private set; } = null!;

        public IssueRegistry Issues { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture._database = new FlynnDatabase(Path.Combine(fixture._directory, "flynn.db"));
            await new SchemaMigrator(fixture._database, NullLogger<SchemaMigrator>.Instance)
                .MigrateAsync(Migrations.All, CancellationToken.None);

            var readiness = new DatabaseReadiness();
            readiness.MarkReady();

            fixture.Repository = new MusicRepository(fixture._database);
            fixture.Issues = new IssueRegistry(fixture._database, TimeProvider.System);
            fixture.Module = new MusicModule(
                fixture.Repository, readiness, TimeProvider.System, NullLogger<MusicModule>.Instance);
            return fixture;
        }

        public Task SaveAsync(params LibraryLoudness[] libraries) =>
            Repository.SaveLoudnessAsync(DateTimeOffset.UtcNow, libraries, CancellationToken.None);

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
