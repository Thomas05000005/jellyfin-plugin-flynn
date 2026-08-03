using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Web;

/// <summary>
/// Puts Flynn's client script into the Jellyfin web UI by rewriting the <c>index.html</c> response
/// body in flight. The file on disk is never touched.
/// <para>
/// Every other approach in the ecosystem either patches jellyfin-web on disk, which fails on Docker
/// installs where the plugin cannot write to the web root, or leans on the third-party JavaScript
/// Injector and File Transformation pair. That pair has no Jellyfin 12 build as of 2026-08, so
/// depending on it would make this plugin a silent no-op on the only servers it targets. Rewriting
/// the response needs no filesystem access, no third-party plugin, and survives server upgrades
/// because nothing is persisted.
/// </para>
/// <para>
/// Deliberately conservative: it only buffers a response when the request looks like the web UI
/// document itself, and any failure falls through to the original bytes. Breaking the web UI to
/// deliver a banner would be a poor trade.
/// </para>
/// </summary>
public sealed class ScriptInjectionMiddleware
{
    /// <summary>
    /// Marker on the injected tag. Makes a double injection detectable and gives the client a way
    /// to find its own element.
    /// </summary>
    internal const string ScriptElementId = "flynn-client";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    /// <summary>Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.</summary>
    /// <param name="next">Next middleware in the pipeline.</param>
    /// <param name="logger">Logger.</param>
    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the tag inserted before <c>&lt;/body&gt;</c>.
    /// <para>
    /// Versioned by the assembly version so a plugin upgrade busts the browser cache; the endpoint
    /// serving the script answers with a matching ETag.
    /// </para>
    /// </summary>
    /// <param name="version">The plugin version.</param>
    /// <returns>The script tag.</returns>
    internal static string BuildScriptTag(string version) =>
        $"<script id=\"{ScriptElementId}\" src=\"/Flynn/client.js?v={version}\" defer></script>";

    /// <summary>
    /// Inserts <paramref name="tag"/> before the closing body tag, falling back to the closing html
    /// tag and then to appending.
    /// <para>
    /// Returns the document unchanged when the marker is already present, so two delivery paths
    /// cannot load the client twice.
    /// </para>
    /// </summary>
    /// <param name="html">The document.</param>
    /// <param name="tag">The tag to insert.</param>
    /// <returns>The document, with the tag present exactly once.</returns>
    internal static string InjectIntoHtml(string html, string tag)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(ScriptElementId, StringComparison.Ordinal))
        {
            return html;
        }

        var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = html.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        }

        return index < 0 ? html + tag : html.Insert(index, tag);
    }

    /// <summary>
    /// True when the request targets the SPA shell rather than an asset or an API call.
    /// <para>
    /// Jellyfin serves the shell at <c>/</c>, <c>/web</c>, <c>/web/</c> and <c>/web/index.html</c>.
    /// Everything else is skipped, so a media stream or an API payload is never buffered.
    /// </para>
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>Whether the response is worth inspecting.</returns>
    internal static bool IsWebUiDocumentPath(PathString path)
    {
        if (!path.HasValue)
        {
            return true; // "/" arrives as an empty PathString.
        }

        var value = path.Value!;
        return value.Length == 0
            || value == "/"
            || value.Equals("/web", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/web/", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Processes a request.</summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsGet(context.Request.Method) || !IsWebUiDocumentPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            buffer.Seek(0, SeekOrigin.Begin);
            context.Response.Body = originalBody;

            if (!ShouldRewrite(context.Response))
            {
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            var version = Plugin.Instance?.Version?.ToString() ?? "0";
            var injected = InjectIntoHtml(Encoding.UTF8.GetString(buffer.ToArray()), BuildScriptTag(version));
            var bytes = Encoding.UTF8.GetBytes(injected);

            // The body grew. A stale Content-Length truncates the document in the browser.
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Never break the web UI on our account.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flynn script injection failed; serving the original response.");
            context.Response.Body = originalBody;
            if (buffer.Length > 0 && !context.Response.HasStarted)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            }
        }
#pragma warning restore CA1031
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>Decides whether a buffered response may be rewritten.</summary>
    /// <param name="response">The response, after the rest of the pipeline has run.</param>
    /// <returns>True only for a plain, successful HTML document.</returns>
    internal static bool ShouldRewrite(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != StatusCodes.Status200OK)
        {
            return false;
        }

        // A compressed body is not text. Decoding gzip or brotli bytes as UTF-8 produces garbage,
        // and inserting a tag into it corrupts the document. Compression happens in the reverse
        // proxy today, i.e. after us, but if the server ever enables it inside the pipeline the
        // bytes have to be handed back untouched rather than mangled.
        if (response.Headers.ContentEncoding.Count > 0)
        {
            return false;
        }

        return response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true;
    }
}
