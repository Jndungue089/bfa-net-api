using System.Security.Claims;
using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BfaNet.Api.Http;

public sealed class RequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public const string ClientHeader = "X-BFA-Client";
    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("Sem contexto HTTP.");

    public Guid? CustomerId => Guid.TryParse(Http.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;
    public Guid? SessionFamilyId => Guid.TryParse(Http.User.FindFirstValue("sid"), out var id) ? id : null;
    public string? IpAddress => Http.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
    public bool IsWebClient => string.Equals(Http.Request.Headers[ClientHeader], "web", StringComparison.OrdinalIgnoreCase);

    public Guid RequireCustomerId() =>
        CustomerId ?? throw new AppException(ErrorCodes.Unauthorized, "Sessão inválida.", 401);
}
