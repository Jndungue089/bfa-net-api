namespace BfaNet.Application.Common;

public static class CredentialPolicy
{
    public const int PasswordMin = 10;
    public const int PasswordMax = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password123", "password1234", "senha12345", "qwerty12345", "1234567890", "angola2024",
        "angola2025", "angola2026", "bfa123456", "bfanet1234", "abcdefghij", "iloveyou12"
    };

    /// <summary>Returns a Portuguese error message, or null when the password satisfies the policy.</summary>
    public static string? ValidatePassword(string? pwd, string? customerNumber = null, string? fullName = null)
    {
        if (string.IsNullOrEmpty(pwd) || pwd.Length < PasswordMin) return $"A palavra-passe deve ter pelo menos {PasswordMin} caracteres.";
        if (pwd.Length > PasswordMax) return $"A palavra-passe não pode exceder {PasswordMax} caracteres.";
        if (!pwd.Any(char.IsUpper)) return "A palavra-passe deve incluir uma letra maiúscula.";
        if (!pwd.Any(char.IsLower)) return "A palavra-passe deve incluir uma letra minúscula.";
        if (!pwd.Any(char.IsDigit)) return "A palavra-passe deve incluir um número.";
        if (!pwd.Any(c => !char.IsLetterOrDigit(c))) return "A palavra-passe deve incluir um símbolo.";
        if (HasRepeatedRun(pwd, 3)) return "A palavra-passe não pode ter mais de 2 caracteres iguais seguidos.";
        if (Common.Contains(pwd)) return "Esta palavra-passe é demasiado comum.";
        if (!string.IsNullOrEmpty(customerNumber) && pwd.Contains(customerNumber)) return "A palavra-passe não pode conter o número de adesão.";
        if (!string.IsNullOrWhiteSpace(fullName))
            foreach (var part in fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 4))
                if (pwd.Contains(part, StringComparison.OrdinalIgnoreCase)) return "A palavra-passe não pode conter o seu nome.";
        return null;
    }

    /// <summary>6 digits, not all equal, not an ascending/descending sequence.</summary>
    public static string? ValidatePin(string? pin)
    {
        if (pin is null || !Patterns.Pin.IsMatch(pin)) return "O PIN deve ter exactamente 6 dígitos.";
        if (pin.Distinct().Count() == 1) return "O PIN não pode ter todos os dígitos iguais.";
        var asc = true; var desc = true;
        for (var i = 1; i < pin.Length; i++)
        {
            if (pin[i] - pin[i - 1] != 1) asc = false;
            if (pin[i - 1] - pin[i] != 1) desc = false;
        }
        return asc || desc ? "O PIN não pode ser uma sequência." : null;
    }

    private static bool HasRepeatedRun(string s, int run)
    {
        var count = 1;
        for (var i = 1; i < s.Length; i++)
        {
            count = s[i] == s[i - 1] ? count + 1 : 1;
            if (count >= run) return true;
        }
        return false;
    }
}
