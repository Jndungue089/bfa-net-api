namespace BfaNet.Application.Assistant;

/// <summary>Annuity (French system) maths. All rounding is to 2 decimals, away from zero, and the schedule adds up exactly.</summary>
public static class LoanMath
{
    public static decimal Installment(decimal principal, decimal annualRatePercent, int months)
    {
        if (months <= 0) throw new ArgumentOutOfRangeException(nameof(months));
        var r = (double)annualRatePercent / 100.0 / 12.0;
        if (r == 0) return Math.Round(principal / months, 2, MidpointRounding.AwayFromZero);
        var pmt = (double)principal * r / (1 - Math.Pow(1 + r, -months));
        return Math.Round((decimal)pmt, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Largest principal whose instalment is ≤ <paramref name="installment"/> over <paramref name="months"/>.</summary>
    public static decimal PrincipalFor(decimal installment, decimal annualRatePercent, int months)
    {
        var r = (double)annualRatePercent / 100.0 / 12.0;
        var pv = r == 0 ? (double)installment * months : (double)installment * (1 - Math.Pow(1 + r, -months)) / r;
        return Math.Floor((decimal)pv);
    }

    public static decimal Total(decimal installment, int months) => installment * months;

    public static decimal Fee(decimal principal, decimal feePercent) => Math.Round(principal * feePercent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Due dates: one month apart, starting one month after disbursement (clamped to month length).</summary>
    public static IReadOnlyList<DateOnly> DueDates(DateOnly disbursement, int months) =>
        Enumerable.Range(1, months).Select(i => disbursement.AddMonths(i)).ToList();
}
