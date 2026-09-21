using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using FluentValidation;

namespace BfaNet.Application.Validators;

internal static class Rules
{
    public static IRuleBuilderOptions<T, string> PersonName<T>(this IRuleBuilder<T, string> r) =>
        r.Must(v => Patterns.PersonName.IsMatch(TextSanitizer.Clean(v))).WithMessage("Nome inválido.");

    public static IRuleBuilderOptionsConditions<T, string> Pin<T>(this IRuleBuilder<T, string> r) =>
        r.Custom((v, ctx) => { if (CredentialPolicy.ValidatePin(v) is { } m) ctx.AddFailure(m); });

    public static IRuleBuilderOptions<T, string> IbanRule<T>(this IRuleBuilder<T, string> r) =>
        r.Must(v => Iban.IsValid(TextSanitizer.NormalizeIban(v))).WithMessage("IBAN inválido.");

    public static IRuleBuilderOptions<T, decimal> Money<T>(this IRuleBuilder<T, decimal> r) =>
        r.GreaterThan(0).WithMessage("O montante deve ser superior a zero.")
         .LessThanOrEqualTo(1_000_000_000m).WithMessage("Montante demasiado elevado.")
         .Must(v => decimal.Round(v, 2) == v).WithMessage("O montante admite no máximo 2 casas decimais.");

    public static IRuleBuilderOptions<T, string?> Description<T>(this IRuleBuilder<T, string?> r) =>
        r.Must(v => v is null || Patterns.Description.IsMatch(TextSanitizer.Clean(v)))
         .WithMessage("A descrição contém caracteres não permitidos (máx. 140).");
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().PersonName();
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254)
            .Must(v => Patterns.Email.IsMatch(TextSanitizer.NormalizeEmail(v))).WithMessage("Email inválido.");
        RuleFor(x => x.Phone).NotEmpty()
            .Must(v => Patterns.Phone.IsMatch(TextSanitizer.NormalizePhone(v))).WithMessage("Telemóvel inválido (9XX XXX XXX).");
        RuleFor(x => x.NationalId).NotEmpty()
            .Must(v => Patterns.NationalId.IsMatch(TextSanitizer.NormalizeNationalId(v))).WithMessage("Nº do BI inválido (ex.: 005678901LA041).");
        RuleFor(x => x.TaxId).Must(v => string.IsNullOrEmpty(v) || Patterns.TaxId.IsMatch(v)).WithMessage("NIF inválido.");
        RuleFor(x => x.BirthDate)
            .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-18))).WithMessage("É necessário ter pelo menos 18 anos.")
            .Must(d => d >= DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-120))).WithMessage("Data de nascimento inválida.");
        RuleFor(x => x.Password).Custom((v, ctx) =>
        {
            var m = ctx.InstanceToValidate;
            if (CredentialPolicy.ValidatePassword(v, fullName: m.FullName) is { } msg) ctx.AddFailure(msg);
        });
        RuleFor(x => x.Pin).Pin();
        RuleFor(x => x.AcceptTerms).Equal(true).WithMessage("É necessário aceitar os termos e condições.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.CustomerNumber).Matches(Patterns.CustomerNumberPattern).WithMessage("Número de adesão inválido.");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(CredentialPolicy.PasswordMax);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(CredentialPolicy.PasswordMax);
        RuleFor(x => x.NewPassword).Custom((v, ctx) =>
        {
            if (CredentialPolicy.ValidatePassword(v) is { } msg) ctx.AddFailure(msg);
        });
        RuleFor(x => x).Must(x => x.NewPassword != x.CurrentPassword).WithName("NewPassword").WithMessage("A nova palavra-passe deve ser diferente da actual.");
    }
}

public sealed class ChangePinRequestValidator : AbstractValidator<ChangePinRequest>
{
    public ChangePinRequestValidator()
    {
        RuleFor(x => x.CurrentPin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
        RuleFor(x => x.NewPin).Pin();
        RuleFor(x => x).Must(x => x.NewPin != x.CurrentPin).WithName("NewPin").WithMessage("O novo PIN deve ser diferente do actual.");
    }
}

public sealed class RenameAccountRequestValidator : AbstractValidator<RenameAccountRequest>
{
    public RenameAccountRequestValidator() =>
        RuleFor(x => x.Nickname).Must(v => v is null || Patterns.Description.IsMatch(TextSanitizer.Clean(v)) && v.Length <= 40)
            .WithMessage("Nome da conta inválido (máx. 40 caracteres).");
}

public sealed class StatementQueryValidator : AbstractValidator<StatementQuery>
{
    public StatementQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 100).When(x => x.Limit.HasValue);
        RuleFor(x => x.Cursor).GreaterThan(0).When(x => x.Cursor.HasValue);
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithName("From").WithMessage("A data inicial deve ser anterior à final.");
    }
}

public sealed class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    public TransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x.ToIban).NotEmpty().IbanRule();
        RuleFor(x => x.BeneficiaryName!).PersonName().When(x => !string.IsNullOrWhiteSpace(x.BeneficiaryName));
        RuleFor(x => x.Amount).Money();
        RuleFor(x => x.Description).Description();
        RuleFor(x => x.Pin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
    }
}

