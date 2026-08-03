using System.Diagnostics;
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
        Assert.Equal("all good", healthy.Headline);
    }

    [Fact]
    public async Task AFailedCard_NeverLeaksTheExceptionMessage()
    {
        var registry = Build(new ThrowingModule());

        var card = Assert.Single(await registry.BuildCardsAsync(_ => true, CancellationToken.None));

        Assert.Equal(ModuleState.Failed, card.State);
        Assert.DoesNotContain(ThrowingModule.SecretInMessage, card.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingModule.SecretInMessage, card.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledModule_IsNeverCalled()
    {
        var spy = new HealthyModule();
        var registry = Build(spy);

        var card = Assert.Single(await registry.BuildCardsAsync(_ => false, CancellationToken.None));

        Assert.Equal(ModuleState.Disabled, card.State);
        Assert.False(spy.WasCalled);
    }

    [Fact]
    public async Task AHangingModule_IsCutOffAndReportedAsFailed()
    {
        var registry = Build(new HangingModule(), new HealthyModule());

        var cards = await registry.BuildCardsAsync(_ => true, CancellationToken.None);

        Assert.Equal(ModuleState.Failed, Assert.Single(cards, c => c.ModuleId == "hanging").State);
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

        public Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(
                new ModuleCard(Id, ModuleState.Healthy, "all good", null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class ThrowingModule : IFlynnModule
    {
        internal const string SecretInMessage = "connection string to /var/lib/secret.db";

        public string Id => "throwing";

        public string DisplayName => "Throwing";

        public string Summary => "Always blows up.";

        public Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SecretInMessage);
    }

    private sealed class HangingModule : IFlynnModule
    {
        public string Id => "hanging";

        public string DisplayName => "Hanging";

        public string Summary => "Never returns on its own.";

        public async Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
