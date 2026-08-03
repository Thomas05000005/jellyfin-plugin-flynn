using Jellyfin.Plugin.Flynn.Core.Config;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Core.Mutations;
using Jellyfin.Plugin.Flynn.Core.Web;
using Jellyfin.Plugin.Flynn.Modules.Storage;
using Microsoft.AspNetCore.Hosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Flynn;

/// <summary>
/// Registers everything Flynn needs with the server's container.
/// <para>
/// Jellyfin instantiates this itself, so it must keep a parameterless constructor.
/// </para>
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Where Flynn's database lives, relative to the server's data directory.
    /// <para>
    /// Under <c>DataPath</c> rather than under the plugin folder on purpose: removing and
    /// reinstalling a plugin is a normal thing to do — Jellyfin 12's own upgrade guidance tells
    /// people to — and it wipes the plugin folder. History that took months to accumulate should
    /// not evaporate because someone followed the upgrade instructions.
    /// </para>
    /// </summary>
    internal const string DatabaseFolder = "flynn";

    /// <summary>Name of the database file.</summary>
    internal const string DatabaseFile = "flynn.db";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddSingleton(TimeProvider.System);

        serviceCollection.AddSingleton(sp => new FlynnDatabase(
            DatabasePathFor(sp.GetRequiredService<IApplicationPaths>())));
        serviceCollection.AddSingleton<SchemaMigrator>();
        serviceCollection.AddSingleton<DatabaseReadiness>();
        serviceCollection.AddHostedService<DatabaseStartupService>();

        serviceCollection.AddSingleton<IConfigurationHost, PluginConfigurationHost>();
        serviceCollection.AddSingleton<ConfigStore>();

        serviceCollection.AddSingleton<IssueRegistry>();
        serviceCollection.AddSingleton<MutationKernel>();
        serviceCollection.AddSingleton<ModuleRegistry>();

        // Modules. Each registers itself as IFlynnModule so the registry and the admin page pick
        // it up without either of them naming it.
        serviceCollection.AddSingleton<StorageRepository>();
        serviceCollection.AddSingleton<IFlynnModule, StorageModule>();

        // An IStartupFilter is all it takes for ASP.NET Core to wrap the pipeline; the
        // server needs no knowledge of us.
        serviceCollection.AddSingleton<IStartupFilter, InjectionStartupFilter>();
    }

    /// <summary>Builds the database path from the server's paths.</summary>
    /// <param name="paths">The server's application paths.</param>
    /// <returns>Full path to the database file.</returns>
    internal static string DatabasePathFor(IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.DataPath, DatabaseFolder, DatabaseFile);
    }
}
