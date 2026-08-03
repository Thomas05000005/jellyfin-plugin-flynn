using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Flynn.Configuration;

namespace Jellyfin.Plugin.Flynn.Core.Config;

/// <summary>
/// Round-trips the configuration through XML in memory, mirroring how the server persists it.
/// <para>
/// This exists because of one measured behaviour of Jellyfin's serialiser
/// (<c>Emby.Server.Implementations/Serialization/MyXmlSerializer.cs</c>): it writes through an
/// <see cref="XmlTextWriter"/> with character checking off, so a control character in a
/// user-supplied string is written <b>successfully and silently</b>. Nothing fails. The file only
/// becomes unreadable on the NEXT load — at which point <c>BasePlugin&lt;T&gt;.LoadConfiguration</c>
/// swallows the exception in a bare <c>catch { }</c> (it has no logger) and hands back a fresh
/// default. Every setting is gone, and nothing was ever logged.
/// </para>
/// <para>
/// Worse, the write is not atomic: <c>FileMode.Create</c> truncates the existing file before the
/// first byte is written, with no temp file and no backup, so a mid-serialisation throw leaves a
/// half-written document where the good configuration used to be.
/// </para>
/// <para>
/// So we reproduce that exact write here, in memory, and then read it back. Anything that would
/// have become an unreadable file at the next reboot instead throws now, while the real file is
/// still intact.
/// </para>
/// </summary>
internal static class ConfigSerialization
{
    /// <summary>
    /// Serialises then deserialises <paramref name="configuration"/> in memory, returning the
    /// deserialised copy.
    /// <para>
    /// Doubles as a deep clone and as the validation gate: if the value cannot survive the trip,
    /// it must never reach the disk.
    /// </para>
    /// </summary>
    /// <param name="configuration">The configuration to round-trip.</param>
    /// <returns>An independent copy that is known to persist and reload cleanly.</returns>
    /// <exception cref="InvalidOperationException">
    /// Either the value cannot be written at all (a <c>Dictionary&lt;,&gt;</c> property, say, which
    /// <see cref="XmlSerializer"/> refuses outright), or it writes but produces a document that
    /// cannot be read back — the silent-corruption case this class exists to catch. The two are
    /// told apart by the inner exception, which is an <see cref="XmlException"/> in the second
    /// case. <see cref="XmlSerializer"/> wraps read failures, so the raw
    /// <see cref="XmlException"/> never surfaces on its own.
    /// </exception>
    internal static PluginConfiguration RoundTrip(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();

        // Deliberately permissive, exactly like the server: XmlTextWriter does not check
        // characters. Using a stricter writer here would reject the poison pill at write time and
        // hide the very failure mode we are trying to reproduce.
        var writer = new XmlTextWriter(buffer, System.Text.Encoding.UTF8) { Formatting = Formatting.Indented };
        serializer.Serialize(writer, configuration);
        writer.Flush();

        buffer.Position = 0;

        // Strict on the way back in — this is the read that would otherwise happen at next boot.
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { CheckCharacters = true });
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }
}
