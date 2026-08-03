using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Flynn.Api;

/// <summary>
/// Works out which language to answer a request in.
/// <para>
/// Reads <c>Accept-Language</c>, which is what the browser already sends and what the admin has
/// already configured once, in their browser, for every site. Asking them to set it again in a
/// plugin would be a setting that exists only to be wrong.
/// </para>
/// </summary>
internal static class RequestCulture
{
    /// <summary>Picks the culture for a request, falling back to the invariant culture.</summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The culture to render text in.</returns>
    internal static CultureInfo For(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FromAcceptLanguage(request.Headers.AcceptLanguage.ToString());
    }

    /// <summary>
    /// Parses an <c>Accept-Language</c> header value.
    /// <para>
    /// Deliberately simple: the highest-weighted tag we can actually serve, or the invariant
    /// culture. Quality values are honoured because a browser sending
    /// <c>en;q=0.9, fr;q=1.0</c> means it, but everything beyond that is more machinery than a
    /// two-language catalogue can justify.
    /// </para>
    /// </summary>
    /// <param name="header">The raw header value. May be empty.</param>
    /// <returns>The best matching culture.</returns>
    internal static CultureInfo FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return CultureInfo.InvariantCulture;
        }

        var best = header
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseEntry)
            .Where(entry => entry.Tag.Length > 0 && entry.Quality > 0)
            .OrderByDescending(entry => entry.Quality)
            .Select(entry => entry.Tag)
            .FirstOrDefault();

        if (best is null || best == "*")
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return new CultureInfo(best);
        }
        catch (CultureNotFoundException)
        {
            // A malformed tag is a client problem, not a reason to fail the request.
            return CultureInfo.InvariantCulture;
        }
    }

    private static (string Tag, double Quality) ParseEntry(string entry)
    {
        var parts = entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tag = parts.Length > 0 ? parts[0] : string.Empty;

        var quality = 1.0d;
        foreach (var parameter in parts.Skip(1))
        {
            if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(
                    parameter.AsSpan(2),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                quality = parsed;
            }
        }

        return (tag, quality);
    }
}
