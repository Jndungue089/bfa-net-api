using System.Globalization;
using BfaNet.Application.Contracts;
using BfaNet.Domain;

namespace BfaNet.Application.Assistant;

internal static class Fmt
{
    private static readonly CultureInfo Pt = new("pt-PT");
    public static string Kz(decimal v) => v.ToString("N0", Pt).Replace('\u00A0', ' ').Replace('\u202F', ' ') + " Kz";
}

/// <summary>One ledger movement on one of the customer's own accounts. <see cref="Amount"/> is the entry amount (fee included on debits).</summary>
public sealed record Movement(
    DateTimeOffset At, TransactionKind Kind, LedgerDirection Direction, decimal Amount, decimal Fee,
    string? Description, string? Counterparty, string? ExternalReference, bool InternalTransfer);

public sealed record AnalyzerInput(
    IReadOnlyList<Movement> Movements, DateOnly Today, decimal SpendableBalance, decimal SavingsBalance,
    Guid? MainAccountId, Guid? SavingsAccountId, string? SavingsIban);

public sealed record AnalysisResult(AssistantInsights Insights, int IncomeMonths);

/// <summary>
/// The "intelligence" of the financial assistant: transparent statistics over the customer's own ledger — monthly flows,
/// category shares, month-end projection, recurring-payment detection, outlier detection and a weighted health score.
/// Pure function (no I/O, no clock), so every number can be reproduced and unit-tested. It is not machine learning and it
/// is not financial advice.
/// </summary>
public static class SpendingAnalyzer
{
    private static readonly TimeSpan Wat = TimeSpan.FromHours(1);

    /// <summary>The next monthly occurrence after <paramref name="today"/>; a cycle that was skipped is not reported as "upcoming" in the past.</summary>
    private static DateOnly NextOccurrence(DateOnly last, DateOnly today)
    {
        var next = last.AddMonths(1);
        for (var i = 2; next < today && i < 24; i++) next = last.AddMonths(i);
        return next;
    }