public sealed class ResolveIbanRequestValidator : AbstractValidator<ResolveIbanRequest>
{
    public ResolveIbanRequestValidator() => RuleFor(x => x.Iban).NotEmpty().IbanRule();
}

public sealed class ServicePaymentRequestValidator : AbstractValidator<ServicePaymentRequest>
{
    public ServicePaymentRequestValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x.EntityCode).Matches(Patterns.ServiceEntityPattern).WithMessage("Entidade inválida (5 dígitos).");
        RuleFor(x => x.Reference).Matches(Patterns.ServiceReferencePattern).WithMessage("Referência inválida (9 dígitos).");
        RuleFor(x => x.Amount).Money();
        RuleFor(x => x.Pin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
    }
}

public sealed class RechargeRequestValidator : AbstractValidator<RechargeRequest>
{
    public RechargeRequestValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.Identifier).Custom((v, ctx) =>
        {
            var p = ctx.InstanceToValidate.Provider;
            var ok = p is RechargeProvider.Unitel or RechargeProvider.Africell
                ? Patterns.Phone.IsMatch(TextSanitizer.NormalizePhone(v))
                : Patterns.Subscriber.IsMatch(TextSanitizer.DigitsOnly(v));
            if (!ok) ctx.AddFailure(p is RechargeProvider.Unitel or RechargeProvider.Africell ? "Telemóvel inválido (9XX XXX XXX)." : "Número inválido (9 a 12 dígitos).");
        });
        RuleFor(x => x.Amount).Money().Custom((v, ctx) =>
        {
            var mobile = ctx.InstanceToValidate.Provider is RechargeProvider.Unitel or RechargeProvider.Africell;
            var (min, max) = mobile ? (100m, 50_000m) : (100m, 500_000m);
            if (v < min || v > max) ctx.AddFailure($"Montante entre {min:N0} e {max:N0} Kz.");
        });
        RuleFor(x => x.Pin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
    }
}

public sealed class StatePaymentRequestValidator : AbstractValidator<StatePaymentRequest>
{
    public StatePaymentRequestValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x.Reference).Must(v => Patterns.StateReference.IsMatch(TextSanitizer.DigitsOnly(v))).WithMessage("A referência tem 13 dígitos.");
        RuleFor(x => x.Amount).Money();
        RuleFor(x => x.Pin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
    }
}

public sealed class KwikResolveRequestValidator : AbstractValidator<KwikResolveRequest>
{
    public KwikResolveRequestValidator() =>
        RuleFor(x => x.Key).Must(v => Patterns.Phone.IsMatch(TextSanitizer.NormalizePhone(v))).WithMessage("Chave KWiK inválida (nº de telemóvel).");
}

public sealed class KwikTransferRequestValidator : AbstractValidator<KwikTransferRequest>
{
    public KwikTransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x.Key).Must(v => Patterns.Phone.IsMatch(TextSanitizer.NormalizePhone(v))).WithMessage("Chave KWiK inválida (nº de telemóvel).");
        RuleFor(x => x.Amount).Money();
        RuleFor(x => x.Description).Description();
        RuleFor(x => x.Pin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
    }
}

public sealed class BiometricEnrollRequestValidator : AbstractValidator<BiometricEnrollRequest>
{
    public BiometricEnrollRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(CredentialPolicy.PasswordMax);
        RuleFor(x => x.DeviceLabel).Must(v => v is null || Patterns.DeviceLabel.IsMatch(TextSanitizer.Clean(v))).WithMessage("Nome do dispositivo inválido.");
    }
}

public sealed class BiometricLoginRequestValidator : AbstractValidator<BiometricLoginRequest>
{
    public BiometricLoginRequestValidator()
    {
        RuleFor(x => x.CustomerNumber).Matches(Patterns.CustomerNumberPattern);
        RuleFor(x => x.DeviceToken).NotEmpty().MaximumLength(128);
    }
}

public sealed class BiometricDisableRequestValidator : AbstractValidator<BiometricDisableRequest>
{
    public BiometricDisableRequestValidator() => RuleFor(x => x.DeviceToken).NotEmpty().MaximumLength(128);
}

public sealed class BeneficiaryRequestValidator : AbstractValidator<BeneficiaryRequest>
{
    public BeneficiaryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().PersonName();
        RuleFor(x => x.Iban).NotEmpty().IbanRule();
    }
}

public sealed class UpdateCardRequestValidator : AbstractValidator<UpdateCardRequest>
{
    public UpdateCardRequestValidator() =>
        RuleFor(x => x.DailyLimit).InclusiveBetween(0, 2_000_000m).When(x => x.DailyLimit.HasValue)
            .Must(v => decimal.Round(v!.Value, 2) == v).When(x => x.DailyLimit.HasValue);
}

public sealed class VerifyPinRequestValidator : AbstractValidator<VerifyPinRequest>
{
    public VerifyPinRequestValidator() => RuleFor(x => x.Pin).Matches(Patterns.PinPattern).WithMessage("PIN inválido.");
}
