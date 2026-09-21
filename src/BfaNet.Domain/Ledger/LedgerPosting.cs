namespace BfaNet.Domain.Ledger;

public readonly record struct PostingLeg(Guid AccountId, LedgerDirection Direction, decimal Amount);

/// <summary>
/// A balanced set of ledger legs. Double-entry invariant: total debits == total credits,
/// all amounts strictly positive with at most 2 decimal places.
/// </summary>
public sealed class LedgerPosting
{
    public IReadOnlyList<PostingLeg> Legs { get; }

    private LedgerPosting(List<PostingLeg> legs) => Legs = legs;

    public static LedgerPosting Create(IEnumerable<PostingLeg> legs)
    {
        var list = legs.ToList();
        if (list.Count < 2) throw new DomainException("Um lançamento exige pelo menos duas partidas.");
        if (list.Any(l => l.Amount <= 0 || decimal.Round(l.Amount, 2) != l.Amount))
            throw new DomainException("Montante inválido.");

        var debit = list.Where(l => l.Direction == LedgerDirection.Debit).Sum(l => l.Amount);
        var credit = list.Where(l => l.Direction == LedgerDirection.Credit).Sum(l => l.Amount);
        if (debit != credit) throw new DomainException("Lançamento desequilibrado.");

        // Deterministic order prevents deadlocks when concurrent postings touch the same accounts.
        return new LedgerPosting(list.OrderBy(l => l.AccountId).ToList());
    }
}
