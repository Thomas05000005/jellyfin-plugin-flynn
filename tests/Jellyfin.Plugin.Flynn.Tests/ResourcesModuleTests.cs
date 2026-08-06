using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Resources;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The module itself, rather than the two collaborators it is built from.
/// <para>
/// Both of those were well covered and the module between them was not, which left the part that
/// actually reaches the screen untested -- including whether its card text resolves at all. A
/// headline whose placeholders do not match the arguments handed to it does not throw: the
/// formatter is caught and the raw template is rendered, so the card shows
/// "{0}% CPU {1}, {2} {3} in use" to the admin and every test stays green.
/// </para>
/// </summary>
public sealed class ResourcesModuleTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-06T12:00:00Z", null);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "flynn-cgroup", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(T0);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task WithoutCgroupCounters_TheCardSaysSoInsteadOfShowingZero()
    {
        var module = new ResourcesModule(new CgroupReader(_root), _clock);

        var card = await module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Degraded, card.State);
        Assert.Equal(StringKeys.ResourcesUnavailable, card.Headline.Key);
    }

    /// <summary>
    /// The test the whole file exists for. Every placeholder in the headline and the detail must be
    /// filled, in both catalogues, or the admin reads the template.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task TheCardText_ResolvesWithNoPlaceholderLeftOver(string language)
    {
        var module = await MeasuringModuleAsync(cpuMicrosecondsUsed: 1_000_000);
        var card = await module.BuildCardAsync(CancellationToken.None);
        var strings = FlynnStrings.ForCulture(new System.Globalization.CultureInfo(language));

        var headline = card.Headline.Resolve(strings);
        var detail = card.Detail?.Resolve(strings);

        Assert.DoesNotContain("{0}", headline, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", headline, StringComparison.Ordinal);
        Assert.DoesNotContain("{2}", headline, StringComparison.Ordinal);
        Assert.DoesNotContain("{3}", headline, StringComparison.Ordinal);
        Assert.DoesNotContain("[resources.", headline, StringComparison.Ordinal);
        Assert.NotNull(detail);
        Assert.DoesNotContain("{0}", detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a quota the ceiling is the machine, and the card has to say which -- eighty percent
    /// of a quota means throttling is imminent, eighty percent of a machine means busy.
    /// </summary>
    [Fact]
    public async Task WithNoQuota_TheHeadlineNamesTheMachinesCoresRatherThanALimit()
    {
        var module = await MeasuringModuleAsync(cpuMicrosecondsUsed: 1_000_000);
        var card = await module.BuildCardAsync(CancellationToken.None);
        var strings = FlynnStrings.ForCulture(System.Globalization.CultureInfo.InvariantCulture);

        var headline = card.Headline.Resolve(strings);

        Assert.Contains("cores", headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limit", headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithAQuota_TheHeadlineSaysItIsALimit()
    {
        var module = await MeasuringModuleAsync(cpuMicrosecondsUsed: 1_000_000, cpuMax: "200000 100000");
        var card = await module.BuildCardAsync(CancellationToken.None);
        var strings = FlynnStrings.ForCulture(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("limit", card.Headline.Resolve(strings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASaturatedServer_ReadsAsDegraded()
    {
        // Every core busy for the whole window.
        var module = await MeasuringModuleAsync(
            cpuMicrosecondsUsed: 10_000_000L * Environment.ProcessorCount);

        var card = await module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Degraded, card.State);
    }

    [Fact]
    public async Task AnIdleServer_ReadsAsHealthy()
    {
        var module = await MeasuringModuleAsync(cpuMicrosecondsUsed: 0);

        var card = await module.BuildCardAsync(CancellationToken.None);

        Assert.Equal(ModuleState.Healthy, card.State);
    }

    /// <summary>
    /// The memory shown is what is in use, not what the group is charged for. Page cache is
    /// reclaimable, and counting it would more than double the figure on a real server.
    /// </summary>
    [Fact]
    public async Task TheMemoryReported_ExcludesReclaimableCache()
    {
        var module = await MeasuringModuleAsync(cpuMicrosecondsUsed: 0);

        var usage = await module.SampleAsync(CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(4 * GiB, usage!.MemoryChargedBytes);
        Assert.Equal(3 * GiB, usage.MemoryInUseBytes);
    }

    /// <summary>
    /// Builds a module that already holds a baseline ten seconds old, so the card is measured from
    /// a real window instead of the noisy 300 ms pair taken when there is nothing to compare to.
    /// </summary>
    private async Task<ResourcesModule> MeasuringModuleAsync(
        long cpuMicrosecondsUsed,
        string cpuMax = "max 100000")
    {
        Write(cpuMax, cpuUsage: 0);
        var module = new ResourcesModule(new CgroupReader(_root), _clock);

        // First call stores the baseline; the delay it takes is real but tiny.
        await module.SampleAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Write(cpuMax, cpuUsage: cpuMicrosecondsUsed);
        return module;
    }

    private void Write(string cpuMax, long cpuUsage)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "cpu.stat"),
            $"usage_usec {cpuUsage}\nnr_periods 0\nnr_throttled 0");
        File.WriteAllText(Path.Combine(_root, "cpu.max"), cpuMax);
        File.WriteAllText(Path.Combine(_root, "memory.current"), (4 * GiB).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(_root, "memory.max"), "max");
        File.WriteAllText(
            Path.Combine(_root, "memory.stat"),
            $"anon {2 * GiB}\ninactive_file {GiB}\nactive_file 0");
    }
}
