using System.Text.RegularExpressions;

namespace BfaNet.Application.Common;

/// <summary>
/// Single source of truth for input formats. The same expressions are mirrored in the shared zod
/// schemas used by web and mobile; the API never trusts the client and re-validates everything.
/// All regexes are anchored, non-backtracking-safe (no nested quantifiers) and time-limited.
/// </summary>
public static partial class Patterns
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public const string CustomerNumberPattern = @"^\d{8}$";
    public const string PhonePattern = @"^9\d{8}$";
    public const string NationalIdPattern = @"^\d{9}[A-Z]{2}\d{3}$";
    public const string TaxIdPattern = @"^\d{10}$";
    public const string IbanPattern = @"^AO\d{23}$";
    public const string PinPattern = @"^\d{6}$";
    public const string ServiceEntityPattern = @"^\d{5}$";
    public const string ServiceReferencePattern = @"^\d{9}$";
    /// <summary>Documento de arrecadação / referência de pagamento ao Estado: 13 dígitos.</summary>
    public const string StateReferencePattern = @"^\d{13}$";
    /// <summary>Subscriber / smartcard / meter number for TV and electricity recharges.</summary>
    public const string SubscriberPattern = @"^\d{9,12}$";
    public const string PersonNamePattern = @"^\p{L}[\p{L}\p{M}'’.\- ]{1,118}[\p{L}.]$";
    public const string EmailPattern = @"^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(\.[A-Za-z0-9\-]{1,63}){1,4}$";
    /// <summary>Free text: letters, digits and a conservative punctuation set. No markup, quotes-breakers or backslashes.</summary>
    public const string DescriptionPattern = @"^[\p{L}\p{N} .,;:'’()/\-_+&#@%!?]{0,140}$";
    public const string DeviceLabelPattern = @"^[\p{L}\p{N} .,'’()\-_]{0,60}$";

    public static readonly Regex CustomerNumber = new(CustomerNumberPattern, Opts);
    public static readonly Regex Phone = new(PhonePattern, Opts);
    public static readonly Regex NationalId = new(NationalIdPattern, Opts);
    public static readonly Regex TaxId = new(TaxIdPattern, Opts);
    public static readonly Regex Iban = new(IbanPattern, Opts);
    public static readonly Regex Pin = new(PinPattern, Opts);
    public static readonly Regex ServiceEntity = new(ServiceEntityPattern, Opts);
    public static readonly Regex ServiceReference = new(ServiceReferencePattern, Opts);
    public static readonly Regex StateReference = new(StateReferencePattern, Opts);
    public static readonly Regex Subscriber = new(SubscriberPattern, Opts);
    public static readonly Regex PersonName = new(PersonNamePattern, Opts);
    public static readonly Regex Email = new(EmailPattern, Opts);
    public static readonly Regex Description = new(DescriptionPattern, Opts);
    public static readonly Regex DeviceLabel = new(DeviceLabelPattern, Opts);
}
