namespace BfaNet.Application.Contracts;

// ---- Insights ----
public sealed record HealthFactorDto(string Name, int Score, int Max, string Note);
public sealed record HealthDto(int Score, string Label, IReadOnlyList<HealthFactorDto> Factors);
public sealed record MonthFlowDto(string Month, decimal Income, decimal Spend);
public sealed record CategorySpendDto(string Category, string Label, decimal Amount, decimal AverageBefore, decimal SharePercent);
public sealed record RecurringDto(string Name, string Category, string CategoryLabel, decimal Amount, string LastDate, string NextDate);

/// <summary>Kind: success | info | warning | tip. ActionType: save | credit | statement | null.</summary>
public sealed record InsightDto(string Id, string Kind, string Title, string Message, string? ActionType, decimal? ActionAmount);
public sealed record SavingsSuggestionDto(decimal Amount, string Reason, decimal YearlyProjection, Guid? FromAccountId, Guid? TargetAccountId, string? TargetIban);

public sealed record AssistantInsights(
    DateTimeOffset GeneratedAt, bool HasEnoughData, string Currency, HealthDto Health,
    decimal SpentThisMonth, decimal ProjectedSpend, decimal AverageMonthlySpend, decimal AverageMonthlyIncome, decimal SavingsRatePercent,
    decimal SpendableBalance, decimal SavingsBalance, decimal DailyBudget, int DaysLeft,
    IReadOnlyList<MonthFlowDto> Months, IReadOnlyList<CategorySpendDto> Categories, IReadOnlyList<RecurringDto> Recurring,
    IReadOnlyList<InsightDto> Insights, SavingsSuggestionDto? Saving);

// ---- Microcredit ----
public sealed record InstallmentDto(int Number, string DueDate, decimal Amount, DateTimeOffset? PaidAt);
public sealed record LoanDto(
    Guid Id, decimal Principal, int TermMonths, decimal AnnualRatePercent, decimal Installment, decimal TotalRepayable,
    decimal Outstanding, string Status, DateTimeOffset CreatedAt, IReadOnlyList<InstallmentDto> Installments);

public sealed record CreditOfferDto(
    bool Eligible, decimal MaxAmount, decimal MinAmount, decimal AnnualRatePercent, decimal OriginationFeePercent,
    IReadOnlyList<int> Terms, decimal MaxInstallment, IReadOnlyList<string> Reasons, IReadOnlyList<string> Blockers, LoanDto? ActiveLoan);

public sealed record CreditSimulationDto(
    decimal Amount, int Months, decimal AnnualRatePercent, decimal Fee, decimal NetDisbursed,
    decimal Installment, decimal TotalRepayable, decimal TotalInterest, bool WithinCapacity);

public sealed record AcceptCreditRequest(Guid AccountId, decimal Amount, int Months, string Pin);
public sealed record RepayLoanRequest(Guid FromAccountId, string Pin);
