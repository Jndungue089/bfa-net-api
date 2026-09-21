namespace BfaNet.Application;

public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Base64 32-byte keys by id, e.g. { "k1": "..." }. Old keys stay for decrypting.</summary>
    public Dictionary<string, string> EncryptionKeys { get; set; } = [];
    public string ActiveEncryptionKeyId { get; set; } = "k1";
    /// <summary>Base64 key (>= 32 bytes) for HMAC blind indexes.</summary>
    public string BlindIndexKey { get; set; } = "";
    /// <summary>Base64 key (>= 32 bytes) for HS256 JWT signing.</summary>
    public string JwtSigningKey { get; set; } = "";
    public string JwtIssuer { get; set; } = "bfanet-api";
    public string JwtAudience { get; set; } = "bfanet-clients";
    public int AccessTokenMinutes { get; set; } = 10;
    public int RefreshTokenDays { get; set; } = 7;
    public int MaxLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MaxPinAttempts { get; set; } = 3;
    public int PinLockoutMinutes { get; set; } = 30;
    public bool SecureCookies { get; set; } = true;
    public string AccessCookieName { get; set; } = "bfa_at";
    public string RefreshCookieName { get; set; } = "bfa_rt";
    /// <summary>Content-free "a session exists" flag (Path=/) so the Next.js proxy can redirect without seeing the JWT.</summary>
    public string SessionFlagCookieName { get; set; } = "bfa_sess";
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class BankingOptions
{
    public const string Section = "Banking";

    public string DefaultBranch { get; set; } = "0001";
    public decimal PerTransferLimit { get; set; } = 5_000_000m;
    public decimal DailyLimit { get; set; } = 10_000_000m;
    /// <summary>Flat fee (AOA) for transfers to other banks.</summary>
    public decimal InterbankFee { get; set; } = 150m;
    public bool OpenRegistration { get; set; } = true;
}
