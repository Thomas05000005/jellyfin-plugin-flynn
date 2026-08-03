using System.Globalization;
using Jellyfin.Plugin.Flynn.Core.Localization;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// A missing translation is invisible until someone with that language opens the page, which is
/// why the completeness checks here matter more than the resolution ones.
/// </summary>
public class FlynnStringsTests
{
    [Fact]
    public void EveryLanguage_DefinesEveryKey()
    {
        foreach (var language in FlynnStrings.AvailableLanguages)
        {
            var catalogue = FlynnStrings.Load(language);
            var missing = StringKeys.All.Where(k => !catalogue.ContainsKey(k)).ToList();

            Assert.True(
                missing.Count == 0,
                $"Catalogue '{language}' is missing: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// The other direction. A key left in a catalogue after its call site was deleted is dead
    /// weight translators keep maintaining, and a key spelled differently in one language reads as
    /// an extra here rather than as a silent fallback to English.
    /// </summary>
    [Fact]
    public void NoLanguage_DefinesKeysThatDoNotExist()
    {
        foreach (var language in FlynnStrings.AvailableLanguages)
        {
            var extra = FlynnStrings.Load(language).Keys.Except(StringKeys.All).ToList();

            Assert.True(
                extra.Count == 0,
                $"Catalogue '{language}' defines keys that are not in StringKeys: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void EveryDeclaredLanguage_IsActuallyEmbedded()
    {
        var embedded = FlynnStrings.EmbeddedCatalogueNames();

        foreach (var language in FlynnStrings.AvailableLanguages)
        {
            Assert.Contains(embedded, n => n.EndsWith($".Strings.{language}.json", StringComparison.Ordinal));
        }

        // And nothing embedded that we do not declare, which would be a catalogue nobody can reach.
        Assert.Equal(FlynnStrings.AvailableLanguages.Count, embedded.Count);
    }

    [Fact]
    public void AKnownCulture_GetsItsOwnText()
    {
        var french = FlynnStrings.ForCulture(new CultureInfo("fr-FR"));
        var english = FlynnStrings.ForCulture(new CultureInfo("en-GB"));

        Assert.Equal("Indisponible", french.Get(StringKeys.ModuleUnavailableHeadline));
        Assert.Equal("Unavailable", english.Get(StringKeys.ModuleUnavailableHeadline));
    }

    [Fact]
    public void ARegionalVariant_ResolvesAgainstItsLanguage()
    {
        var quebecois = FlynnStrings.ForCulture(new CultureInfo("fr-CA"));

        Assert.Equal("Indisponible", quebecois.Get(StringKeys.ModuleUnavailableHeadline));
    }

    [Fact]
    public void AnUnsupportedCulture_FallsBackToEnglish()
    {
        var japanese = FlynnStrings.ForCulture(new CultureInfo("ja-JP"));

        Assert.Equal("Unavailable", japanese.Get(StringKeys.ModuleUnavailableHeadline));
    }

    /// <summary>
    /// A blank label looks like a layout bug and gets ignored; the bracketed key names the thing to
    /// add and where.
    /// </summary>
    [Fact]
    public void AnUnknownKey_RendersVisiblyRatherThanBlank()
    {
        var strings = FlynnStrings.ForCulture(CultureInfo.InvariantCulture);

        Assert.Equal("[nope.not.a.key]", strings.Get("nope.not.a.key"));
    }

    [Fact]
    public void AMalformedTemplate_DoesNotTakeThePageDown()
    {
        var strings = FlynnStrings.ForCulture(CultureInfo.InvariantCulture);

        // No placeholder to fill, but arguments supplied: must not throw.
        var text = strings.Get(StringKeys.ModuleDisabledHeadline, "unexpected");

        Assert.Equal("Off", text);
    }

    [Fact]
    public void FrenchText_KeepsItsAccents()
    {
        var french = FlynnStrings.ForCulture(new CultureInfo("fr-FR"));

        // The pure-ASCII rule applies to the injected client script, which some Jellyfin clients
        // decode as latin1. It does not apply to a UTF-8 JSON catalogue, and unaccented French
        // reads as broken to the people it is for.
        Assert.Contains("é", french.Get(StringKeys.ModuleUnavailableDetail), StringComparison.Ordinal);
    }
}
