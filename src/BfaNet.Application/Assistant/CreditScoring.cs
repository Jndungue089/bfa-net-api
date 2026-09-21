using BfaNet.Application.Contracts;

namespace BfaNet.Application.Assistant;

public sealed record CreditProfile(int DaysOfHistory, decimal AvgMonthlyIncome, decimal AvgMonthlySpend, int IncomeMonths, int HealthScore, decimal TotalBalance, bool HasActiveLoan);

/// <summary>
/// Deterministic, explainable pre-approval: every input, threshold and outcome is listed in <see cref="CreditOfferDto.Reasons"/>
/// or <see cref="CreditOfferDto.Blockers"/>. There is no model in the loop, and the server re-runs this on every simulation
/// and acceptance — the client's numbers are never trusted.
/// </summary>
public static class CreditScoring
{
    public static CreditOfferDto Evaluate(CreditProfile p, AssistantOptions o, LoanDto? activeLoan)
    {
        var blockers = new List<string>();
        var reasons = new List<string>();
        var longest = o.Terms.Max();

        if (p.HasActiveLoan) blockers.Add("Já tem um microcrédito activo. Termine de o pagar para pedir outro.");
        if (p.DaysOfHistory < o.MinHistoryDays) blockers.Add($"É preciso ter pelo menos {o.MinHistoryDays} dias de movimentos (tem {p.DaysOfHistory}).");
        else reasons.Add($"{p.DaysOfHistory} dias de histórico na conta");
        if (p.IncomeMonths < o.MinIncomeMonths) blockers.Add($"Rendimento regular em pelo menos {o.MinIncomeMonths} dos últimos 3 meses (tem {p.IncomeMonths}).");
        else reasons.Add($"Rendimento em {p.IncomeMonths} dos últimos 3 meses");
        if (p.AvgMonthlyIncome < o.MinMonthlyIncome) blockers.Add($"Rendimento mensal médio abaixo do mínimo ({Fmt.Kz(o.MinMonthlyIncome)}).");
        else reasons.Add($"Rendimento médio de {Fmt.Kz(p.AvgMonthlyIncome)}/mês");
        if (p.HealthScore < o.MinHealthScore) blockers.Add($"Saúde financeira {p.HealthScore}/100 — mínimo {o.MinHealthScore}.");
        else reasons.Add($"Saúde financeira {p.HealthScore}/100");

        var rate = p.HealthScore >= 75 ? o.RateTop : p.HealthScore >= 60 ? o.RateMid : o.RateBase;
        var maxInstallment = Math.Floor(p.AvgMonthlyIncome * o.InstallmentToIncomeCap);
        var byCapacity = LoanMath.PrincipalFor(maxInstallment, rate, longest);
        var byIncome = Math.Floor(p.AvgMonthlyIncome * 3);
        var max = Math.Floor(Math.Min(o.MaxOffer, Math.Min(byCapacity, byIncome)) / 1000m) * 1000m;

        if (blockers.Count == 0 && max < o.MinOffer) blockers.Add($"A sua capacidade de pagamento não chega ao mínimo de {Fmt.Kz(o.MinOffer)}.");
        var eligible = blockers.Count == 0;
        if (eligible) reasons.Add($"Prestação limitada a {(o.InstallmentToIncomeCap * 100):0}% do rendimento");

        return new CreditOfferDto(eligible, eligible ? max : 0, o.MinOffer, rate, o.OriginationFeePercent, o.Terms, eligible ? maxInstallment : 0, reasons, blockers, activeLoan);
    }

    /// <summary>The insight card that surfaces a pre-approved offer inside the assistant's feed.</summary>
    public static InsightDto? OfferInsight(CreditOfferDto o) => !o.Eligible ? null : new InsightDto("credit", "tip", "Microcrédito pré-aprovado",
        $"Tem até {Fmt.Kz(o.MaxAmount)} disponível a {o.AnnualRatePercent:0.#}% ao ano, em {string.Join(", ", o.Terms)} meses. Simule sem compromisso: só é concedido se confirmar com o PIN.", "credit", o.MaxAmount);

    public static CreditSimulationDto Simulate(decimal amount, int months, CreditOfferDto offer)
    {
        var installment = LoanMath.Installment(amount, offer.AnnualRatePercent, months);
        var total = LoanMath.Total(installment, months);
        var fee = LoanMath.Fee(amount, offer.OriginationFeePercent);
        return new CreditSimulationDto(amount, months, offer.AnnualRatePercent, fee, amount - fee, installment, total, total - amount, installment <= offer.MaxInstallment);
    }

}
