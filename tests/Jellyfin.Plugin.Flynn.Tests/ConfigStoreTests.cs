using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Flynn.Configuration;
using Jellyfin.Plugin.Flynn.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The store's promises: no lost update between concurrent writers, and nothing reaches the disk
/// unless it has been proved to reload.
/// </summary>
public class ConfigStoreTests
{
    /// <summary>
    /// U+0001 is a perfectly ordinary .NET string character and an illegal one in XML 1.0. An
    /// admin pasting from a badly-behaved source is all it takes.
    /// </summary>
    private const string Unpersistable = "stor\u0001age";

    /// <summary>
    /// Documents the hazard the store exists to defend against, by reproducing it directly.
    /// <para>
    /// If this test ever goes green on the write half and green on the read half, Jellyfin has
    /// started validating characters and the round-trip gate could be reconsidered. Until then the
    /// gate is load-bearing.
    /// </para>
    /// </summary>
    [Fact]
    public void TheHazardIsReal_TheWriteSucceedsSilentlyAndOnlyTheNextReadFails()
    {
        var poisoned = new PluginConfiguration();
        poisoned.Modules.Add(new ModuleToggle { Id = Unpersistable, Enabled = true });

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();

        // Exactly how the server writes: XmlTextWriter, no character checking.
        var writer = new XmlTextWriter(buffer, System.Text.Encoding.UTF8) { Formatting = Formatting.Indented };
        var writeException = Record.Exception(() =>
        {
            serializer.Serialize(writer, poisoned);
            writer.Flush();
        });

        // The write is the quiet part. Nothing complains, and a file would now exist on disk.
        Assert.Null(writeException);
        Assert.True(buffer.Length > 0);

        // The next boot is where it surfaces — and there, Jellyfin swallows it and returns defaults.
        // XmlSerializer wraps the real cause, so the inner exception is what identifies it as a
        // character problem rather than any other kind of read failure.
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { CheckCharacters = true });
        var readException = Assert.Throws<InvalidOperationException>(() => serializer.Deserialize(reader));
        Assert.IsType<XmlException>(readException.InnerException);
    }

    [Fact]
    public async Task AnUnpersistableChange_IsRefusedAndChangesNothing()
    {
        var host = new FakeHost();
        using var store = new ConfigStore(host, NullLogger<ConfigStore>.Instance);

        await Assert.ThrowsAsync<ConfigurationRejectedException>(() =>
            store.MutateAsync(
                c => c.Modules.Add(new ModuleToggle { Id = Unpersistable, Enabled = true }),
                CancellationToken.None));

        Assert.Empty(host.Current.Modules);
        Assert.Equal(0, host.CommitCount);
    }

    [Fact]
    public async Task AThrowingMutation_LeavesTheStoredConfigurationUntouched()
    {
        var host = new FakeHost();
        host.Current.Modules.Add(new ModuleToggle { Id = "storage", Enabled = true });
        using var store = new ConfigStore(host, NullLogger<ConfigStore>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MutateAsync(
                c =>
                {
                    c.Modules.Clear();
                    throw new InvalidOperationException("changed my mind halfway through");
                },
                CancellationToken.None));

        Assert.Equal("storage", Assert.Single(host.Current.Modules).Id);
        Assert.Equal(0, host.CommitCount);
    }

    /// <summary>
    /// The reason the store exists. Fifty callers each adding one entry must end with fifty
    /// entries: an unsynchronised read-modify-write against the shared instance loses most of them.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriters_DoNotLoseEachOthersChanges()
    {
        const int Writers = 50;
        var host = new FakeHost();
        using var store = new ConfigStore(host, NullLogger<ConfigStore>.Instance);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(i =>
            Task.Run(() => store.MutateAsync(
                c => c.Modules.Add(new ModuleToggle { Id = $"module-{i}", Enabled = true }),
                CancellationToken.None))));

        Assert.Equal(Writers, host.Current.Modules.Count);
        Assert.Equal(Writers, host.Current.Modules.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public async Task TheDraftIsPrivate_UntilItIsCommitted()
    {
        var host = new FakeHost();
        using var store = new ConfigStore(host, NullLogger<ConfigStore>.Instance);
        PluginConfiguration? draft = null;

        await store.MutateAsync(
            c =>
            {
                draft = c;
                c.Modules.Add(new ModuleToggle { Id = "storage", Enabled = true });

                // Mid-mutation, the shared instance must still be the old one.
                Assert.Empty(host.Current.Modules);
            },
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.NotSame(draft, host.Current);
        Assert.Equal("storage", Assert.Single(host.Current.Modules).Id);
        Assert.Equal(1, host.CommitCount);
    }

    /// <summary>
    /// Stands in for <c>BasePlugin&lt;T&gt;</c>: holds one cached instance and replaces it wholesale
    /// on commit, which is what the real one does.
    /// </summary>
    private sealed class FakeHost : IConfigurationHost
    {
        private PluginConfiguration _current = new();

        public int CommitCount { get; private set; }

        public PluginConfiguration Current => _current;

        public void Commit(PluginConfiguration configuration)
        {
            _current = configuration;
            CommitCount++;
        }
    }
}
