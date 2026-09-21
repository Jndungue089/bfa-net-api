using System.Security.Cryptography;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using BfaNet.Domain;
using BfaNet.Domain.Entities;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BfaNet.Infrastructure.Services;

public sealed class AuthService(
    BankDbContext db, IRequestContext ctx, ISecretHasher hasher, IBlindIndex blind, ITokenService tokens,
    CredentialAttempts attempts, IAuditWriter audit, IOptions<SecurityOptions> secOpts, IOptions<BankingOptions> bankOpts,
    TimeProvider clock) : IAuthService
{
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AbsoluteSessionLifetime = TimeSpan.FromDays(30);
    private readonly SecurityOptions _sec = secOpts.Value;
    private readonly BankingOptions _bank = bankOpts.Value;

    // ---------- Registration ----------

    public async Task<AuthResult> RegisterAsync(RegisterRequest r, CancellationToken ct)
    {
        if (!_bank.OpenRegistration) throw new AppException(ErrorCodes.Forbidden, "O registo online está desactivado.", 403);

        var name = TextSanitizer.Clean(r.FullName);
        var email = TextSanitizer.NormalizeEmail(r.Email);
        var phone = TextSanitizer.NormalizePhone(r.Phone);
        var nationalId = TextSanitizer.NormalizeNationalId(r.NationalId);
        var emailHash = blind.Compute("email", email);
        var phoneHash = blind.Compute("phone", phone);
        var idHash = blind.Compute("national_id", nationalId);

        // Deliberately generic: do not reveal which identifier is already registered.
        var duplicate = new AppException(ErrorCodes.Conflict,
            "Não foi possível concluir o registo com os dados fornecidos. Se já é cliente, contacte o balcão.", 409);

        if (await db.Customers.AnyAsync(c => c.EmailHash == emailHash || c.PhoneHash == phoneHash || c.NationalIdHash == idHash, ct))
        {
            await audit.RecordAsync("auth.register", false, detail: "duplicate");
            throw duplicate;
        }

        var now = clock.GetUtcNow();
        var pinHash = hasher.Hash(r.Pin);
        var passwordHash = hasher.Hash(r.Password);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var customer = new Customer
            {
                CustomerNumber = RandomDigits(8, nonZeroLead: true),
                FullName = name, Email = email, EmailHash = emailHash, Phone = phone, PhoneHash = phoneHash,
                NationalId = nationalId, NationalIdHash = idHash, TaxId = TextSanitizer.CleanOrNull(r.TaxId),
                BirthDate = r.BirthDate, PasswordHash = passwordHash, PinHash = pinHash,
                PasswordChangedAt = now, CreatedAt = now, UpdatedAt = now
            };
            var accountNumber = RandomDigits(11, nonZeroLead: false);
            var account = new Account
            {
                CustomerId = customer.Id, AccountNumber = accountNumber, Type = AccountType.Ordem,
                Iban = Iban.Generate(_bank.DefaultBranch, accountNumber), OpenedAt = now
            };
            var card = new Card
            {
                CustomerId = customer.Id, AccountId = account.Id, Product = CardProduct.Debito,
                ProductName = "Cartão de Débito BFA", Last4 = RandomDigits(4, false), HolderName = name.ToUpperInvariant(),
                ExpiryMonth = now.Month, ExpiryYear = now.Year + 4, DailyLimit = 200_000m, CreatedAt = now
            };

            db.Customers.Add(customer);
            db.Accounts.Add(account);
            db.Cards.Add(card);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                db.ChangeTracker.Clear();
                // Random-number collision → retry; identity collision (race) → generic conflict.
                if (pg.ConstraintName?.Contains("customer_number") == true || pg.ConstraintName?.Contains("account_number") == true
                    || pg.ConstraintName?.Contains("iban") == true) continue;
                await audit.RecordAsync("auth.register", false, detail: "duplicate-race");
                throw duplicate;
            }

            await audit.RecordAsync("auth.register", true, customer.Id);
            return await IssueSessionAsync(customer, Guid.CreateVersion7(), now, ct);
        }
        throw new AppException(ErrorCodes.Conflict, "Não foi possível concluir o registo. Tente novamente.", 409);
    }

    // ---------- Login ----------

    public async Task<AuthResult> LoginAsync(LoginRequest r, CancellationToken ct)
    {
        var invalid = new AppException(ErrorCodes.InvalidCredentials, "Número de adesão ou palavra-passe incorrectos.", 401);
        var now = clock.GetUtcNow();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.CustomerNumber == r.CustomerNumber, ct);

        if (customer is null)
        {
            hasher.DummyVerify();
            await audit.RecordAsync("auth.login", false, detail: "unknown-user");
            throw invalid;
        }

        if (customer.Status != CustomerStatus.Active || customer.IsLockedOut(now))
        {
            hasher.DummyVerify();
            await audit.RecordAsync("auth.login", false, customer.Id, "locked");
            throw new AppException(ErrorCodes.AccountLocked,
                "Acesso temporariamente bloqueado. Tente novamente mais tarde ou contacte o BFA.", 423);
        }

        if (!hasher.Verify(r.Password, customer.PasswordHash))
        {
            await attempts.RegisterLoginFailureAsync(customer.Id, ct);
            await audit.RecordAsync("auth.login", false, customer.Id, "bad-password");
            throw invalid;
        }

        await attempts.ResetLoginAsync(customer.Id, ct);
        await audit.RecordAsync("auth.login", true, customer.Id);
        return await IssueSessionAsync(customer, Guid.CreateVersion7(), now, ct);
    }

    // ---------- Refresh (rotation + reuse detection) ----------

    public async Task<AuthResult> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        var unauthorized = new AppException(ErrorCodes.Unauthorized, "Sessão expirada. Inicie sessão novamente.", 401);
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 128) throw unauthorized;

        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw unauthorized;

        if (stored.RevokedAt is { } revokedAt)
        {
            // A just-rotated token showing up again is usually two tabs racing; anything later is theft.
            if (stored.ReplacedById is null || now - revokedAt > RotationGrace)
            {
                await RevokeFamilyAsync(stored.CustomerId, stored.FamilyId, now, ct);
                await audit.RecordAsync("auth.token_reuse", false, stored.CustomerId, $"family={stored.FamilyId}");
            }
            throw unauthorized;
        }

        if (stored.ExpiresAt <= now || now - stored.SessionStartedAt > AbsoluteSessionLifetime) throw unauthorized;

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == stored.CustomerId, ct) ?? throw unauthorized;
        if (customer.Status != CustomerStatus.Active) throw unauthorized;

        var (raw, next) = NewRefreshToken(customer.Id, stored.FamilyId, now, stored.SessionStartedAt);
        stored.RevokedAt = now;
        stored.ReplacedById = next.Id;
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);

        var (access, accessExp) = tokens.CreateAccessToken(customer, stored.FamilyId);
        return new AuthResult(access, accessExp, raw, next.ExpiresAt, await ToProfileAsync(customer, ct));
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 128) return;
        var hash = tokens.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return;
        await RevokeFamilyAsync(stored.CustomerId, stored.FamilyId, clock.GetUtcNow(), ct);
        await audit.RecordAsync("auth.logout", true, stored.CustomerId);
    }

    public async Task LogoutAllAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var now = clock.GetUtcNow();
        await db.RefreshTokens.Where(t => t.CustomerId == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await RevokeDeviceCredentialsAsync(id, now, ct);
        await audit.RecordAsync("auth.logout_all", true, id);
    }

    // ---------- Credentials ----------

    public async Task ChangePasswordAsync(ChangePasswordRequest r, CancellationToken ct)
    {
        var customer = await CurrentCustomerAsync(ct);
        if (!hasher.Verify(r.CurrentPassword, customer.PasswordHash))
        {
            await attempts.RegisterLoginFailureAsync(customer.Id, ct);
            await audit.RecordAsync("auth.change_password", false, customer.Id, "bad-current");
            throw AppException.Invalid(nameof(r.CurrentPassword), "Palavra-passe actual incorrecta.");
        }

        if (CredentialPolicy.ValidatePassword(r.NewPassword, customer.CustomerNumber, customer.FullName) is { } msg)
            throw AppException.Invalid(nameof(r.NewPassword), msg);

        var now = clock.GetUtcNow();
        customer.PasswordHash = hasher.Hash(r.NewPassword);
        customer.PasswordChangedAt = now;
        customer.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        // Everything except the session that made the change is signed out.
        var keep = ctx.SessionFamilyId;
        await db.RefreshTokens.Where(t => t.CustomerId == customer.Id && t.RevokedAt == null && t.FamilyId != keep)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        // A new password invalidates every biometric enrolment: the device must prove the password again.
        await RevokeDeviceCredentialsAsync(customer.Id, now, ct);
        await audit.RecordAsync("auth.change_password", true, customer.Id);
    }

    public async Task ChangePinAsync(ChangePinRequest r, CancellationToken ct)
    {
        var customer = await CurrentCustomerAsync(ct);
        var now = clock.GetUtcNow();
        if (customer.IsPinLockedOut(now))
            throw new AppException(ErrorCodes.PinLocked, "PIN bloqueado temporariamente.", 423);

        if (!hasher.Verify(r.CurrentPin, customer.PinHash))
        {
            await attempts.RegisterPinFailureAsync(customer.Id, ct);
            await audit.RecordAsync("auth.change_pin", false, customer.Id, "bad-current");
            throw new AppException(ErrorCodes.InvalidPin, "PIN actual incorrecto.", 422);
        }

        customer.PinHash = hasher.Hash(r.NewPin);
        customer.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await attempts.ResetPinAsync(customer.Id, ct);
        await audit.RecordAsync("auth.change_pin", true, customer.Id);
    }

    // ---------- Profile & sessions ----------

    public async Task<ProfileResponse> GetProfileAsync(CancellationToken ct) => await ToProfileAsync(await CurrentCustomerAsync(ct), ct);

    public async Task<IReadOnlyList<ActiveSessionDto>> ListSessionsAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var now = clock.GetUtcNow();
        var live = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.CustomerId == id && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        return live.Select(t => new ActiveSessionDto(t.FamilyId, t.DeviceLabel, t.IpAddress, t.SessionStartedAt, t.FamilyId == ctx.SessionFamilyId)).ToList();
    }

    public async Task RevokeSessionAsync(Guid familyId, CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        await RevokeFamilyAsync(id, familyId, clock.GetUtcNow(), ct);
        await audit.RecordAsync("auth.revoke_session", true, id, $"family={familyId}");
    }

    // ---------- Biometric login (per-device credential) ----------

    private const int MaxDeviceCredentials = 5;

    public async Task<BiometricEnrollResponse> EnrollBiometricAsync(BiometricEnrollRequest r, CancellationToken ct)
    {
        var customer = await CurrentCustomerAsync(ct);
        // Enrolling a device is as sensitive as logging in: the password must be proven again.
        if (!hasher.Verify(r.Password, customer.PasswordHash))
        {
            await attempts.RegisterLoginFailureAsync(customer.Id, ct);
            await audit.RecordAsync("auth.biometric_enroll", false, customer.Id, "bad-password");
            throw AppException.Invalid(nameof(r.Password), "Palavra-passe incorrecta.");
        }

        var now = clock.GetUtcNow();
        var live = await db.DeviceCredentials.Where(d => d.CustomerId == customer.Id && d.RevokedAt == null).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        foreach (var old in live.Take(Math.Max(0, live.Count - (MaxDeviceCredentials - 1)))) old.RevokedAt = now;

        var (raw, hash) = tokens.CreateRefreshToken(); // 48 random bytes, hashed at rest
        db.DeviceCredentials.Add(new DeviceCredential
        {
            CustomerId = customer.Id, TokenHash = hash, CreatedAt = now,
            DeviceLabel = TextSanitizer.CleanOrNull(r.DeviceLabel) ?? DescribeDevice(ctx.UserAgent)
        });
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("auth.biometric_enroll", true, customer.Id);
        return new BiometricEnrollResponse(raw);
    }

    public async Task<AuthResult> BiometricLoginAsync(BiometricLoginRequest r, CancellationToken ct)
    {
        var invalid = new AppException(ErrorCodes.InvalidCredentials, "Não foi possível autenticar com biometria. Entre com a palavra-passe.", 401);
        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(r.DeviceToken);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.CustomerNumber == r.CustomerNumber, ct);
        var credential = customer is null ? null
            : await db.DeviceCredentials.FirstOrDefaultAsync(d => d.CustomerId == customer.Id && d.TokenHash == hash && d.RevokedAt == null, ct);

        if (customer is null || credential is null)
        {
            await audit.RecordAsync("auth.biometric_login", false, customer?.Id, "bad-credential");
            throw invalid;
        }
        if (customer.Status != CustomerStatus.Active || customer.IsLockedOut(now))
        {
            await audit.RecordAsync("auth.biometric_login", false, customer.Id, "locked");
            throw new AppException(ErrorCodes.AccountLocked, "Acesso temporariamente bloqueado. Tente novamente mais tarde ou contacte o BFA.", 423);
        }

        credential.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        await attempts.ResetLoginAsync(customer.Id, ct);
        await audit.RecordAsync("auth.biometric_login", true, customer.Id);
        return await IssueSessionAsync(customer, Guid.CreateVersion7(), now, ct);
    }

    public async Task DisableBiometricAsync(BiometricDisableRequest r, CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var hash = tokens.HashRefreshToken(r.DeviceToken);
        var now = clock.GetUtcNow();
        await db.DeviceCredentials.Where(d => d.CustomerId == id && d.TokenHash == hash && d.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), ct);
        await audit.RecordAsync("auth.biometric_disable", true, id);
    }

    public async Task VerifyPinAsync(VerifyPinRequest r, CancellationToken ct)
    {
        var customer = await CurrentCustomerAsync(ct);
        var now = clock.GetUtcNow();
        if (customer.IsPinLockedOut(now)) throw new AppException(ErrorCodes.PinLocked, "PIN bloqueado temporariamente.", 423);
        if (!hasher.Verify(r.Pin, customer.PinHash))
        {
            await attempts.RegisterPinFailureAsync(customer.Id, ct);
            await audit.RecordAsync("auth.verify_pin", false, customer.Id, "bad-pin");
            throw new AppException(ErrorCodes.InvalidPin, "PIN incorrecto.", 422);
        }
        await attempts.ResetPinAsync(customer.Id, ct);
    }

    private Task RevokeDeviceCredentialsAsync(Guid customerId, DateTimeOffset now, CancellationToken ct) =>
        db.DeviceCredentials.Where(d => d.CustomerId == customerId && d.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), ct);

    // ---------- helpers ----------

    private async Task<Customer> CurrentCustomerAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        return await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new AppException(ErrorCodes.Unauthorized, "Sessão inválida.", 401);
    }

    private Task RevokeFamilyAsync(Guid customerId, Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.CustomerId == customerId && t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private async Task<AuthResult> IssueSessionAsync(Customer customer, Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var (raw, token) = NewRefreshToken(customer.Id, familyId, now, now);
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(ct);
        var (access, accessExp) = tokens.CreateAccessToken(customer, familyId);
        return new AuthResult(access, accessExp, raw, token.ExpiresAt, await ToProfileAsync(customer, ct));
    }

    private (string Raw, RefreshToken Token) NewRefreshToken(Guid customerId, Guid familyId, DateTimeOffset now, DateTimeOffset sessionStart)
    {
        var (raw, hash) = tokens.CreateRefreshToken();
        var expires = now.AddDays(_sec.RefreshTokenDays);
        var hardCap = sessionStart + AbsoluteSessionLifetime;
        return (raw, new RefreshToken
        {
            CustomerId = customerId, FamilyId = familyId, TokenHash = hash, CreatedAt = now, SessionStartedAt = sessionStart,
            ExpiresAt = expires < hardCap ? expires : hardCap,
            DeviceLabel = DescribeDevice(ctx.UserAgent), IpAddress = ctx.IpAddress
        });
    }

    private async Task<ProfileResponse> ToProfileAsync(Customer c, CancellationToken ct)
    {
        var avatar = await db.CustomerAvatars.AsNoTracking().Where(a => a.CustomerId == c.Id)
            .Select(a => (DateTimeOffset?)a.UpdatedAt).FirstOrDefaultAsync(ct);
        return new ProfileResponse(c.Id, c.CustomerNumber, c.FullName, c.Email, c.Phone, TextSanitizer.MaskNationalId(c.NationalId),
            c.BirthDate, c.LastLoginAt, avatar?.ToUnixTimeMilliseconds());
    }

    private static string RandomDigits(int length, bool nonZeroLead)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(i == 0 && nonZeroLead ? 1 : 0, 10));
        return new string(chars);
    }

    private static string? DescribeDevice(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        var os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : null;
        var app = ua.Contains("BFANET-Mobile") ? "App BFA NET" : ua.Contains("Firefox") ? "Firefox"
            : ua.Contains("Edg/") ? "Edge" : ua.Contains("Chrome") ? "Chrome" : ua.Contains("Safari") ? "Safari" : "Browser";
        return os is null ? app : $"{app} · {os}";
    }
}
