using BfaNet.Application;
using BfaNet.Application.Contracts;

namespace BfaNet.Api.Http;

public static class AuthCookies
{
    public const string RefreshPath = "/api/v1/auth";

    /// <summary>Web gets HttpOnly cookies (tokens never reach JS); mobile gets tokens in the body for secure storage.</summary>
    public static SessionResponse Deliver(HttpContext http, SecurityOptions o, AuthResult r, bool web)
    {
        var expiresIn = (int)(r.AccessExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
        if (!web) return new SessionResponse(r.AccessToken, r.RefreshToken, expiresIn, r.Profile);

        http.Response.Cookies.Append(o.AccessCookieName, r.AccessToken, Options(o, "/api", r.AccessExpiresAt));
        http.Response.Cookies.Append(o.RefreshCookieName, r.RefreshToken, Options(o, RefreshPath, r.RefreshExpiresAt));
        http.Response.Cookies.Append(o.SessionFlagCookieName, "1", Options(o, "/", r.RefreshExpiresAt));
        return new SessionResponse(null, null, expiresIn, r.Profile);
    }

    public static void Clear(HttpContext http, SecurityOptions o)
    {
        http.Response.Cookies.Delete(o.AccessCookieName, Options(o, "/api", null));
        http.Response.Cookies.Delete(o.RefreshCookieName, Options(o, RefreshPath, null));
        http.Response.Cookies.Delete(o.SessionFlagCookieName, Options(o, "/", null));
    }

    private static CookieOptions Options(SecurityOptions o, string path, DateTimeOffset? expires) => new()
    {
        HttpOnly = true, Secure = o.SecureCookies, SameSite = SameSiteMode.Strict, Path = path, Expires = expires, IsEssential = true
    };
}
