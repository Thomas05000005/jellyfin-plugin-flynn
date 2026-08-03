using Jellyfin.Plugin.Flynn.Core.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Delivery is the one thing that, if wrong, makes every client feature silently absent. The path
/// filter also decides what gets buffered in memory, so it is a correctness question and a
/// performance one at once.
/// </summary>
public class ScriptInjectionTests
{
    private const string Tag = "<script id=\"flynn-client\" src=\"/Flynn/client.js?v=1.0.0\" defer></script>";

    [Fact]
    public void TheTag_GoesJustBeforeTheClosingBody()
    {
        var result = ScriptInjectionMiddleware.InjectIntoHtml("<html><body><div>x</div></body></html>", Tag);

        Assert.Equal("<html><body><div>x</div>" + Tag + "</body></html>", result);
    }

    [Fact]
    public void WithNoBody_ItFallsBackToTheClosingHtml()
    {
        var result = ScriptInjectionMiddleware.InjectIntoHtml("<html><head></head></html>", Tag);

        Assert.Equal("<html><head></head>" + Tag + "</html>", result);
    }

    [Fact]
    public void WithNeither_ItAppends()
    {
        var result = ScriptInjectionMiddleware.InjectIntoHtml("<div>fragment</div>", Tag);

        Assert.Equal("<div>fragment</div>" + Tag, result);
    }

    /// <summary>
    /// Two delivery paths could each insert the tag. Loading the client twice means duplicated
    /// listeners and duplicated network calls, so the second insertion has to be a no-op.
    /// </summary>
    [Fact]
    public void ADocumentThatAlreadyHasTheTag_IsLeftAlone()
    {
        var already = "<html><body>" + Tag + "</body></html>";

        Assert.Equal(already, ScriptInjectionMiddleware.InjectIntoHtml(already, Tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyDocument_IsLeftAlone(string? html)
    {
        Assert.Equal(html, ScriptInjectionMiddleware.InjectIntoHtml(html!, Tag));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/web")]
    [InlineData("/web/")]
    [InlineData("/web/index.html")]
    public void TheSpaShell_IsInspected(string path)
    {
        Assert.True(ScriptInjectionMiddleware.IsWebUiDocumentPath(new PathString(path)));
    }

    /// <summary>
    /// Everything here would otherwise be buffered into memory on its way to the client, media
    /// streams included.
    /// </summary>
    [Theory]
    [InlineData("/System/Info/Public")]
    [InlineData("/Items/abc/Images/Primary")]
    [InlineData("/web/main.js")]
    [InlineData("/web/assets/style.css")]
    [InlineData("/Videos/abc/stream.mkv")]
    [InlineData("/Flynn/client.js")]
    public void EverythingElse_IsStreamedThrough(string path)
    {
        Assert.False(ScriptInjectionMiddleware.IsWebUiDocumentPath(new PathString(path)));
    }

    [Fact]
    public void APlainHtmlSuccess_IsRewritten()
    {
        Assert.True(ScriptInjectionMiddleware.ShouldRewrite(Response("text/html; charset=utf-8", 200)));
    }

    /// <summary>
    /// Decoding gzip or brotli bytes as UTF-8 produces garbage, and inserting a tag into that
    /// corrupts the document. Compression lives in the reverse proxy today, i.e. after us, but the
    /// day it moves into the pipeline this is what stops us mangling every page.
    /// </summary>
    [Fact]
    public void ACompressedBody_IsHandedBackUntouched()
    {
        var response = Response("text/html", 200);
        response.Headers.ContentEncoding = "gzip";

        Assert.False(ScriptInjectionMiddleware.ShouldRewrite(response));
    }

    [Theory]
    [InlineData("application/json", 200)]
    [InlineData("text/html", 304)]
    [InlineData("text/html", 302)]
    [InlineData("text/html", 500)]
    [InlineData(null, 200)]
    public void AnythingElse_IsHandedBackUntouched(string? contentType, int status)
    {
        Assert.False(ScriptInjectionMiddleware.ShouldRewrite(Response(contentType, status)));
    }

    private static HttpResponse Response(string? contentType, int statusCode)
    {
        var context = new DefaultHttpContext();
        context.Response.ContentType = contentType;
        context.Response.StatusCode = statusCode;
        return context.Response;
    }
}
