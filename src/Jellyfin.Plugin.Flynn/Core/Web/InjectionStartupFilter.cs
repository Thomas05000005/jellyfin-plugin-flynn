using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Flynn.Core.Web;

/// <summary>
/// Puts <see cref="ScriptInjectionMiddleware"/> into the server's request pipeline.
/// <para>
/// An <c>IStartupFilter</c> is how a plugin adds middleware without the server having to know it
/// exists: registering one in DI is enough for ASP.NET Core to wrap the application builder.
/// </para>
/// <para>
/// The middleware goes in first so it is outermost, wrapping everything that could produce the SPA
/// shell. Placed later it would sit behind whatever middleware ends up serving the document, and
/// would never see the body it needs to rewrite.
/// </para>
/// </summary>
public sealed class InjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            builder.UseMiddleware<ScriptInjectionMiddleware>();
            next(builder);
        };
    }
}
