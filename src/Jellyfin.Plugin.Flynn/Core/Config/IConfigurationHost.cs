using Jellyfin.Plugin.Flynn.Configuration;

namespace Jellyfin.Plugin.Flynn.Core.Config;

/// <summary>
/// The seam between <see cref="ConfigStore"/> and Jellyfin's plugin base class.
/// <para>
/// It exists so the store's concurrency and validation behaviour can be tested without booting a
/// server: <c>BasePlugin&lt;T&gt;</c> reads the real configuration directory and writes the real
/// file, which is exactly what a test must not do.
/// </para>
/// </summary>
public interface IConfigurationHost
{
    /// <summary>
    /// Gets the live configuration instance.
    /// <para>
    /// Jellyfin caches this object for the lifetime of the process and never re-reads the file, so
    /// mutating it in place is immediately visible to the whole server even if nothing is ever
    /// saved. Treat what you get back as read-only; <see cref="ConfigStore"/> is what changes it.
    /// </para>
    /// </summary>
    PluginConfiguration Current { get; }

    /// <summary>
    /// Replaces the configuration and persists it.
    /// <para>
    /// Synchronous and blocking: it serialises straight to disk on the calling thread, then raises
    /// the server's <c>ConfigurationChanged</c> handler inline. Not something to call in a loop.
    /// </para>
    /// </summary>
    /// <param name="configuration">The already-validated replacement.</param>
    void Commit(PluginConfiguration configuration);
}
