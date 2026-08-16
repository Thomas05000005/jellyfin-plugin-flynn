using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Modules;

/// <summary>
/// Holds every registered module and is the only place that talks to them as a group.
/// <para>
/// Its job is isolation: one module throwing, hanging, or being switched off must never affect
/// the others. Callers get a card per module, always, whatever happened.
/// </para>
/// </summary>
public sealed class ModuleRegistry
{
    /// <summary>
    /// How long a single module may take to build its card before it is reported as failed.
    /// Cards are supposed to read pre-computed values, so this is generous on purpose: hitting it
    /// means the module is doing work it should have done in its scheduled task.
    /// </summary>
    internal static readonly TimeSpan DefaultCardTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IFlynnModule> _modules;
    private readonly ILogger<ModuleRegistry> _logger;
    private readonly TimeSpan _cardTimeout;

    /// <summary>Initializes a new instance of the <see cref="ModuleRegistry"/> class.</summary>
    /// <param name="modules">Every module resolved from DI.</param>
    /// <param name="logger">Logger.</param>
    public ModuleRegistry(IEnumerable<IFlynnModule> modules, ILogger<ModuleRegistry> logger)
        : this(modules, logger, DefaultCardTimeout)
    {
    }

    /// <summary>Test seam: same thing with a shorter deadline so the timeout path is fast to assert.</summary>
    /// <param name="modules">Every module resolved from DI.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cardTimeout">Per-module deadline.</param>
    internal ModuleRegistry(
        IEnumerable<IFlynnModule> modules,
        ILogger<ModuleRegistry> logger,
        TimeSpan cardTimeout)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules.ToList();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cardTimeout = cardTimeout;
    }

    /// <summary>Gets every registered module, enabled or not.</summary>
    public IReadOnlyList<IFlynnModule> Modules => _modules;

    /// <summary>
    /// Whether a module is currently switched on.
    /// <para>
    /// A module the admin has never touched falls back to its own default, not to off: the absence
    /// of a saved preference is "not asked yet", not "asked and declined".
    /// </para>
    /// <para>
    /// It lives here rather than at each call site because two callers disagreeing about what
    /// "enabled" means is how a scheduled task ends up doing hours of work for a card nobody is
    /// shown. An unknown id is off, since nothing can act on it.
    /// </para>
    /// </summary>
    /// <param name="saved">The admin's saved toggles, from the plugin configuration.</param>
    /// <param name="moduleId">The module id.</param>
    /// <returns>True when the module should run.</returns>
    public bool IsEnabled(IReadOnlyList<Configuration.ModuleToggle> saved, string moduleId)
    {
        ArgumentNullException.ThrowIfNull(saved);

        var module = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null)
        {
            return false;
        }

        return saved.FirstOrDefault(m => m.Id == moduleId)?.Enabled ?? module.EnabledByDefault;
    }

    /// <summary>
    /// Builds one card per module, concurrently, isolating failures.
    /// </summary>
    /// <param name="isEnabled">
    /// Tells whether a module id is currently switched on. Disabled modules are not called at all.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One card per registered module, in registration order.</returns>
    public async Task<IReadOnlyList<ModuleCard>> BuildCardsAsync(
        Func<string, bool> isEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);

        var tasks = _modules.Select(m => BuildOneAsync(m, isEnabled, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ModuleCard> BuildOneAsync(
        IFlynnModule module,
        Func<string, bool> isEnabled,
        CancellationToken cancellationToken)
    {
        if (!isEnabled(module.Id))
        {
            return ModuleCard.Disabled(module.Id, DateTimeOffset.UtcNow);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_cardTimeout);

        try
        {
            return await module.BuildCardAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER gave up (request aborted, server shutting down). That is not a module
            // failure and must not be dressed up as one, or a cancelled dashboard request would
            // render a wall of red cards. Must stay ABOVE the generic catch below.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Module {ModuleId} took longer than {Seconds}s to build its card and was cut off.",
                module.Id,
                _cardTimeout.TotalSeconds);
            return ModuleCard.TimedOut(module.Id, DateTimeOffset.UtcNow);
        }
#pragma warning disable CA1031 // Isolation is the entire point of this class.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module {ModuleId} threw while building its card.", module.Id);
            return ModuleCard.Failed(module.Id, DateTimeOffset.UtcNow);
        }
#pragma warning restore CA1031
    }
}
