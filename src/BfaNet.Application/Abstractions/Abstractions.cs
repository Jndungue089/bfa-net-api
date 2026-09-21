using BfaNet.Application.Contracts;
using BfaNet.Domain.Entities;

namespace BfaNet.Application.Abstractions;

/// <summary>Per-request identity and client context, populated by the API layer.</summary>
public interface IRequestContext
{
    Guid? CustomerId { get; }
    Guid? SessionFamilyId { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    bool IsWebClient { get; }
    Guid RequireCustomerId();
}

/// <summary>AES-256-GCM field encryption with key versioning (envelope: v1.{keyId}.{base64(nonce|tag|cipher)}).</summary>
public interface IFieldEncryptor
{
    /// <param name="context">Bound as AAD (e.g. "customers.email") so ciphertexts cannot be swapped between columns.</param>
    string Encrypt(string plaintext, string context);
    string Decrypt(string envelope, string context);
}

/// <summary>Keyed HMAC-SHA256 blind index for exact-match lookups over encrypted columns.</summary>
public interface IBlindIndex
{
    string Compute(string purpose, string normalizedValue);
}

public interface ISecretHasher
{
    string Hash(string secret);
    bool Verify(string secret, string hash);
    /// <summary>Burns comparable CPU time when the account does not exist (timing side-channel mitigation).</summary>
    void DummyVerify();
}

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Customer customer, Guid familyId);
    (string Raw, string Hash) CreateRefreshToken();
    string HashRefreshToken(string raw);
}

public interface IAuditWriter
{
    Task RecordAsync(string action, bool success, Guid? customerId = null, string? detail = null);
}

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<AuthResult> RefreshAsync(string? refreshToken, CancellationToken ct);
    Task LogoutAsync(string? refreshToken, CancellationToken ct);
    Task LogoutAllAsync(CancellationToken ct);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct);
    Task ChangePinAsync(ChangePinRequest request, CancellationToken ct);
    Task<ProfileResponse> GetProfileAsync(CancellationToken ct);
    Task<IReadOnlyList<ActiveSessionDto>> ListSessionsAsync(CancellationToken ct);
    Task RevokeSessionAsync(Guid familyId, CancellationToken ct);
    Task<BiometricEnrollResponse> EnrollBiometricAsync(BiometricEnrollRequest request, CancellationToken ct);
    Task<AuthResult> BiometricLoginAsync(BiometricLoginRequest request, CancellationToken ct);
    Task DisableBiometricAsync(BiometricDisableRequest request, CancellationToken ct);
    /// <summary>Checks the operations PIN (with the same lockout as payments) without moving money.</summary>
    Task VerifyPinAsync(VerifyPinRequest request, CancellationToken ct);
}

public interface IAccountService
{
    Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken ct);
    Task<AccountDto> GetAsync(Guid id, CancellationToken ct);
    Task<AccountDto> RenameAsync(Guid id, RenameAccountRequest request, CancellationToken ct);
    Task<StatementPage> StatementAsync(Guid id, StatementQuery query, CancellationToken ct);
}

public interface ITransferService
{
    Task<ResolveIbanResponse> ResolveIbanAsync(ResolveIbanRequest request, CancellationToken ct);
    Task<TransactionReceipt> TransferAsync(Guid idempotencyKey, TransferRequest request, CancellationToken ct);
    Task<TransactionReceipt> PayServiceAsync(Guid idempotencyKey, ServicePaymentRequest request, CancellationToken ct);
    Task<TransactionReceipt> RechargeAsync(Guid idempotencyKey, RechargeRequest request, CancellationToken ct);
    Task<TransactionReceipt> PayStateAsync(Guid idempotencyKey, StatePaymentRequest request, CancellationToken ct);
    Task<KwikResolveResponse> ResolveKwikAsync(KwikResolveRequest request, CancellationToken ct);
    Task<TransactionReceipt> KwikTransferAsync(Guid idempotencyKey, KwikTransferRequest request, CancellationToken ct);
    Task<TransactionReceipt> GetReceiptAsync(Guid transactionId, CancellationToken ct);
}

public interface IBeneficiaryService
{
    Task<IReadOnlyList<BeneficiaryDto>> ListAsync(CancellationToken ct);
    Task<BeneficiaryDto> CreateAsync(BeneficiaryRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface ICardService
{
    Task<IReadOnlyList<CardDto>> ListAsync(CancellationToken ct);
    Task<CardDto> UpdateAsync(Guid id, UpdateCardRequest request, CancellationToken ct);
}

public interface IAssistantService
{
    Task<AssistantInsights> InsightsAsync(CancellationToken ct);
    Task<CreditOfferDto> CreditOfferAsync(CancellationToken ct);
    Task<CreditSimulationDto> SimulateAsync(decimal amount, int months, CancellationToken ct);
    Task<LoanDto> AcceptCreditAsync(Guid idempotencyKey, AcceptCreditRequest request, CancellationToken ct);
    Task<IReadOnlyList<LoanDto>> LoansAsync(CancellationToken ct);
    Task<TransactionReceipt> RepayAsync(Guid idempotencyKey, Guid loanId, RepayLoanRequest request, CancellationToken ct);
}

public interface IPublicInfoService
{
    Task<IReadOnlyList<ExchangeRateDto>> ExchangeRatesAsync(CancellationToken ct);
    IReadOnlyList<ProductDto> Products();
    IReadOnlyList<ContactDto> Contacts();
    AboutInfo About();
}

public sealed record ReceiptPdf(byte[] Content, string FileName);

public interface IStatementPdfService
{
    /// <summary>Statement PDF of an account the caller owns. Defaults: current month up to today (Luanda time).</summary>
    Task<ReceiptPdf> BuildAsync(Guid accountId, DateOnly? from, DateOnly? to, CancellationToken ct);
}

public interface IReceiptPdfService
{
    /// <summary>PDF comprovativo of a transaction the caller took part in (as payer or payee).</summary>
    Task<ReceiptPdf> BuildAsync(Guid transactionId, CancellationToken ct);
}

public sealed record AvatarFile(byte[] Data, string ContentType, DateTimeOffset UpdatedAt);

public interface IAvatarService
{
    /// <summary>
    /// Accepts JPEG, PNG or WebP up to <see cref="MaxUploadBytes"/> (type detected from the bytes), then crops it
    /// square, resizes and re-encodes it as a clean JPEG of at most <see cref="MaxStoredBytes"/>.
    /// </summary>
    Task SaveAsync(byte[] original, CancellationToken ct);
    Task<AvatarFile?> GetAsync(CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
    const int MaxUploadBytes = 10 * 1024 * 1024;
    const int MaxStoredBytes = 300 * 1024;
}
