using System.Globalization;
using Jellyfin.Plugin.Flynn.Api;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Language selection reads a header the browser already sends, so the failure mode is answering
/// everyone in English and nobody noticing until a French admin says the plugin "is not
/// translated".
/// </summary>
public class RequestCultureTests
{
    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-FR,fr;q=0.9,en;q=0.8", "fr")]
    [InlineData("en-GB,en;q=0.9", "en")]
    public void TheFirstUsableTag_Wins(string header, string expectedLanguage)
    {
        var culture = RequestCulture.FromAcceptLanguage(header);

        Assert.Equal(expectedLanguage, culture.TwoLetterISOLanguageName);
    }

    /// <summary>A browser sending q-values means them, so order in the header is not the answer.</summary>
    [Fact]
    public void QualityValues_BeatHeaderOrder()
    {
        var culture = RequestCulture.FromAcceptLanguage("en;q=0.5, fr;q=0.9");

        Assert.Equal("fr", culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void ATagWithZeroQuality_IsRefused()
    {
        // "de;q=0" is the browser explicitly saying "not German".
        var culture = RequestCulture.FromAcceptLanguage("de;q=0, fr;q=0.4");

        Assert.Equal("fr", culture.TwoLetterISOLanguageName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("*")]
    public void NoPreference_FallsBackToInvariant(string? header)
    {
        Assert.Equal(CultureInfo.InvariantCulture, RequestCulture.FromAcceptLanguage(header));
    }

    /// <summary>A malformed header is a client problem, not a reason to fail the request.</summary>
    [Theory]
    [InlineData("this is not a language tag")]
    [InlineData("zz-ZZ-ZZ-ZZ")]
    [InlineData(";;;")]
    public void AMalformedHeader_DoesNotThrow(string header)
    {
        var culture = RequestCulture.FromAcceptLanguage(header);

        Assert.NotNull(culture);
    }

    /// <summary>
    /// A language with no catalogue must still resolve to something, and the resolver falls back to
    /// English for it.
    /// </summary>
    [Fact]
    public void AnUnsupportedLanguage_StillProducesAUsableCulture()
    {
        var culture = RequestCulture.FromAcceptLanguage("ja-JP");

        Assert.Equal("ja", culture.TwoLetterISOLanguageName);
    }
}
