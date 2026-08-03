using System.Xml;
using Jellyfin.Plugin.Flynn.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Config;

/// <summary>
/// The only thing in Flynn allowed to change persisted configuration.
/// <para>
/// Jellyfin's own locking covers the file write and nothing else, so two endpoints doing
/// read-modify-write on the shared configuration object can and do lose each other's changes.
/// Every mutation here happens under one gate, against a private copy, and is proved to reload
/// before it is allowed anywhere near the disk.
/// </para>
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IConfigurationHost _host;
    private readonly ILogger<ConfigStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConfigStore"/> class.</summary>
    /// <param name="host">The plugin configuration seam.</param>
    /// <param name="logger">Logger.</param>
    public ConfigStore(IConfigurationHost host, ILogger<ConfigStore> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the current configuration for reading.
    /// <para>
    /// This is the live instance the whole server shares — <b>do not mutate it</b>. Changing a
    /// field here takes effect everywhere immediately and is never written to disk, so the server
    /// silently disagrees with its own configuration file until it restarts. Use
    /// <see cref="MutateAsync"/>.
    /// </para>
    /// </summary>
    public PluginConfiguration Current => _host.Current;

    /// <summary>
    /// Applies a change to the configuration and persists it.
    /// <para>
    /// The whole read-modify-write runs under a single gate, so concurrent callers queue instead
    /// of overwriting each other. <paramref name="mutate"/> is handed a private copy: if it throws,
    /// or if the result could not be reloaded afterwards, the live configuration and the file on
    /// disk are both left exactly as they were.
    /// </para>
    /// </summary>
    /// <param name="mutate">Applies the change to a private copy of the configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the new configuration is on disk.</returns>
    /// <exception cref="ConfigurationRejectedException">
    /// The resulting configuration would not survive being written and read back. Callers should
    /// surface this as a bad request, not a server error: it means the submitted values are
    /// unpersistable, most often a control character in a free-text field.
    /// </exception>
    public async Task MutateAsync(Action<PluginConfiguration> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Work on a copy. Until the commit below, nothing the caller does is observable —
            // which is what makes a throwing mutate harmless.
            var draft = ConfigSerialization.RoundTrip(_host.Current);
            mutate(draft);

            PluginConfiguration validated;
            try
            {
                // Rehearse the persistence. Jellyfin would happily write an unreadable file and
                // only discover it at the next boot, by which point the settings are gone and
                // nothing was logged. Failing here costs nothing.
                validated = ConfigSerialization.RoundTrip(draft);
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "Refused a configuration change that could not be written and read back. "
                    + "The stored configuration is unchanged.");
                throw new ConfigurationRejectedException(
                    "The submitted configuration cannot be stored. This usually means a text field "
                    + "contains a character that is not valid in XML.",
                    ex);
            }

            _host.Commit(validated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