    public static AnalysisResult Analyze(AnalyzerInput x, DateTimeOffset generatedAt)
    {
        var today = x.Today;
        var months = new[] { today.AddMonths(-2), today.AddMonths(-1), today }.Select(d => (d.Year, d.Month)).ToArray(); // oldest → current
        static DateOnly Local(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(Wat).DateTime);
        bool InMonth(Movement m, (int Year, int Month) mo) { var d = Local(m.At); return d.Year == mo.Year && d.Month == mo.Month; }

        var real = x.Movements.Where(m => !m.InternalTransfer).ToList();
        // Money leaving the customer's world (own-account transfers, loan proceeds and deposits are not "spending").
        var outflows = real.Where(m => m.Direction == LedgerDirection.Debit && m.Kind is not (TransactionKind.Deposit or TransactionKind.Loan)).ToList();
        // Money arriving from outside (salary, received transfers). Loan proceeds are debt, not income.
        var inflows = real.Where(m => m.Direction == LedgerDirection.Credit && m.Kind is TransactionKind.Transfer or TransactionKind.Deposit).ToList();

        var flow = months.Select(mo => (Month: mo,
            Income: inflows.Where(m => InMonth(m, mo)).Sum(m => m.Amount),
            Spend: outflows.Where(m => InMonth(m, mo)).Sum(m => m.Amount))).ToList();
        var cur = flow[2];
        var prev = flow.Take(2).Where(f => f.Income > 0 || f.Spend > 0).ToList();
        var hasPrev = prev.Count > 0;
        var avgSpend = hasPrev ? prev.Average(f => f.Spend) : cur.Spend;
        var avgIncome = hasPrev ? prev.Average(f => f.Income) : cur.Income;
        var incomeMonths = flow.Count(f => f.Income > 0);

        var dim = DateTime.DaysInMonth(today.Year, today.Month);
        var daysLeft = dim - today.Day + 1;
        var spent = cur.Spend;
        var paced = spent / Math.Max(1, today.Day) * dim;
        var w = (decimal)today.Day / dim; // trust the current pace more as the month advances
        var projected = Math.Round(hasPrev ? Math.Max(spent, w * paced + (1 - w) * avgSpend) : Math.Max(spent, paced), 2);
        var budgetBase = hasPrev ? Math.Max(avgSpend, spent) : projected;
        var dailyBudget = Math.Round(Math.Max(0, (budgetBase - spent) / Math.Max(1, daysLeft)), 0);
        var savingsRate = avgIncome > 0 ? Math.Round((avgIncome - avgSpend) / avgIncome * 100, 1) : 0m;

        var hasEnough = x.Movements.Count >= 5;
        var monthsFlow = flow.Select(f => new MonthFlowDto($"{f.Month.Year}-{f.Month.Month:D2}", f.Income, f.Spend)).ToList();

        // ---- categories (current month vs. average of previous months) ----
        SpendCategory Cat(Movement m) => SpendCategorizer.Classify(m.Kind, m.Description, m.Counterparty, m.ExternalReference);
        var curByCat = outflows.Where(m => InMonth(m, months[2])).GroupBy(Cat).ToDictionary(g => g.Key, g => g.Sum(m => m.Amount));
        var prevMonths = flow.Take(2).Where(f => f.Income > 0 || f.Spend > 0).Select(f => f.Month).ToList();
        var prevByCat = outflows.Where(m => prevMonths.Any(pm => InMonth(m, pm))).GroupBy(Cat)
            .ToDictionary(g => g.Key, g => prevMonths.Count == 0 ? 0m : g.Sum(m => m.Amount) / prevMonths.Count);
        var categories = curByCat.Keys.Union(prevByCat.Keys)
            .Select(c => (Cat: c, Amount: curByCat.GetValueOrDefault(c), Avg: prevByCat.GetValueOrDefault(c)))
            .OrderByDescending(t => t.Amount).ThenByDescending(t => t.Avg).Take(8)
            .Select(t => new CategorySpendDto(t.Cat.ToString(), SpendCategorizer.Label(t.Cat), t.Amount, Math.Round(t.Avg, 2), cur.Spend > 0 ? Math.Round(t.Amount / cur.Spend * 100, 1) : 0m))
            .ToList();

        var recurring = DetectRecurring(outflows, Local, Cat, today);
        var health = Health(x, avgSpend, savingsRate, flow.Select(f => f.Spend).Where(s => s > 0).ToList(), incomeMonths);
        var saving = SuggestSaving(x, avgIncome, avgSpend, hasPrev, hasEnough);

        var insights = hasEnough
            ? BuildInsights(x, spent, projected, avgSpend, avgIncome, hasPrev, categories, recurring, outflows, InMonth, months, Local, Cat, saving, health, w: today.Day)
            : [new InsightDto("welcome", "info", "A conhecer os seus hábitos", "Ainda há poucos movimentos para uma análise fiável. Continue a usar a conta: com mais atividade as sugestões ficam precisas.", null, null)];

        var result = new AssistantInsights(generatedAt, hasEnough, "AOA", health, Math.Round(spent, 2), projected, Math.Round(avgSpend, 2), Math.Round(avgIncome, 2), savingsRate,
            x.SpendableBalance, x.SavingsBalance, dailyBudget, daysLeft, monthsFlow, categories, recurring, insights, saving);
        return new AnalysisResult(result, incomeMonths);
    }

    // ------------------------------------------------------------------------------------------------------------

    private static HealthDto Health(AnalyzerInput x, decimal avgSpend, decimal savingsRate, List<decimal> monthlySpends, int incomeMonths)
    {
        var rate = (int)Math.Round(Math.Clamp(savingsRate / 20m * 35m, 0m, 35m));
        var totalBalance = x.SpendableBalance + x.SavingsBalance;
        var bufferMonths = avgSpend > 0 ? totalBalance / avgSpend : (totalBalance > 0 ? 3m : 0m);
        var buffer = (int)Math.Round(Math.Clamp(bufferMonths / 3m * 25m, 0m, 25m));
        int stability; string stabilityNote;
        if (monthlySpends.Count < 2) { stability = 10; stabilityNote = "Poucos meses para medir"; }
        else
        {
            var mean = monthlySpends.Average();
            var sd = (decimal)Math.Sqrt(monthlySpends.Average(s => Math.Pow((double)(s - mean), 2)));
            var cv = mean > 0 ? sd / mean : 0;
            stability = (int)Math.Round(20m * (1m - Math.Min(cv / 0.5m, 1m)));
            stabilityNote = $"Variação mensal de {(cv * 100):0}%";
        }
        var regularity = (int)Math.Round(incomeMonths / 3m * 20m);

        var factors = new List<HealthFactorDto>
        {
            new("Taxa de poupança", rate, 35, $"{savingsRate:0.#}% do rendimento (meta: 20%)"),
            new("Reserva de emergência", buffer, 25, $"Cobre {bufferMonths:0.#} meses de gastos (meta: 3)"),
            new("Estabilidade dos gastos", stability, 20, stabilityNote),
            new("Regularidade do rendimento", regularity, 20, $"Rendimento em {incomeMonths} de 3 meses"),
        };
        var score = factors.Sum(f => f.Score);
        return new HealthDto(score, score >= 80 ? "Excelente" : score >= 60 ? "Boa" : score >= 40 ? "Razoável" : "Atenção", factors);
    }

