using Jellyfin.Plugin.Flynn.Core.Localization;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Modules.Storage;

namespace Jellyfin.Plugin.Flynn.Modules.Resources;

/// <summary>
/// Reports what Jellyfin actually costs, transcodes included.
/// <para>
/// The figures come from the control group rather than from the process, because on a media server
/// most of the CPU is spent by ffmpeg children that <c>Process.GetCurrentProcess()</c> cannot see.
/// Reporting the process alone would show a nearly idle server during four simultaneous transcodes.
/// </para>
/// </summary>
public sealed class ResourcesModule : IFlynnModule
{
    /// <summary>
    /// How long a stored sample stays usable as the baseline for a rate.
    /// <para>
    /// Beyond this the average is over so long a window that it no longer describes now. A fresh
    /// pair is taken instead.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromMinutes(10);

    /// <summary>Shortest baseline worth using, below which the reading is mostly scheduling noise.</summary>
    internal static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(2);

    /// <summary>Gap used when a fresh pair has to be taken during a page load.</summary>
    internal static readonly TimeSpan FreshPairGap = TimeSpan.FromMilliseconds(300);

    private readonly CgroupReader _reader;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private CgroupSample? _previous;

    /// <summary>Initializes a new instance of the <see cref="ResourcesModule"/> class.</summary>
    /// <param name="reader">Reads the control group counters.</param>
    /// <param name="clock">Time source.</param>
    public ResourcesModule(CgroupReader reader, TimeProvider clock)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public string Id => "resources";

    /// <inheritdoc />
    public string NameKey => StringKeys.ResourcesName;

    /// <inheritdoc />
    public string SummaryKey => StringKeys.ResourcesSummary;

    /// <inheritdoc />
    public ModuleCategory Category => ModuleCategory.System;

    /// <inheritdoc />
    public bool EnabledByDefault => true;

    /// <inheritdoc />
    public async Task<ModuleCard> BuildCardAsync(CancellationToken cancellationToken)
    {
        if (!_reader.IsAvailable)
        {
            return new ModuleCard(
                Id, ModuleState.Degraded, LocalizedText.Of(StringKeys.ResourcesUnavailable), null,
                _clock.GetUtcNow());
        }

        var usage = await SampleAsync(cancellationToken).ConfigureAwait(false);
        if (usage?.CpuPercent is null)
        {
            return new ModuleCard(
                Id, ModuleState.Healthy, LocalizedText.Of(StringKeys.ResourcesMeasuring), null,
                _clock.GetUtcNow());
        }

        var memory = StorageModule.ScaleBytes(usage.MemoryInUseBytes);
        var cache = StorageModule.ScaleBytes(usage.MemoryChargedBytes - usage.MemoryInUseBytes);

        return new ModuleCard(
            Id,
            usage.CpuPercent >= 90 || usage.ThrottledFraction >= 0.1 ? ModuleState.Degraded : ModuleState.Healthy,
            LocalizedText.Of(
                StringKeys.ResourcesHeadline,
                (int)Math.Round(usage.CpuPercent.Value),
                memory.Value,
                LocalizedText.Of(memory.UnitKey)),
            LocalizedText.Of(StringKeys.ResourcesCacheNote, cache.Value, LocalizedText.Of(cache.UnitKey)),
            _clock.GetUtcNow());
    }

    /// <summary>
    /// Produces a usage reading, reusing the stored sample when it is recent enough.
    /// <para>
    /// A rate needs two readings. Keeping the previous one means a page opened twice in a few
    /// minutes gets a real average over that gap, which describes the server better than anything
    /// measured inside one page load. When there is nothing recent to compare against, a second
    /// sample is taken a fraction of a second later: short, noisy, and still better than a blank.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The usage, or null when the counters vanished mid-read.</returns>
    internal async Task<ResourceUsage?> SampleAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var current = _reader.Read(now);
        if (current is null)
        {
            return null;
        }

        CgroupSample? baseline;
        lock (_gate)
        {
            baseline = _previous;
            _previous = current;
        }

        var age = baseline is null ? TimeSpan.MaxValue : now - baseline.TakenAt;
        if (baseline is not null && age >= MinimumWindow && age <= MaximumWindow)
        {
            return ResourceCalculator.Between(baseline, current, Environment.ProcessorCount);
        }

        await Task.Delay(FreshPairGap, _clock, cancellationToken).ConfigureAwait(false);

        var second = _reader.Read(_clock.GetUtcNow());
        if (second is null)
        {
            return ResourceCalculator.Between(null, current, Environment.ProcessorCount);
        }

        lock (_gate)
        {
            _previous = second;
        }

        return ResourceCalculator.Between(current, second, Environment.ProcessorCount);
    }
}
