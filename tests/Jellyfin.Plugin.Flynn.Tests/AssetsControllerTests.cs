using Jellyfin.Plugin.Flynn.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// The three files the browser fetches.
/// <para>
/// This controller had no tests at all, which matters more than it looks: it is the only anonymous
/// surface in the plugin, and the only one whose failure mode is silence. A missing embedded
/// resource served as an empty file produces a plugin that loads, reports healthy, and simply does
/// nothing -- and nobody would have a reason to look at the server log.
/// </para>
/// </summary>
public class AssetsControllerTests
{
    /// <summary>
    /// The one that would have been noticed last. A csproj edit that drops an EmbeddedResource
    /// entry breaks nothing at build time.
    /// </summary>
    [Theory]
    [InlineData("Jellyfin.Plugin.Flynn.Client.runtime.flynn.js")]
    [InlineData("Jellyfin.Plugin.Flynn.Client.admin.admin.js")]
    [InlineData("Jellyfin.Plugin.Flynn.Client.admin.admin.css")]
    public void EveryServedAsset_IsActuallyEmbeddedInTheAssembly(string name)
    {
        using var stream = typeof(AssetsController).Assembly.GetManifestResourceStream(name);

        Assert.NotNull(stream);
        Assert.True(stream!.Length > 0, $"{name} is embedded but empty.");
    }

    [Theory]
    [InlineData("client.js")]
    [InlineData("admin.js")]
    [InlineData("admin.css")]
    public void EachRoute_IsDeclaredAndAnonymous(string route)
    {
        var method = typeof(AssetsController)
            .GetMethods()
            .Single(m => m.GetCustomAttributes(typeof(HttpGetAttribute), false)
                .Cast<HttpGetAttribute>()
                .Any(a => a.Template == route));

        Assert.NotNull(method);
    }

    [Fact]
    public void TheAssets_AreServedWithoutRequiringAuthorization()
    {
        // A browser sends no authorization header for a script src or a link href, so requiring one
        // here would mean the admin page silently loses its styling and its behaviour.
        var attributes = typeof(AssetsController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true);

        Assert.NotEmpty(attributes);
    }

    [Fact]
    public void AMatchingETag_AnswersNotModifiedRatherThanResendingTheFile()
    {
        var controller = Controller();
        // Plugin.Instance is null outside a server, so the version falls back to "0" -- which is
        // exactly the value the controller will compare against.
        controller.Request.Headers.IfNoneMatch = "\"0\"";

        var result = controller.GetAdminScript();

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public void AStaleETag_GetsTheFile()
    {
        var controller = Controller();
        controller.Request.Headers.IfNoneMatch = "\"0.4.0.0\"";

        var result = controller.GetAdminScript();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/javascript", file.ContentType);
    }

    /// <summary>
    /// no-cache, never max-age. A max-age here is how a fix ships and nobody receives it for a
    /// week, because the browser never asks again.
    /// </summary>
    [Fact]
    public void TheCachingHeaders_MakeTheBrowserRevalidateEveryTime()
    {
        var controller = Controller();

        controller.GetAdminStyles();

        var cacheControl = controller.Response.Headers.CacheControl.ToString();
        Assert.Contains("no-cache", cacheControl, StringComparison.Ordinal);
        Assert.Contains("must-revalidate", cacheControl, StringComparison.Ordinal);
        Assert.DoesNotContain("max-age", cacheControl, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(controller.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public void TheStylesheet_IsServedAsCssAndNotAsScript()
    {
        var file = Assert.IsType<FileStreamResult>(Controller().GetAdminStyles());

        Assert.Equal("text/css", file.ContentType);
    }

    private static AssetsController Controller() =>
        new()
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