    private static SavingsSuggestionDto? SuggestSaving(AnalyzerInput x, decimal avgIncome, decimal avgSpend, bool hasPrev, bool hasEnough)
    {
        if (!hasEnough || !hasPrev || x.MainAccountId is null) return null;
        var surplus = avgIncome - avgSpend;
        var available = x.SpendableBalance - 0.5m * avgSpend; // always leave half a month of spending untouched
        var amount = Math.Floor(Math.Min(surplus * 0.5m, available * 0.5m) / 500m) * 500m;
        if (amount < 1000m) return null;
        var reason = $"Nos últimos meses sobrou, em média, {Fmt.Kz(Math.Round(surplus))} por mês. Guardar {Fmt.Kz(amount)} por mês soma {Fmt.Kz(amount * 12)} num ano, sem mexer na reserva do dia-a-dia.";
        return new SavingsSuggestionDto(amount, reason, amount * 12, x.MainAccountId, x.SavingsAccountId, x.SavingsIban);
    }

    private static IReadOnlyList<RecurringDto> DetectRecurring(List<Movement> outflows, Func<DateTimeOffset, DateOnly> local, Func<Movement, SpendCategory> cat, DateOnly today)
    {
        var list = new List<RecurringDto>();
        foreach (var g in outflows.GroupBy(m => SpendCategorizer.Normalize(m.Counterparty ?? m.ExternalReference ?? m.Kind.ToString())).Where(g => g.Count() >= 2))
        {
            var items = g.OrderBy(m => m.At).ToList();
            var dates = items.Select(m => local(m.At)).ToList();
            var intervals = dates.Zip(dates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).Where(d => d > 0).OrderBy(d => d).ToList();
            if (intervals.Count == 0) continue;
            var medianInterval = intervals[intervals.Count / 2];
            var amounts = items.Select(m => m.Amount).OrderBy(a => a).ToList();
            var medianAmount = amounts[amounts.Count / 2];
            var mean = amounts.Average();
            var sd = (decimal)Math.Sqrt(amounts.Average(a => Math.Pow((double)(a - mean), 2)));
            if (medianInterval is < 24 or > 38 || (mean > 0 && sd / mean > 0.2m)) continue; // roughly monthly, roughly constant

            var last = dates[^1];
            var first = items[0];
            var c = cat(first);
            list.Add(new RecurringDto(first.Counterparty ?? SpendCategorizer.Label(c), c.ToString(), SpendCategorizer.Label(c), medianAmount, last.ToString("yyyy-MM-dd"), NextOccurrence(last, today).ToString("yyyy-MM-dd")));
        }
        return list.OrderByDescending(r => r.Amount).Take(8).ToList();
    }

