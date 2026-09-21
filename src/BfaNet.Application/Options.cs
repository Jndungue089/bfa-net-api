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

/// <summary>Financial assistant and microcredit policy. Everything here is evaluated on the server; clients only display it.</summary>
public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    public int MinHistoryDays { get; set; } = 30;
    public int MinIncomeMonths { get; set; } = 2;
    public decimal MinMonthlyIncome { get; set; } = 30_000m;
    public int MinHealthScore { get; set; } = 40;
    /// <summary>Instalment may not exceed this share of average monthly income.</summary>
    public decimal InstallmentToIncomeCap { get; set; } = 0.30m;
    public decimal MaxOffer { get; set; } = 1_000_000m;
    public decimal MinOffer { get; set; } = 20_000m;
    public int[] Terms { get; set; } = [3, 6, 9, 12];
    public decimal OriginationFeePercent { get; set; } = 1.0m;
    /// <summary>Annual rate by health tier: ≥ 75, ≥ 60, otherwise.</summary>
    public decimal RateTop { get; set; } = 18m;
    public decimal RateMid { get; set; } = 24m;
    public decimal RateBase { get; set; } = 30m;
}
