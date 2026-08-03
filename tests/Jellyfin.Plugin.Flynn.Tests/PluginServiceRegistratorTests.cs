using Jellyfin.Plugin.Flynn;
using Jellyfin.Plugin.Flynn.Core.Config;
using Jellyfin.Plugin.Flynn.Core.Data;
using Jellyfin.Plugin.Flynn.Core.Issues;
using Jellyfin.Plugin.Flynn.Core.Modules;
using Jellyfin.Plugin.Flynn.Core.Mutations;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Wiring that compiles but does not resolve is a plugin that dies at boot, with a stack trace in
/// the server log and nothing in the UI. These tests build the container for real and pull
/// everything out of it.
/// </summary>
public class PluginServiceRegistratorTests
{
    /// <summary>
    /// The one that earns its keep. Forgetting a dependency compiles fine and only fails when
    /// Jellyfin starts, on someone else's machine.
    /// </summary>
    [Fact]
    public void EveryRegisteredService_CanActuallyBeResolved()
    {
        using var provider = BuildProvider(out var collection);

        var failures = new List<string>();
        foreach (var descriptor in collection)
        {
            if (descriptor.ServiceType.IsGenericTypeDefinition)
            {
                continue;
            }

            try
            {
                provider.GetRequiredService(descriptor.ServiceType);
            }
#pragma warning disable CA1031 // Collecting every failure beats stopping at the first.
            catch (Exception ex)
            {
                failures.Add($"{descriptor.ServiceType.Name}: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(typeof(FlynnDatabase))]
    [InlineData(typeof(SchemaMigrator))]
    [InlineData(typeof(DatabaseReadiness))]
    [InlineData(typeof(ConfigStore))]
    [InlineData(typeof(IssueRegistry))]
    [InlineData(typeof(MutationKernel))]
    [InlineData(typeof(ModuleRegistry))]
    [InlineData(typeof(TimeProvider))]
    public void EachSocleService_IsRegistered(Type serviceType)
    {
        using var provider = BuildProvider(out _);

        Assert.NotNull(provider.GetService(serviceType));
    }

    /// <summary>
    /// Sharing matters here: two ConfigStore instances would each hold their own write gate, which
    /// is exactly the lost update the store exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(typeof(ConfigStore))]
    [InlineData(typeof(FlynnDatabase))]
    [InlineData(typeof(DatabaseReadiness))]
    public void SocleServices_AreShared(Type serviceType)
    {
        using var provider = BuildProvider(out _);

        Assert.Same(provider.GetService(serviceType), provider.GetService(serviceType));
    }

    [Fact]
    public void TheMigration_RunsAtStartupRatherThanOnFirstUse()
    {
        using var provider = BuildProvider(out _);

        Assert.Contains(provider.GetServices<IHostedService>(), s => s is DatabaseStartupService);
    }

    /// <summary>
    /// Removing and reinstalling a plugin is normal — Jellyfin 12's upgrade guidance tells people
    /// to do it — and it wipes the plugin folder. Months of history must not live there.
    /// </summary>
    [Fact]
    public void TheDatabase_LivesOutsideThePluginFolder()
    {
        var paths = new FakePaths();

        var path = PluginServiceRegistrator.DatabasePathFor(paths);

        Assert.StartsWith(paths.DataPath, path, StringComparison.Ordinal);
        Assert.DoesNotContain(paths.PluginsPath, path, StringComparison.Ordinal);
        Assert.DoesNotContain(paths.PluginConfigurationsPath, path, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProvider(out IServiceCollection collection)
    {
        collection = new ServiceCollection();
        collection.AddSingleton<IApplicationPaths>(new FakePaths());
        collection.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        new PluginServiceRegistrator().RegisterServices(collection, applicationHost: null!);

        return collection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private sealed class FakePaths : IApplicationPaths
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flynn-di", Guid.NewGuid().ToString("N"));

        public string ProgramDataPath => _root;

        public string WebPath => Path.Combine(_root, "web");

        public string ProgramSystemPath => _root;

        public string DataPath => Path.Combine(_root, "data");

        public string ImageCachePath => Path.Combine(_root, "imagecache");

        public string PluginsPath => Path.Combine(_root, "plugins");

        public string PluginConfigurationsPath => Path.Combine(_root, "plugins", "configurations");

        public string LogDirectoryPath => Path.Combine(_root, "log");

        public string ConfigurationDirectoryPath => Path.Combine(_root, "config");

        public string SystemConfigurationFilePath => Path.Combine(_root, "config", "system.xml");

        public string CachePath => Path.Combine(_root, "cache");

        public string TempDirectory => Path.Combine(_root, "temp");

        public string VirtualDataPath => Path.Combine(_root, "virtual");

        public string TrickplayPath => Path.Combine(_root, "trickplay");

        public string BackupPath => Path.Combine(_root, "backup");

        // Startup-only checks the server performs on its own directories. Nothing under test calls
        // them, and doing real filesystem work here would make the container tests touch disk.
        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
