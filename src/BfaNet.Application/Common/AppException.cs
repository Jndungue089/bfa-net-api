namespace BfaNet.Application.Common;

public static class ErrorCodes
{
    public const string Validation = "validation_error";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string InvalidPin = "invalid_pin";
    public const string PinLocked = "pin_locked";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string InsufficientFunds = "insufficient_funds";
    public const string LimitExceeded = "limit_exceeded";
    public const string IdempotencyMismatch = "idempotency_mismatch";
    public const string RateLimited = "rate_limited";
    public const string TokenReuse = "token_reuse";
}

/// <summary>Expected, user-facing failure. Message is safe to return to the client.</summary>
public sealed class AppException(string code, string message, int status = 400, IDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public IDictionary<string, string[]>? Errors { get; } = errors;

    public static AppException NotFound(string what = "Recurso", bool feminine = false) =>
        new(ErrorCodes.NotFound, $"{what} {(feminine ? "não encontrada" : "não encontrado")}.", 404);
    public static AppException Conflict(string msg) => new(ErrorCodes.Conflict, msg, 409);
    public static AppException Invalid(string field, string msg) =>
        new(ErrorCodes.Validation, "Dados inválidos.", 422, new Dictionary<string, string[]> { [field] = [msg] });
}
