using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace BfaNet.Api.Http;

public static class RateLimiting
{
    public const string Auth = "auth";
    public const string Money = "money";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection s, IConfiguration cfg)
    {
        int Limit(string key, int fallback) => cfg.GetValue<int?>($"RateLimits:{key}") ?? fallback;
        int global = Limit("GlobalPerMinute", 240), auth = Limit("AuthPerMinute", 10), money = Limit("MoneyPerMinute", 15);

        return s.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new { status = 429, title = "Demasiados pedidos. Aguarde um momento.", code = "rate_limited" }, ct);
            };

            // Everything: 240 req/min per user (or per IP when anonymous).
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => Partition(http, global, TimeSpan.FromMinutes(1)));

            // Credential endpoints: strict per-IP budget, the main brute-force / enumeration brake.
            o.AddPolicy(Auth, http => RateLimitPartition.GetSlidingWindowLimiter($"auth:{Ip(http)}",
                _ => new SlidingWindowRateLimiterOptions { PermitLimit = auth, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));

            // Money movement: per customer.
            o.AddPolicy(Money, http => RateLimitPartition.GetSlidingWindowLimiter($"money:{User(http) ?? Ip(http)}",
                _ => new SlidingWindowRateLimiterOptions { PermitLimit = money, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
        });
    }

    private static RateLimitPartition<string> Partition(HttpContext http, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(User(http) ?? Ip(http),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });

    private static string? User(HttpContext http) => http.User.FindFirstValue("sub");
    private static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
