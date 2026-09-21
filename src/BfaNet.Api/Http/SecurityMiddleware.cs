using BfaNet.Application;
using Microsoft.Extensions.Options;

namespace BfaNet.Api.Http;

public static class SecurityMiddleware
{
    /// <summary>Strict defaults for a JSON-only API: nothing here should ever be framed, sniffed or cached.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app, bool hsts) =>
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Cache-Control"] = "no-store";
            h["Pragma"] = "no-cache";
            if (hsts) h["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
            await next();
        });

    /// <summary>
    /// CSRF defence for cookie-authenticated browsers: state-changing requests must carry the custom
    /// X-BFA-Client header (which cross-site forms cannot set and CORS is not enabled to allow) and,
    /// when the browser sends an Origin, it must be one of ours. Cookies are also SameSite=Strict.
    /// </summary>
    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
            var unsafeMethod = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method);
            var usesCookies = ctx.Request.Cookies.ContainsKey(opts.AccessCookieName) || ctx.Request.Cookies.ContainsKey(opts.RefreshCookieName);

            if (unsafeMethod && usesCookies)
            {
                var hasHeader = string.Equals(ctx.Request.Headers[RequestContext.ClientHeader], "web", StringComparison.OrdinalIgnoreCase);
                var origin = ctx.Request.Headers.Origin.ToString();
                var originOk = origin.Length == 0 || opts.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
                if (!hasHeader || !originOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { status = 403, title = "Pedido rejeitado.", code = "csrf" });
                    return;
                }
            }
            await next();
        });
}
