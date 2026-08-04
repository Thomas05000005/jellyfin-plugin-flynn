namespace Jellyfin.Plugin.Flynn.Core.Localization;

/// <summary>
/// A piece of user-facing text, kept as a key and its arguments rather than as a resolved string.
/// <para>
/// It stays unresolved until it reaches the boundary that knows who is asking, so two admins with
/// different languages get their own text from the same computation. Resolving early would bake in
/// whatever culture the background task happened to run under.
/// </para>
/// </summary>
/// <param name="Key">One of the constants on <see cref="StringKeys"/>.</param>
/// <param name="Args">Values for the <c>{0}</c>, <c>{1}</c>... placeholders.</param>
public sealed record LocalizedText(string Key, IReadOnlyList<object?> Args)
{
    /// <summary>Builds a piece of text from a key and its arguments.</summary>
    /// <param name="key">One of the constants on <see cref="StringKeys"/>.</param>
    /// <param name="args">Values for the placeholders, in order.</param>
    /// <returns>The unresolved text.</returns>
    public static LocalizedText Of(string key, params object?[] args) => new(key, args);

    /// <summary>Resolves this text with a given resolver.</summary>
    /// <param name="strings">The resolver for the reader's culture.</param>
    /// <returns>The translated text.</returns>
    public string Resolve(FlynnStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        // An argument can itself be translatable -- a size and its unit, a count and the thing
        // being counted. Resolving nested text here keeps the unit out of the caller's hands, so
        // "19.5 TB" and "19,5 To" come from the same computation.
        var resolved = Args
            .Select(arg => arg is LocalizedText nested ? nested.Resolve(strings) : arg)
            .ToArray();

        return strings.Get(Key, resolved);
    }
}
