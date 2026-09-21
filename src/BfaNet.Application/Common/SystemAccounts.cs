namespace BfaNet.Application.Common;

/// <summary>Internal GL accounts the ledger posts against (settlement, fees, cash vault).</summary>
public static class SystemAccounts
{
    public static readonly Guid CashVault = Guid.Parse("00000000-0000-7000-8000-000000000001");
    public static readonly Guid InterbankSettlement = Guid.Parse("00000000-0000-7000-8000-000000000002");
    public static readonly Guid ServiceSettlement = Guid.Parse("00000000-0000-7000-8000-000000000003");
    public static readonly Guid TopUpSettlement = Guid.Parse("00000000-0000-7000-8000-000000000004");
    public static readonly Guid Fees = Guid.Parse("00000000-0000-7000-8000-000000000005");
    public static readonly Guid StateSettlement = Guid.Parse("00000000-0000-7000-8000-000000000006");
    /// <summary>Microcredit book: debited on disbursement, credited on repayment (may go negative, it is a GL account).</summary>
    public static readonly Guid CreditPortfolio = Guid.Parse("00000000-0000-7000-8000-000000000007");

    public static readonly (Guid Id, string Number)[] All =
    [
        (CashVault, "00000000001"), (InterbankSettlement, "00000000002"), (ServiceSettlement, "00000000003"),
        (TopUpSettlement, "00000000004"), (Fees, "00000000005"), (StateSettlement, "00000000006"), (CreditPortfolio, "00000000007")
    ];
}
