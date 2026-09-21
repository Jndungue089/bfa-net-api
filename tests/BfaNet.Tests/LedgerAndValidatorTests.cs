using BfaNet.Application.Contracts;
using BfaNet.Application.Validators;
using BfaNet.Domain;
using BfaNet.Domain.Ledger;
using Xunit;

namespace BfaNet.Tests;

public class LedgerPostingTests
{
    private static readonly Guid A = Guid.CreateVersion7(), B = Guid.CreateVersion7(), C = Guid.CreateVersion7();

    [Fact]
    public void Balanced_posting_is_accepted_and_ordered_by_account()
    {
        var p = LedgerPosting.Create([new(B, LedgerDirection.Credit, 90m), new(A, LedgerDirection.Debit, 100m), new(C, LedgerDirection.Credit, 10m)]);
        Assert.Equal(p.Legs.Select(l => l.AccountId).Order(), p.Legs.Select(l => l.AccountId));
    }

    [Fact]
    public void Unbalanced_posting_is_rejected() =>
        Assert.Throws<DomainException>(() => LedgerPosting.Create([new(A, LedgerDirection.Debit, 100m), new(B, LedgerDirection.Credit, 99.99m)]));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.005)]
    public void Non_positive_or_sub_cent_amounts_are_rejected(double amount) =>
        Assert.Throws<DomainException>(() => LedgerPosting.Create(
            [new(A, LedgerDirection.Debit, (decimal)amount), new(B, LedgerDirection.Credit, (decimal)amount)]));

    [Fact]
    public void Single_leg_is_rejected() =>
        Assert.Throws<DomainException>(() => LedgerPosting.Create([new(A, LedgerDirection.Debit, 1m)]));
}

public class ValidatorTests
{
    private static string ValidIban => BfaNet.Application.Common.Iban.Generate("0001", "12345678901");

    [Fact]
    public void Transfer_valid_passes() =>
        Assert.True(new TransferRequestValidator().Validate(new TransferRequest(Guid.NewGuid(), ValidIban, null, 1500.50m, "Renda", "482915")).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.123)]
    [InlineData(2_000_000_000)]
    public void Transfer_bad_amounts_fail(double amount) =>
        Assert.False(new TransferRequestValidator().Validate(new TransferRequest(Guid.NewGuid(), ValidIban, null, (decimal)amount, null, "482915")).IsValid);

    [Fact]
    public void Transfer_bad_iban_and_html_description_fail()
    {
        var r = new TransferRequestValidator().Validate(new TransferRequest(Guid.NewGuid(), "AO06000600010123456789013", null, 10m, "<img src=x onerror=alert(1)>", "482915"));
        Assert.Contains(r.Errors, e => e.PropertyName == "ToIban");
        Assert.Contains(r.Errors, e => e.PropertyName == "Description");
    }

    [Fact]
    public void Registration_requires_adult_terms_and_strong_secrets()
    {
        var bad = new RegisterRequest("Ana Maria", "ana@example.ao", "923456789", "005678901LA041", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-10)), "fraca", "123456", false);
        var errors = new RegisterRequestValidator().Validate(bad).Errors.Select(e => e.PropertyName).ToHashSet();
        Assert.Contains("BirthDate", errors);
        Assert.Contains("Password", errors);
        Assert.Contains("Pin", errors);
        Assert.Contains("AcceptTerms", errors);
    }

    [Fact]
    public void Statement_query_bounds()
    {
        var v = new StatementQueryValidator();
        Assert.False(v.Validate(new StatementQuery(null, null, null, null, 1000)).IsValid);
        Assert.False(v.Validate(new StatementQuery(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1), null, null, 10)).IsValid);
        Assert.True(v.Validate(new StatementQuery(null, null, null, 55, 50)).IsValid);
    }
}
