using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.Flynn.Core.Localization;

/// <summary>
/// Resolves translatable strings from the catalogues embedded in the assembly.
/// <para>
/// English is the source language and the fallback. A key missing from a translation falls back to
/// English; a key missing from English too is rendered as the key itself, in brackets. That is
/// deliberately ugly: a blank label looks like a layout bug and gets ignored, whereas
/// <c>[issue.storage.title]</c> on screen tells you exactly what to add and where.
/// </para>
/// </summary>
public sealed class FlynnStrings
{
    private const string SourceLanguage = "en";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Catalogues = new();

    private readonly IReadOnlyDictionary<string, string> _preferred;
    private readonly IReadOnlyDictionary<string, string> _fallback;

    private FlynnStrings(
        IReadOnlyDictionary<string, string> preferred,
        IReadOnlyDictionary<string, string> fallback)
    {
        _preferred = preferred;
        _fallback = fallback;
    }

    /// <summary>Gets the language codes that ship with the plugin.</summary>
    public static IReadOnlyList<string> AvailableLanguages { get; } = ["en", "fr"];

    /// <summary>
    /// Gets a resolver for a culture, falling back to the neutral language and then to English.
    /// </summary>
    /// <param name="culture">
    /// The requested culture. <c>fr-CA</c> resolves against the <c>fr</c> catalogue; anything with
    /// no catalogue gets English.
    /// </param>
    /// <returns>A resolver. Cheap to call repeatedly: catalogues are parsed once per language.</returns>
    public static FlynnStrings ForCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var language = culture.TwoLetterISOLanguageName;
        var english = Load(SourceLanguage);
        return new FlynnStrings(
            AvailableLanguages.Contains(language) ? Load(language) : english,
            english);
    }

    /// <summary>Resolves a key, substituting positional arguments.</summary>
    /// <param name="key">One of the constants on <see cref="StringKeys"/>.</param>
    /// <param name="args">
    /// Values for the <c>{0}</c>, <c>{1}</c>... placeholders. Extra arguments are ignored; a
    /// placeholder with no matching argument is left as-is rather than throwing, because a label
    /// reading <c>{1}</c> is a bug report and a crashed dashboard is an outage.
    /// </param>
    /// <returns>The translated text.</returns>
    public string Get(string key, params object?[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_preferred.TryGetValue(key, out var template)
            && !_fallback.TryGetValue(key, out template))
        {
            return $"[{key}]";
        }

        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // A malformed template is a translator error, not a reason to take a page down.
            return template;
        }
    }

    /// <summary>Loads and caches one catalogue. Exposed for the completeness tests.</summary>
    /// <param name="language">Two-letter language code.</param>
    /// <returns>The key/value pairs for that language.</returns>
    internal static IReadOnlyDictionary<string, string> Load(string language) =>
        Catalogues.GetOrAdd(language, static lang =>
        {
            var assembly = typeof(FlynnStrings).Assembly;
            var name = $"{typeof(FlynnStrings).Namespace!.Replace(".Core.Localization", string.Empty, StringComparison.Ordinal)}.Strings.{lang}.json";

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    $"Missing embedded string catalogue '{name}'. Check the EmbeddedResource entry in the csproj.");

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"String catalogue '{name}' is empty or malformed.");
        });

    /// <summary>Lists the embedded catalogue resource names. Used by tests to detect an unregistered file.</summary>
    /// <returns>The manifest resource names under Strings/.</returns>
    internal static IReadOnlyList<string> EmbeddedCatalogueNames() =>
        [.. typeof(FlynnStrings).Assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Strings.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)];
}
