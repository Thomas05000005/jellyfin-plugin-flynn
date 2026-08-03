using Jellyfin.Plugin.Flynn.Configuration;

namespace Jellyfin.Plugin.Flynn.Core.Config;

/// <summary>
/// The production <see cref="IConfigurationHost"/>: forwards to the running plugin instance.
/// <para>
/// Nothing but <see cref="ConfigStore"/> should hold one of these. Going through it directly
/// bypasses the gate and the round-trip check, which is how two admin endpoints end up losing each
/// other's changes and how an unpersistable value reaches the disk.
/// </para>
/// </summary>
public sealed class PluginConfigurationHost : IConfigurationHost
{
    /// <inheritdoc />
    public PluginConfiguration Current =>
        Plugin.Instance?.Configuration
        ?? throw new InvalidOperationException(
            "The Flynn plugin instance is not available yet. Jellyfin constructs it during startup; "
            + "anything reading configuration before that is running too early.");

    /// <inheritdoc />
    public void Commit(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException(
                "The Flynn plugin instance is not available; refusing to persist configuration.");

        // Replaces the cached instance, writes the file synchronously, then raises the server's
        // ConfigurationChanged handlers inline on this thread.
        plugin.UpdateConfiguration(configuration);
    }
}
