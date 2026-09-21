using BfaNet.Domain;

namespace BfaNet.Application.Contracts;

// ---- Auth ----
public sealed record RegisterRequest(
    string FullName, string Email, string Phone, string NationalId, string? TaxId,
    DateOnly BirthDate, string Password, string Pin, bool AcceptTerms);

public sealed record LoginRequest(string CustomerNumber, string Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ChangePinRequest(string CurrentPin, string NewPin);

/// <summary>Internal result; the API decides whether tokens go in cookies (web) or the body (mobile).</summary>
public sealed record AuthResult(
    string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, ProfileResponse Profile);

public sealed record SessionResponse(
    string? AccessToken, string? RefreshToken, int ExpiresInSeconds, ProfileResponse Profile);

public sealed record ProfileResponse(
    Guid Id, string CustomerNumber, string FullName, string Email, string Phone,
    string NationalId, DateOnly BirthDate, DateTimeOffset? LastLoginAt, long? AvatarVersion);

public sealed record BiometricEnrollRequest(string Password, string? DeviceLabel);
public sealed record BiometricEnrollResponse(string DeviceToken);
public sealed record BiometricLoginRequest(string CustomerNumber, string DeviceToken);
public sealed record BiometricDisableRequest(string DeviceToken);
public sealed record VerifyPinRequest(string Pin);

public sealed record ActiveSessionDto(Guid FamilyId, string? Device, string? IpAddress, DateTimeOffset CreatedAt, bool Current);

// ---- Accounts ----
public sealed record AccountDto(
    Guid Id, string Iban, string AccountNumber, AccountType Type, Currency Currency,
    decimal Balance, AccountStatus Status, string? Nickname, DateTimeOffset OpenedAt);

public sealed record RenameAccountRequest(string? Nickname);

public sealed record StatementQuery(
    DateOnly? From, DateOnly? To, LedgerDirection? Direction, long? Cursor, int? Limit);

public sealed record StatementItem(
    long EntryId, Guid TransactionId, string Reference, TransactionKind Kind, LedgerDirection Direction,
    decimal Amount, decimal BalanceAfter, string? Description, string? Counterparty, DateTimeOffset CreatedAt);

public sealed record StatementPage(IReadOnlyList<StatementItem> Items, long? NextCursor);

// ---- Money movement ----
public sealed record TransferRequest(
    Guid FromAccountId, string ToIban, string? BeneficiaryName, decimal Amount, string? Description, string Pin);

public sealed record ResolveIbanRequest(string Iban);
public sealed record ResolveIbanResponse(bool Valid, bool InternalAccount, string? HolderMasked, string? BankName);

public sealed record ServicePaymentRequest(Guid FromAccountId, string EntityCode, string Reference, decimal Amount, string Pin);

/// <summary>Unitel/Africell are mobile top-ups (identifier = phone); Dstv/Zap are TV subscriptions; Ende is electricity.</summary>
public enum RechargeProvider { Unitel, Africell, Dstv, Zap, Ende }
public sealed record RechargeRequest(Guid FromAccountId, RechargeProvider Provider, string Identifier, decimal Amount, string Pin);

public sealed record StatePaymentRequest(Guid FromAccountId, string Reference, decimal Amount, string Pin);

/// <summary>KWiK key = the recipient's mobile number.</summary>
public sealed record KwikResolveRequest(string Key);
public sealed record KwikResolveResponse(bool Found, string? HolderMasked);
public sealed record KwikTransferRequest(Guid FromAccountId, string Key, decimal Amount, string? Description, string Pin);

public sealed record TransactionReceipt(
    Guid TransactionId, string Reference, TransactionKind Kind, TransactionStatus Status,
    decimal Amount, decimal Fee, Currency Currency, string? Description, string? CounterpartyName,
    string? CounterpartyIban, decimal BalanceAfter, DateTimeOffset CreatedAt,
    /// <summary>From the viewer's side: Debit = money left their account, Credit = money arrived.</summary>
    LedgerDirection Direction, string? OriginatorName);

// ---- Beneficiaries ----
public sealed record BeneficiaryRequest(string Name, string Iban);
public sealed record BeneficiaryDto(Guid Id, string Name, string Iban, string? BankName, DateTimeOffset CreatedAt);

// ---- Cards ----
public sealed record CardDto(
    Guid Id, Guid AccountId, CardProduct Product, string ProductName, string Last4, string HolderName,
    int ExpiryMonth, int ExpiryYear, CardStatus Status, bool OnlinePurchases, bool Contactless,
    bool AtmWithdrawals, bool InternationalPayments, decimal DailyLimit);

public sealed record UpdateCardRequest(
    bool? Blocked, bool? OnlinePurchases, bool? Contactless, bool? AtmWithdrawals, bool? InternationalPayments, decimal? DailyLimit);

// ---- Public ----
public sealed record ExchangeRateDto(Currency Currency, decimal Buy, decimal Sell, string Source, DateTimeOffset UpdatedAt);
public sealed record ProductDto(string Code, string Category, string Name, string Summary, string Url);
/// <summary>Kind: phone | email | web | branch. Value is display text; Url is a tel:/mailto:/https link.</summary>
public sealed record ContactDto(string Kind, string Label, string Value, string Url);
public sealed record AboutInfo(string Title, string Url);
