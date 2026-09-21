using System.Globalization;
using System.Text;

namespace BfaNet.Application.Common;

public static class TextSanitizer
{
    /// <summary>
    /// Unicode-normalises (NFC), drops control / format characters (which includes zero-width and
    /// bidi-override characters used for spoofing), collapses whitespace and trims.
    /// </summary>
    public static string Clean(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var normalized = input.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        var lastWasSpace = false;

        foreach (var ch in normalized)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate
                or UnicodeCategory.OtherNotAssigned)
            {
                // Whitespace-like controls (tab, newline) become a single space; the rest are dropped.
                if (ch is '\t' or '\n' or '\r' && !lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    public static string? CleanOrNull(string? input)
    {
        var s = Clean(input);
        return s.Length == 0 ? null : s;
    }

    /// <summary>Angolan mobile → national 9-digit form. Accepts "+244 9xx xxx xxx", "244…", "9xx…".</summary>
    public static string NormalizePhone(string? input)
    {
        var digits = new string((input ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("244") && digits.Length == 12) digits = digits[3..];
        return digits;
    }

    public static string DigitsOnly(string? input) => new((input ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    public static string NormalizeEmail(string? input) => Clean(input).ToLowerInvariant();

    public static string NormalizeNationalId(string? input) => Clean(input).Replace(" ", "").ToUpperInvariant();

    public static string NormalizeIban(string? input) => Clean(input).Replace(" ", "").ToUpperInvariant();

    public static string MaskName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => p.Length <= 2 ? p : p[..2] + new string('*', Math.Min(p.Length - 2, 6))));
    }

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at < 2) return "***";
        return email[0] + new string('*', Math.Min(at - 1, 6)) + email[at..];
    }

    public static string MaskPhone(string phone) => phone.Length < 9 ? "***" : $"{phone[..3]} *** **{phone[^1]}";

    public static string MaskNationalId(string id) => id.Length < 6 ? "***" : $"{id[..3]}******{id[^2..]}";
}
