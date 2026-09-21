using BfaNet.Application;
using BfaNet.Application.Assistant;
using BfaNet.Application.Contracts;
using BfaNet.Domain;
using Xunit;

namespace BfaNet.Tests;

public class SpendCategorizerTests
{
    [Theory]
    [InlineData(TransactionKind.TopUp, null, "Unitel", "Unitel:923456789", SpendCategory.Telecomunicacoes)]
    [InlineData(TransactionKind.TopUp, null, "DStv", "Dstv:1234567890", SpendCategory.Televisao)]
    [InlineData(TransactionKind.TopUp, null, "ENDE", "Ende:123456789012", SpendCategory.Energia)]
    [InlineData(TransactionKind.StatePayment, "Pagamento ao Estado", "Estado", "1234567890123", SpendCategory.Estado)]
    [InlineData(TransactionKind.LoanRepayment, "Prestação", "BFA", null, SpendCategory.Credito)]
    [InlineData(TransactionKind.ServicePayment, "Kero Hipermercado", "Kero", "11223/123456789", SpendCategory.Alimentacao)]
    [InlineData(TransactionKind.Transfer, "Renda de Setembro", "Imobiliária Sol", null, SpendCategory.Habitacao)]
    [InlineData(TransactionKind.Transfer, "Compra - Farmácia Central", "Farmácia Central", null, SpendCategory.Saude)]
    [InlineData(TransactionKind.Transfer, "Compra - Loja do Zé", "Loja do Zé", null, SpendCategory.Compras)]
    [InlineData(TransactionKind.ServicePayment, "Pagamento de serviços 11223", "Entidade 11223", "11223/123456789", SpendCategory.Servicos)]
    [InlineData(TransactionKind.Transfer, "Prenda", "Ana Costa", null, SpendCategory.Transferencias)]
    public void Classifies(TransactionKind kind, string? description, string? counterparty, string? ext, SpendCategory expected) =>
        Assert.Equal(expected, SpendCategorizer.Classify(kind, description, counterparty, ext));

    [Fact]
    public void Is_accent_and_case_insensitive() => Assert.Equal(SpendCategory.Educacao, SpendCategorizer.Classify(TransactionKind.Transfer, "PROPINA de Março", "Colégio São José", null));
}

public class LoanMathTests
{
    [Fact]
    public void Installment_matches_the_annuity_formula()
    {
        Assert.Equal(9455.96m, LoanMath.Installment(100_000m, 24m, 12), 1);
        Assert.Equal(33_333.33m, LoanMath.Installment(100_000m, 0m, 3));
    }

    [Theory]
    [InlineData(10_000, 24, 12)]
    [InlineData(75_000, 18, 6)]
    [InlineData(250_000, 30, 3)]
    public void Principal_for_never_exceeds_the_instalment_budget(int budget, int rate, int months)
    {
        var principal = LoanMath.PrincipalFor(budget, rate, months);
        Assert.True(LoanMath.Installment(principal, rate, months) <= budget, "instalment must fit the budget");
        Assert.True(LoanMath.Installment(principal + 1000, rate, months) > budget - 1, "and the next 1 000 Kz should (roughly) not");
    }

    [Fact]
    public void Due_dates_are_monthly_and_clamped_to_month_end() =>
        Assert.Equal([new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30)], LoanMath.DueDates(new DateOnly(2026, 1, 31), 3));

    [Fact]
    public void Fee_is_a_percentage_rounded_to_cents() => Assert.Equal(1_234.57m, LoanMath.Fee(123_456.789m, 1m));
}

public class SpendingAnalyzerTests
{
    private static readonly DateOnly Today = new(2026, 9, 21);
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static Movement Out(string date, decimal amount, string counterparty, string? description = null, TransactionKind kind = TransactionKind.ServicePayment, decimal fee = 0, string? ext = null, bool internalTransfer = false) =>
        new(D(date), kind, LedgerDirection.Debit, amount, fee, description, counterparty, ext, internalTransfer);

    private static Movement In(string date, decimal amount, string from, TransactionKind kind = TransactionKind.Deposit, bool internalTransfer = false) =>
        new(D(date), kind, LedgerDirection.Credit, amount, 0, "Salário", from, null, internalTransfer);

    private static DateTimeOffset D(string date) => new(DateOnly.Parse(date).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(1));

