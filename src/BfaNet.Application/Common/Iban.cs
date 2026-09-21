using System.Numerics;

namespace BfaNet.Application.Common;

/// <summary>Angolan IBAN: AO + 2 check digits + 21-digit NIB (bank 4, branch 4, account 11, check 2).</summary>
public static class Iban
{
    public const string BfaBankCode = "0006";

    public static bool IsValid(string? iban)
    {
        if (string.IsNullOrEmpty(iban) || !Patterns.Iban.IsMatch(iban)) return false;
        return Mod97(iban[4..] + iban[..4]) == 1;
    }

    public static bool IsBfa(string iban) => iban.Length == 25 && iban.AsSpan(4, 4).SequenceEqual(BfaBankCode);

    /// <summary>Builds a valid IBAN for the given branch (4 digits) and account number (11 digits).</summary>
    public static string Generate(string branch, string accountNumber11)
    {
        if (branch.Length != 4 || accountNumber11.Length != 11
            || !branch.All(char.IsAsciiDigit) || !accountNumber11.All(char.IsAsciiDigit))
            throw new ArgumentException("Estrutura de conta inválida.");

        var first19 = BfaBankCode + branch + accountNumber11;
        var nibCheck = 98 - (int)(BigInteger.Parse(first19 + "00") % 97);
        var bban = first19 + nibCheck.ToString("D2");
        var ibanCheck = 98 - Mod97(bban + "AO00");
        return $"AO{ibanCheck:D2}{bban}";
    }

    private static int Mod97(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length * 2);
        foreach (var c in input)
            sb.Append(char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString() : c.ToString());
        return (int)(BigInteger.Parse(sb.ToString()) % 97);
    }

    public static string Format(string iban) =>
        string.Join(' ', Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));
}
