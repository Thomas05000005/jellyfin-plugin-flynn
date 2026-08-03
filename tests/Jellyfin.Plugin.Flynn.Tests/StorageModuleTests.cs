using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The card has to be cheap and it has to be honest: at four hundred thousand tracks a card that
/// computes anything on page load is an outage, and one that shows last week's numbers as if they
/// were current is worse than one that admits it has nothing.
/// </summary>
public sealed class StorageModuleTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flynn-tests", Guid.NewGuid().ToString("N"));

    private FlynnDatabase _database = null!;
    private StorageRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _database = new FlynnDatabase(Path.Combine(_directory, "flynn.db"));
        await new SchemaMigrator(_database, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(Migrations.All, CancellationToken.None);
        _repository = new StorageRepository(_database);
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
    public async Task ASnapshot_SurvivesTheRoundTrip()
    {
        var taken = DateTimeOffset.Parse("2026-08-03T03:15:00Z", null);
        await _repository.SaveAsync(
            new StorageSnapshot(
                taken,
                [new LibrarySnapshot("lib-1", "Films", 1700, 8_000_000_000_000)],
                [new DeviceSnapshot("dev-1", "/mnt/pool", 20_000_000_000_000, 4_000_000_000_000)]),
            CancellationToken.None);

        var back = await _repository.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(back);
        Assert.Equal(taken, back.TakenAt);
        Assert.Equal("Films", Assert.Single(back.Libraries).LibraryName);
        Assert.Equal(8_000_000_000_000, back.TotalLibraryBytes);
        Assert.Equal("/mnt/pool", Assert.Single(back.Devices).MountPath);
    }

    [Fact]
    public async Task TheLatestSnapshot_IsTheMostRecentOne()
    {
        await _repository.SaveAsync(Snapshot("2026-08-01T03:15:00Z", 1_000), CancellationToken.None);
        await _repository.SaveAsync(Snapshot("2026-08-03T03:15:00Z", 3_000), CancellationToken.None);
        await _repository.SaveAsync(Snapshot("2026-08-02T03:15:00Z", 2_000), CancellationToken.None);

        var back = await _repository.GetLatestAsync(CancellationToken.None);

        Assert.Equal(3_000, back!.TotalLibraryBytes);
    }

    /// <summary>Two libraries on one disk must not have the same free space counted twice.</summary>
    [Fact]
    public void TheTightestDevice_IsTheOneClosestToFull()
    {
        var snapshot = new StorageSnapshot(
            DateTimeOffset.UtcNow,
            [],
            [
                new DeviceSnapshot("a", "/mnt/a", 1000, 500),   // 50% used
                new DeviceSnapshot("b", "/mnt/b", 1000, 50),    // 95% used
                new DeviceSnapshot("c", "/mnt/c", 1000, 900),   // 10% used
            ]);

        Assert.Equal("/mnt/b", snapshot.TightestDevice!.MountPath);
    }

    [Fact]
    public void AnUnreadableDevice_DoesNotDivideByZero()
    {
        var device = new DeviceSnapshot("x", "/mnt/x", 0, 0);

        Assert.Equal(0, device.UsedFraction);
    }

    [Fact]
    public async Task WithNoSnapshotYet_TheCardSaysSoRatherThanShowingZero()
    {
        var module = new StorageModule(_repository, Ready());

        var card = await module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Degraded, card.State);
        Assert.Equal(StringKeys.StorageNoSnapshotYet, card.Headline.Key);
    }

    /// <summary>
    /// Degraded rather than Failed: nothing is broken about this module, its storage is simply not
    /// there. Either way it must not show numbers.
    /// </summary>
    [Fact]
    public async Task WithStorageDown_TheCardIsDegradedAndCarriesNoFigures()
    {
        var readiness = new DatabaseReadiness();
        var module = new StorageModule(_repository, readiness);

        var card = await module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Degraded, card.State);
        Assert.Equal(StringKeys.StorageUnavailable, card.Headline.Key);
        Assert.Empty(card.Headline.Args);
    }

    /// <summary>
    /// The card reports when the data was computed, not when the card was built, so a panel that
    /// has not been refreshed for a week is visibly a week old.
    /// </summary>
    [Fact]
    public async Task TheCard_IsStampedWithTheSweepTimeNotTheRenderTime()
    {
        var taken = DateTimeOffset.UtcNow.AddDays(-7);
        await _repository.SaveAsync(
            new StorageSnapshot(taken, [new LibrarySnapshot("l", "L", 1, 42)], []),
            CancellationToken.None);

        var card = await new StorageModule(_repository, Ready()).BuildCardAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Healthy, card.State);
        Assert.Equal(taken.ToUnixTimeSeconds(), card.GeneratedAt.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Asserted as a number and a unit, never as a formatted string. A test expecting "4.2 TB"
    /// passes in English and fails in French for a decimal comma, which says nothing about the
    /// code and everything about the machine it ran on.
    /// </summary>
    [Theory]
    [InlineData(0, 0d, "B")]
    [InlineData(-5, 0d, "B")]
    [InlineData(512, 512d, "B")]
    [InlineData(1024, 1d, "KB")]
    [InlineData(1536, 2d, "KB")]
    [InlineData(1099511627776L, 1d, "TB")]
    [InlineData(4_617_089_444_659L, 4.2d, "TB")]
    public void ByteCounts_AreScaledButNotFormatted(long bytes, double value, string unit)
    {
        var scaled = StorageModule.ScaleBytes(bytes);

        Assert.Equal(value, scaled.Value);
        Assert.Equal(unit, scaled.Unit);
    }

    /// <summary>
    /// The reason the value stays a number: the separator is the reader's, not the server
    /// process's.
    /// </summary>
    [Fact]
    public void TheHeadline_UsesTheReadersDecimalSeparator()
    {
        var text = LocalizedText.Of(StringKeys.StorageHeadline, 4.2d, "TB");

        var french = text.Resolve(FlynnStrings.ForCulture(new System.Globalization.CultureInfo("fr-FR")));
        var english = text.Resolve(FlynnStrings.ForCulture(new System.Globalization.CultureInfo("en-GB")));

        Assert.Contains("4,2", french, StringComparison.Ordinal);
        Assert.Contains("4.2", english, StringComparison.Ordinal);
    }

    private static DatabaseReadiness Ready()
    {
        var readiness = new DatabaseReadiness();
        readiness.MarkReady();
        return readiness;
    }

    private static StorageSnapshot Snapshot(string when, long bytes) =>
        new(
            DateTimeOffset.Parse(when, null),
            [new LibrarySnapshot("lib-1", "Films", 1, bytes)],
            []);
}