    /// <summary>Three months of a salaried customer who overspends on shopping in September.</summary>
    private static List<Movement> Typical() =>
    [
        In("2026-07-27", 450_000, "Empresa Alfa"), In("2026-08-27", 450_000, "Empresa Alfa"),
        Out("2026-07-03", 120_000, "Imobiliária Sol", "Renda de casa"), Out("2026-08-03", 120_000, "Imobiliária Sol", "Renda de casa"), Out("2026-09-03", 120_000, "Imobiliária Sol", "Renda de casa"),
        Out("2026-07-12", 15_000, "DStv", kind: TransactionKind.TopUp, ext: "Dstv:1234567890"), Out("2026-08-12", 15_000, "DStv", kind: TransactionKind.TopUp, ext: "Dstv:1234567890"), Out("2026-09-12", 15_000, "DStv", kind: TransactionKind.TopUp, ext: "Dstv:1234567890"),
        Out("2026-07-08", 60_000, "Kero Hipermercado"), Out("2026-08-09", 62_000, "Kero Hipermercado"), Out("2026-09-08", 58_000, "Kero Hipermercado"),
        Out("2026-07-20", 10_000, "Loja do Zé", "Compra - Loja do Zé"), Out("2026-08-18", 10_000, "Loja do Zé", "Compra - Loja do Zé"),
        Out("2026-09-16", 65_000, "Móveis & Decor", "Compra - Móveis & Decor"),
        Out("2026-08-25", 10_150, "Ana Costa", kind: TransactionKind.Transfer, fee: 150),
        // must NOT count: own-account transfer (savings) and loan proceeds
        Out("2026-08-28", 100_000, "Poupança", kind: TransactionKind.Transfer, internalTransfer: true),
        new(D("2026-09-05"), TransactionKind.Loan, LedgerDirection.Credit, 200_000, 0, "Microcrédito", "BFA", null, false),
    ];

    private static AnalysisResult Run(List<Movement> m, decimal spendable = 800_000, decimal savings = 500_000) =>
        SpendingAnalyzer.Analyze(new AnalyzerInput(m, Today, spendable, savings, Guid.NewGuid(), Guid.NewGuid(), "AO06000600010000000000000"), Now);

    [Fact]
    public void Own_transfers_and_loan_proceeds_are_neither_spending_nor_income()
    {
        var r = Run(Typical()).Insights;
        Assert.Equal(450_000m, r.AverageMonthlyIncome);                       // salary only; the 200k loan is debt
        var august = r.Months.Single(m => m.Month == "2026-08");
        Assert.Equal(120_000m + 15_000m + 62_000m + 10_000m + 10_150m, august.Spend); // the 100k to savings is excluded
        Assert.Equal(450_000m, august.Income);
    }

    [Fact]
    public void Detects_monthly_recurring_payments_but_not_one_offs()
    {
        var names = Run(Typical()).Insights.Recurring.Select(r => r.Name).ToList();
        Assert.Contains("Imobiliária Sol", names);
        Assert.Contains("DStv", names);
        Assert.Contains("Kero Hipermercado", names);
        Assert.DoesNotContain("Móveis & Decor", names);
        Assert.Equal("2026-10-12", Run(Typical()).Insights.Recurring.Single(r => r.Name == "DStv").NextDate);
    }

    [Fact]
    public void Next_occurrence_of_a_skipped_cycle_is_never_in_the_past()
    {
        var recurring = Run(Typical()).Insights.Recurring;
        Assert.All(recurring, r => Assert.True(DateOnly.Parse(r.NextDate) >= Today, $"{r.Name}: {r.NextDate}"));
    }

    [Fact]
    public void Flags_the_shopping_spike_the_big_purchase_and_the_interbank_fee()
    {
        var ids = Run(Typical()).Insights.Insights.Select(i => i.Id).ToList();
        Assert.Contains("spike-Compras", ids);   // 65k vs 10k average
        Assert.Contains("big-purchase", ids);
        Assert.Contains("interbank-fees", ids);
    }

    [Fact]
    public void Suggests_a_saving_that_leaves_a_safety_margin()
    {
        var s = Run(Typical()).Insights.Saving;
        Assert.NotNull(s);
        Assert.Equal(0m, s!.Amount % 500m);
        Assert.InRange(s.Amount, 1_000m, 200_000m);
        Assert.Equal(s.Amount * 12, s.YearlyProjection);
        Assert.NotNull(s.TargetAccountId);
        Assert.Contains(Run(Typical()).Insights.Insights, i => i.Id == "save" && i.ActionType == "save" && i.ActionAmount == s.Amount);
    }

    [Fact]
    public void No_saving_when_the_balance_is_too_thin()
    {
        var r = Run(Typical(), spendable: 100_000, savings: 0).Insights;
        Assert.Null(r.Saving);
        Assert.Contains(r.Insights, i => i.Id == "low-buffer");
    }

