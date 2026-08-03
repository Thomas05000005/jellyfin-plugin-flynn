using Jellyfin.Plugin.Flynn.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Flynn;

/// <summary>
/// Flynn — a Jellyfin administration suite. One plugin, many modules the admin can switch on
/// and off individually.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin's identity as far as Jellyfin is concerned. Changing it makes every existing
    /// installation see a different plugin and lose its settings, so it is fixed for life.
    /// </summary>
    private const string PluginGuid = "3c456481-2879-4378-82fd-96bc084d5668";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Server paths, supplied by Jellyfin.</param>
    /// <param name="xmlSerializer">Configuration serialiser, supplied by Jellyfin.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the running instance.
    /// <para>
    /// Jellyfin constructs the plugin itself, so this is the only handle non-DI code has. Prefer
    /// constructor injection wherever DI is available; reach for this only when it is not.
    /// </para>
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Flynn";

    /// <inheritdoc />
    public override string Description =>
        "Administration suite for Jellyfin: maintenance, dashboards, capacity forecasting and library care.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }
}
