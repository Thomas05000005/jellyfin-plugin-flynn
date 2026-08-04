using System.Diagnostics;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The registry's whole reason to exist is isolation. These tests are the guarantee: if one of
/// them goes red, a single broken module can take the dashboard down with it.
/// </summary>
public class ModuleRegistryTests
{
    private const string HealthyHeadlineKey = "test.healthy.headline";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);

    private static ModuleRegistry Build(params IFlynnModule[] modules) =>
        new(modules, NullLogger<ModuleRegistry>.Instance, ShortTimeout);

    [Fact]
    public async Task AThrowingModule_DoesNotAffectItsNeighbours()
    {
        var registry = Build(new ThrowingModule(), new HealthyModule());

        var cards = await registry.BuildCardsAsync(_ => true, CancellationToken.None);

        var broken = Assert.Single(cards, c => c.ModuleId == "throwing");
        Assert.Equal(ModuleState.Failed, broken.State);

        var healthy = Assert.Single(cards, c => c.ModuleId == "healthy");
        Assert.Equal(ModuleState.Healthy, healthy.State);
        Assert.Equal(HealthyHeadlineKey, healthy.Headline.Key);
    }

    /// <summary>
    /// A failure card is built from keys alone, so there is no channel through which an exception
    /// message could reach the admin's screen. This asserts that the channel stays closed.
    /// </summary>
    [Fact]
    public async Task AFailedCard_CarriesOnlyKeys_SoNothingFromTheExceptionCanLeak()
    {
        var registry = Build(new ThrowingModule());

        var card = Assert.Single(await registry.BuildCardsAsync(_ => true, CancellationToken.None));

        Assert.Equal(StringKeys.ModuleUnavailableHeadline, card.Headline.Key);
        Assert.Equal(StringKeys.ModuleUnavailableDetail, card.Detail?.Key);
        Assert.Empty(card.Headline.Args);
        Assert.Empty(card.Detail!.Args);
    }

    [Fact]
    public async Task ADisabledModule_IsNeverCalled()
    {
        var spy = new HealthyModule();
        var registry = Build(spy);

        var card = Assert.Single(await registry.BuildCardsAsync(_ => false, CancellationToken.None));

        Assert.Equal(ModuleState.Disabled, card.State);
        Assert.Equal(StringKeys.ModuleDisabledHeadline, card.Headline.Key);
        Assert.False(spy.WasCalled);
    }

    [Fact]
    public async Task AHangingModule_IsCutOffAndReportedAsFailed()
    {
        var registry = Build(new HangingModule(), new HealthyModule());

        var cards = await registry.BuildCardsAsync(_ => true, CancellationToken.None);

        var cut = Assert.Single(cards, c => c.ModuleId == "hanging");
        Assert.Equal(ModuleState.Failed, cut.State);
        Assert.Equal(StringKeys.ModuleTimedOutDetail, cut.Detail?.Key);

        Assert.Equal(ModuleState.Healthy, Assert.Single(cards, c => c.ModuleId == "healthy").State);
    }

    [Fact]
    public async Task CallerCancellation_IsNotSwallowedAsAModuleFailure()
    {
        var registry = Build(new HangingModule());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.BuildCardsAsync(_ => true, cts.Token));
    }

    private sealed class HealthyModule : IFlynnModule
    {
        public bool WasCalled { get; private set; }

        public string Id => "healthy";

        public string DisplayName => "Healthy";

        public string Summary => "Reports fine.";

        public bool EnabledByDefault => true;

        public Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new ModuleCard(
                Id,
                ModuleState.Healthy,
                LocalizedText.Of(HealthyHeadlineKey),
                null,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class ThrowingModule : IFlynnModule
    {
        internal const string SecretInMessage = "connection string to /var/lib/secret.db";

        public string Id => "throwing";

        public string DisplayName => "Throwing";

        public string Summary => "Always blows up.";

        public bool EnabledByDefault => true;

        public Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SecretInMessage);
    }

    private sealed class HangingModule : IFlynnModule
    {
        public string Id => "hanging";

        public string DisplayName => "Hanging";

        public string Summary => "Never returns on its own.";

        public bool EnabledByDefault => true;

        public async Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
