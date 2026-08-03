using System.Net.Mime;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Flynn.Api;

/// <summary>Serves Flynn's client runtime to the browser.</summary>
[ApiController]
[Route("Flynn")]
public sealed class ClientScriptController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.Flynn.Client.runtime.flynn.js";

    /// <summary>
    /// Returns the client runtime.
    /// <para>
    /// Anonymous by necessity: the browser fetches this from a <c>&lt;script src&gt;</c> tag, which
    /// carries no authorization header. It contains no data — everything it displays comes from
    /// authenticated endpoints it calls afterwards.
    /// </para>
    /// <para>
    /// Cached by ETag tied to the assembly version, with <c>no-cache</c> so the browser always
    /// revalidates. A plugin upgrade changes the version, the ETag changes with it, and the new
    /// script is fetched. Switching this to <c>max-age</c> is how you ship a fix that nobody
    /// receives for a week.
    /// </para>
    /// </summary>
    /// <returns>The script, or 304 when the caller's copy is current.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var version = Plugin.Instance?.Version?.ToString() ?? "0";
        var etag = $"\"{version}\"";

        Response.Headers.CacheControl = "public, no-cache, must-revalidate";
        Response.Headers.ETag = etag;

        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var stream = typeof(ClientScriptController).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // The embedded resource is missing from the build, which means the EmbeddedResource
            // entry in the csproj was lost. Say so rather than serving an empty script that would
            // look like a working but inert plugin.
            return Problem(
                detail: $"Embedded client runtime '{ResourceName}' is missing from the assembly.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return File(stream, MediaTypeNames.Text.JavaScript);
    }
}
