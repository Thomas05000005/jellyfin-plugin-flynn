using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Flynn.Modules.Music;

/// <summary>
/// Finds the libraries a music audit should look at.
/// <para>
/// Queried rather than read off <c>RootFolder.Children</c>, so this goes through the interface like
/// everything else and can be exercised without standing up a server.
/// </para>
/// </summary>
internal static class MusicLibraries
{
    /// <summary>Lists every library whose content type is music.</summary>
    /// <param name="library">The server's library manager.</param>
    /// <returns>The music libraries.</returns>
    internal static IReadOnlyList<BaseItem> Of(ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return [.. library
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.CollectionFolder] })
            .Where(folder => library.GetContentType(folder) == CollectionType.music)];
    }
}
