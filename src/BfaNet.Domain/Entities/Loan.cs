namespace BfaNet.Domain.Entities;

public sealed class Loan
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CustomerId { get; init; }
    /// <summary>Account that received the money and is debited for instalments.</summary>
    public Guid AccountId { get; init; }
    public decimal Principal { get; init; }
    public int TermMonths { get; init; }
    public decimal AnnualRatePercent { get; init; }
    public decimal OriginationFee { get; init; }
    public decimal Installment { get; init; }
    public decimal TotalRepayable { get; init; }
    public LoanStatus Status { get; set; } = LoanStatus.Active;
    /// <summary>Ledger transaction that disbursed the loan; also the idempotency anchor for the acceptance request.</summary>
    public Guid DisbursementTransactionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; set; }

    public List<LoanInstallment> Installments { get; init; } = [];
}

public sealed class LoanInstallment
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid LoanId { get; init; }
    public int Number { get; init; }
    public DateOnly DueDate { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset? PaidAt { get; set; }
    public Guid? PaymentTransactionId { get; set; }
}
