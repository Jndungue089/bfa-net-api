using BfaNet.Application.Common;
using Xunit;

namespace BfaNet.Tests;

public class IbanTests
{
    [Fact]
    public void Generated_iban_is_valid_and_bfa()
    {
        var iban = Iban.Generate("0001", "12345678901");
        Assert.Equal(25, iban.Length);
        Assert.StartsWith("AO", iban);
        Assert.True(Iban.IsValid(iban));
        Assert.True(Iban.IsBfa(iban));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AO00000000000000000000000")]
    [InlineData("PT50000201231234567890154")]
    [InlineData("AO06000600010123456789012")]
    [InlineData("ao06000600010123456789012")]
    public void Malformed_or_bad_checksum_is_rejected(string iban) => Assert.False(Iban.IsValid(iban));

    [Fact]
    public void Single_digit_change_breaks_checksum()
    {
        var iban = Iban.Generate("0001", "12345678901");
        var tampered = iban[..20] + (iban[20] == '9' ? '0' : (char)(iban[20] + 1)) + iban[21..];
        Assert.False(Iban.IsValid(tampered));
    }

    [Fact]
    public void Ibans_are_unique_per_account_number() =>
        Assert.NotEqual(Iban.Generate("0001", "00000000001"), Iban.Generate("0001", "00000000002"));
}

public class CredentialPolicyTests
{
    [Theory]
    [InlineData("Curta1!", false)]
    [InlineData("semmaiusculas1!", false)]
    [InlineData("SEMMINUSCULAS1!", false)]
    [InlineData("SemNumeros!!ab", false)]
    [InlineData("SemSimbolos123ab", false)]
    [InlineData("Xaaabcdefg1!", false)]     // 3 repeated chars
    [InlineData("Boa#Passe-2026x", true)]
    public void Password_policy(string pwd, bool ok) => Assert.Equal(ok, CredentialPolicy.ValidatePassword(pwd) is null);

    [Fact]
    public void Password_cannot_contain_name_or_customer_number()
    {
        Assert.NotNull(CredentialPolicy.ValidatePassword("Maria#2026xyz", fullName: "Maria Santos"));
        Assert.NotNull(CredentialPolicy.ValidatePassword("X#10000001yzAb", customerNumber: "10000001"));
    }

    [Theory]
    [InlineData("123456", false)]
    [InlineData("654321", false)]
    [InlineData("111111", false)]
    [InlineData("12345", false)]
    [InlineData("12a456", false)]
    [InlineData("482915", true)]
    public void Pin_policy(string pin, bool ok) => Assert.Equal(ok, CredentialPolicy.ValidatePin(pin) is null);
}

public class PatternTests
{
    [Theory]
    [InlineData("005678901LA041", true)]
    [InlineData("05678901LA041", false)]
    [InlineData("005678901la041", false)]
    [InlineData("005678901LA041 ", false)]
    public void National_id(string v, bool ok) => Assert.Equal(ok, Patterns.NationalId.IsMatch(v));

    [Theory]
    [InlineData("923456789", true)]
    [InlineData("823456789", false)]
    [InlineData("92345678", false)]
    public void Phone(string v, bool ok) => Assert.Equal(ok, Patterns.Phone.IsMatch(v));

    [Theory]
    [InlineData("Pagamento renda - Setembro", true)]
    [InlineData("<script>alert(1)</script>", false)]
    [InlineData("a\"b", false)]
    [InlineData("back\\slash", false)]
    public void Description_rejects_markup_and_quote_breakers(string v, bool ok) => Assert.Equal(ok, Patterns.Description.IsMatch(v));

    [Theory]
    [InlineData("João Manuel Pereira", true)]
    [InlineData("D'Angelo O’Neil", true)]
    [InlineData("Maria1", false)]
    [InlineData("A", false)]
    [InlineData("<b>Maria</b>", false)]
    public void Person_name(string v, bool ok) => Assert.Equal(ok, Patterns.PersonName.IsMatch(v));

    [Fact]
    public void Email_pattern_is_not_vulnerable_to_catastrophic_backtracking()
    {
        var evil = new string('a', 50_000) + "@" + new string('b', 50_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.DoesNotMatch(Patterns.Email, evil);
        Assert.True(sw.ElapsedMilliseconds < 500);
    }
}

public class TextSanitizerTests
{
    [Fact]
    public void Removes_bidi_override_zero_width_and_control_chars() =>
        Assert.Equal("Pago a Joao", TextSanitizer.Clean("Pago‮ a​ Jo\u0000ao"));

    [Fact]
    public void Collapses_whitespace_and_trims() => Assert.Equal("a b c", TextSanitizer.Clean("  a \t\n b   c  "));

    [Fact]
    public void Normalises_unicode_to_nfc() => Assert.Equal("é", TextSanitizer.Clean("é"));

    [Theory]
    [InlineData("+244 923 456 789", "923456789")]
    [InlineData("244923456789", "923456789")]
    [InlineData("923-456-789", "923456789")]
    public void Phone_normalisation(string input, string expected) => Assert.Equal(expected, TextSanitizer.NormalizePhone(input));

    [Fact]
    public void Masking_hides_most_of_the_value()
    {
        Assert.Equal("Ma*** Fe******", TextSanitizer.MaskName("Maria Fernanda"));
        Assert.DoesNotContain("aria", TextSanitizer.MaskEmail("maria@x.ao"));
        Assert.Equal("005******41", TextSanitizer.MaskNationalId("005678901LA041"));
    }
}
