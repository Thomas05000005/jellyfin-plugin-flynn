using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Flynn.Api;

/// <summary>
/// Serves Flynn's embedded browser assets.
/// <para>
/// All anonymous, and they have to be: a browser sends no authorization header for a
/// <c>&lt;script src&gt;</c> or a <c>&lt;link href&gt;</c>. None of them carry data — everything
/// they display comes from the authenticated endpoints they call afterwards, which is where the
/// access check belongs.
/// </para>
/// </summary>
[ApiController]
[Route("Flynn")]
[AllowAnonymous]
public sealed class AssetsController : ControllerBase
{
    private const string Prefix = "Jellyfin.Plugin.Flynn.Client.";

    /// <summary>Returns the client runtime injected into the web UI.</summary>
    /// <returns>The script.</returns>
    [HttpGet("client.js")]
    public ActionResult GetClientScript() => Serve("runtime.flynn.js", MediaTypeNames.Text.JavaScript);

    /// <summary>Returns the admin page's script.</summary>
    /// <returns>The script.</returns>
    [HttpGet("admin.js")]
    public ActionResult GetAdminScript() => Serve("admin.admin.js", MediaTypeNames.Text.JavaScript);

    /// <summary>Returns the admin page's stylesheet.</summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("admin.css")]
    public ActionResult GetAdminStyles() => Serve("admin.admin.css", MediaTypeNames.Text.Css);

    /// <summary>
    /// Serves an embedded asset with an ETag tied to the plugin version.
    /// <para>
    /// <c>no-cache</c> rather than <c>max-age</c>, so the browser revalidates every time and picks
    /// up a new version the moment it is installed. A max-age here is how you ship a fix that
    /// nobody receives for a week.
    /// </para>
    /// </summary>
    /// <param name="suffix">Resource name after the namespace prefix.</param>
    /// <param name="contentType">MIME type to answer with.</param>
    /// <returns>The asset, 304, or 500 when the resource is missing from the build.</returns>
    private ActionResult Serve(string suffix, string contentType)
    {
        var etag = $"\"{Plugin.Instance?.Version?.ToString() ?? "0"}\"";

        Response.Headers.CacheControl = "public, no-cache, must-revalidate";
        Response.Headers.ETag = etag;

        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var name = Prefix + suffix;
        var stream = typeof(AssetsController).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            // The EmbeddedResource entry was lost. Saying so beats serving an empty file, which
            // would look like a plugin that works and simply does nothing.
            return Problem(
                detail: $"Embedded asset '{name}' is missing from the assembly.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return File(stream, contentType);
    }
}