    [Fact]
    public void Health_score_is_the_sum_of_its_bounded_factors()
    {
        var h = Run(Typical()).Insights.Health;
        Assert.Equal(h.Factors.Sum(f => f.Score), h.Score);
        Assert.All(h.Factors, f => Assert.InRange(f.Score, 0, f.Max));
        Assert.InRange(h.Score, 0, 100);
        Assert.Contains(h.Label, new[] { "Excelente", "Boa", "Razoável", "Atenção" });
    }

    [Fact]
    public void Projection_never_falls_below_what_was_already_spent_and_budget_is_non_negative()
    {
        var r = Run(Typical()).Insights;
        Assert.True(r.ProjectedSpend >= r.SpentThisMonth);
        Assert.True(r.DailyBudget >= 0);
        Assert.Equal(10, r.DaysLeft);   // 30 − 21 + 1
    }

    [Fact]
    public void Too_little_data_gives_a_friendly_placeholder_instead_of_made_up_advice()
    {
        var r = Run([Out("2026-09-10", 5_000, "Kero"), In("2026-09-01", 100_000, "Empresa")]).Insights;
        Assert.False(r.HasEnoughData);
        Assert.Single(r.Insights);
        Assert.Equal("welcome", r.Insights[0].Id);
        Assert.Null(r.Saving);
    }
}

public class CreditScoringTests
{
    private static readonly AssistantOptions O = new();
    private static CreditProfile Good(int score = 80) => new(DaysOfHistory: 90, AvgMonthlyIncome: 450_000, AvgMonthlySpend: 280_000, IncomeMonths: 3, HealthScore: score, TotalBalance: 900_000, HasActiveLoan: false);

    [Fact]
    public void Strong_profile_gets_an_offer_within_all_the_caps()
    {
        var o = CreditScoring.Evaluate(Good(), O, null);
        Assert.True(o.Eligible);
        Assert.Empty(o.Blockers);
        Assert.Equal(0m, o.MaxAmount % 1000m);
        Assert.InRange(o.MaxAmount, O.MinOffer, O.MaxOffer);
        Assert.True(o.MaxAmount <= 450_000m * 3);
        Assert.Equal(Math.Floor(450_000m * O.InstallmentToIncomeCap), o.MaxInstallment);
        // the largest offer must fit the instalment budget over the longest term
        Assert.True(LoanMath.Installment(o.MaxAmount, o.AnnualRatePercent, O.Terms.Max()) <= o.MaxInstallment);
    }

    [Theory]
    [InlineData(90, 18)]
    [InlineData(65, 24)]
    [InlineData(45, 30)]
    public void Better_health_means_a_lower_rate(int score, int expectedRate) => Assert.Equal(expectedRate, CreditScoring.Evaluate(Good(score), O, null).AnnualRatePercent);

    [Fact]
    public void Each_blocker_is_explained_and_no_offer_is_made()
    {
        Assert.Contains("dias de movimentos", string.Join(" ", CreditScoring.Evaluate(Good() with { DaysOfHistory = 10 }, O, null).Blockers));
        Assert.Contains("Rendimento regular", string.Join(" ", CreditScoring.Evaluate(Good() with { IncomeMonths = 1 }, O, null).Blockers));
        Assert.Contains("mínimo", string.Join(" ", CreditScoring.Evaluate(Good() with { AvgMonthlyIncome = 10_000 }, O, null).Blockers));
        Assert.Contains("Saúde financeira", string.Join(" ", CreditScoring.Evaluate(Good(20), O, null).Blockers));
        Assert.Contains("microcrédito activo", string.Join(" ", CreditScoring.Evaluate(Good() with { HasActiveLoan = true }, O, null).Blockers));
        Assert.All(new[] { 10, 20 }, d => Assert.Equal(0m, CreditScoring.Evaluate(Good() with { DaysOfHistory = d }, O, null).MaxAmount));
    }

    [Fact]
    public void Simulation_is_consistent_and_flags_instalments_beyond_capacity()
    {
        var offer = CreditScoring.Evaluate(Good(), O, null);
        var ok = CreditScoring.Simulate(100_000m, 6, offer);
        Assert.Equal(ok.Installment * 6, ok.TotalRepayable);
        Assert.Equal(ok.TotalRepayable - 100_000m, ok.TotalInterest);
        Assert.Equal(100_000m - ok.Fee, ok.NetDisbursed);
        Assert.True(ok.WithinCapacity);
        Assert.False(CreditScoring.Simulate(offer.MaxAmount, 3, offer).WithinCapacity);   // max amount over 3 months is too heavy
    }
}