    private static IReadOnlyList<InsightDto> BuildInsights(
        AnalyzerInput x, decimal spent, decimal projected, decimal avgSpend, decimal avgIncome, bool hasPrev,
        List<CategorySpendDto> categories, IReadOnlyList<RecurringDto> recurring, List<Movement> outflows,
        Func<Movement, (int Year, int Month), bool> inMonth, (int Year, int Month)[] months, Func<DateTimeOffset, DateOnly> local,
        Func<Movement, SpendCategory> cat, SavingsSuggestionDto? saving, HealthDto health, int w)
    {
        var list = new List<(int Priority, InsightDto Item)>();

        // 1) Month-end pace vs. habit
        if (hasPrev && avgSpend > 0)
        {
            var ratio = projected / avgSpend;
            if (ratio > 1.15m)
                list.Add((1, new("pace", "warning", $"A caminho de gastar {((ratio - 1) * 100):0}% acima do habitual",
                    $"Já gastou {Fmt.Kz(spent)} este mês. Ao ritmo actual chega a {Fmt.Kz(projected)}; a sua média é {Fmt.Kz(avgSpend)}.", "statement", null)));
            else if (ratio < 0.9m && w >= 10)
                list.Add((5, new("pace", "success", "Está a gastar menos do que é habitual",
                    $"Ao ritmo actual termina o mês em {Fmt.Kz(projected)}, abaixo da média de {Fmt.Kz(avgSpend)}.", null, null)));
        }

        // 2) A category that jumped
        var spike = categories.Where(c => c.AverageBefore > 0 && c.Amount > 1.5m * c.AverageBefore && c.Amount - c.AverageBefore >= 10_000m).OrderByDescending(c => c.Amount - c.AverageBefore).FirstOrDefault();
        if (spike is not null)
            list.Add((2, new($"spike-{spike.Category}", "warning", $"{spike.Label} subiu {((spike.Amount / spike.AverageBefore - 1) * 100):0}%",
                $"Este mês gastou {Fmt.Kz(spike.Amount)} em {spike.Label.ToLowerInvariant()}, contra {Fmt.Kz(spike.AverageBefore)} de média.", "statement", null)));

        // 3) An unusually large payment this month, judged against the customer's own history in that category
        var recurringNames = recurring.Select(r => SpendCategorizer.Normalize(r.Name)).ToHashSet();
        var history = outflows.Where(m => !inMonth(m, months[2])).GroupBy(cat)
            .ToDictionary(g => g.Key, g => { var a = g.Select(m => m.Amount).OrderBy(v => v).ToList(); return a[a.Count / 2]; });
        var big = outflows
            .Where(m => inMonth(m, months[2]) && m.Kind != TransactionKind.LoanRepayment && !recurringNames.Contains(SpendCategorizer.Normalize(m.Counterparty ?? m.ExternalReference ?? m.Kind.ToString())))
            .Select(m => (Move: m, Typical: history.TryGetValue(cat(m), out var t) ? t : 0m))
            .Where(t => t.Typical > 0 ? t.Move.Amount >= Math.Max(25_000m, 3 * t.Typical) : t.Move.Amount >= 50_000m)   // a brand-new category needs a bigger bar
            .OrderByDescending(t => t.Move.Amount).Cast<(Movement Move, decimal Typical)?>().FirstOrDefault();
        if (big is { } b3)
            list.Add((3, new("big-purchase", "info", "Despesa fora do normal",
                b3.Typical > 0
                    ? $"{Fmt.Kz(b3.Move.Amount)} em {b3.Move.Counterparty ?? SpendCategorizer.Label(cat(b3.Move))} ({local(b3.Move.At):dd/MM}) — o seu gasto típico nesta categoria é {Fmt.Kz(b3.Typical)}."
                    : $"{Fmt.Kz(b3.Move.Amount)} em {b3.Move.Counterparty ?? SpendCategorizer.Label(cat(b3.Move))} ({local(b3.Move.At):dd/MM}) — uma categoria em que não gastava antes.",
                "statement", null)));

        // 4) Fixed commitments
        if (recurring.Count > 0)
        {
            var total = recurring.Sum(r => r.Amount);
            var share = avgIncome > 0 ? $" ({(total / avgIncome * 100):0}% do rendimento)" : "";
            list.Add((4, new("recurring", "info", $"{recurring.Count} pagamentos recorrentes",
                $"Compromissos mensais de cerca de {Fmt.Kz(total)}{share}: {string.Join(", ", recurring.Take(3).Select(r => r.Name))}{(recurring.Count > 3 ? "…" : "")}.", null, null)));
        }

        // 5) Money left on the table: interbank fees
        var fees = outflows.Where(m => m.Kind == TransactionKind.Transfer).Sum(m => m.Fee);
        if (fees > 0)
            list.Add((6, new("interbank-fees", "tip", "Poupe em comissões",
                $"Pagou {Fmt.Kz(fees)} em comissões de transferências para outros bancos nos últimos 3 meses. Transferências entre contas BFA e KWiK não têm comissão.", null, null)));

        // 6) Saving
        if (saving is not null)
            list.Add((3, new("save", "tip", $"Pode poupar {Fmt.Kz(saving.Amount)}", saving.Reason, "save", saving.Amount)));

        // 7) Emergency fund
        var buffer = avgSpend > 0 ? (x.SpendableBalance + x.SavingsBalance) / avgSpend : 3m;
        if (avgSpend > 0 && buffer < 1m)
            list.Add((1, new("low-buffer", "warning", "Reserva de emergência baixa",
                $"O seu saldo cobre cerca de {buffer * 4.3m:0} semanas de gastos. O ideal é ter 3 meses guardados.", null, null)));

        // 8) Biggest cost
        var top = categories.FirstOrDefault(c => c.Amount > 0);
        if (top is not null && top.SharePercent >= 25)
            list.Add((7, new("top-category", "info", $"{top.Label} pesa {top.SharePercent:0}% dos gastos",
                $"É a sua maior categoria este mês: {Fmt.Kz(top.Amount)}.", null, null)));

        if (health.Score >= 80)
            list.Add((8, new("health-great", "success", "Finanças em muito boa forma", $"A sua saúde financeira é {health.Score}/100. Continue assim.", null, null)));

        return list.OrderBy(t => t.Priority).Select(t => t.Item).Take(7).ToList();
    }
}
