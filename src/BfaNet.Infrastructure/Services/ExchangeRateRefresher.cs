using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BfaNet.Domain;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BfaNet.Infrastructure.Services;

/// <summary>
/// Best-effort refresh of the EUR rate from the public bfa.ao page. The page is server-rendered HTML
/// with no API, so the result is sanity-checked before being written; on any doubt the seeded rate stays.
/// </summary>
public sealed partial class ExchangeRateRefresher(
    IServiceScopeFactory scopes, IHttpClientFactory http, TimeProvider clock, ILogger<ExchangeRateRefresher> log) : BackgroundService
{
    public const string ClientName = "bfa-site";
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stop);
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await RefreshAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Refresh de câmbios falhou; mantém valores anteriores."); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var html = await http.CreateClient(ClientName).GetStringAsync("/pt/particulares/pesquisa-de-cambios/", ct);
        var row = EurRow().Match(html);
        if (!row.Success) return;

        var buy = Parse(row.Groups["buy"].Value);
        var sell = Parse(row.Groups["sell"].Value);
        if (buy is null || sell is null || buy < 50 || sell > 5000 || sell < buy) return; // sanity

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BankDbContext>();
        var rate = await db.ExchangeRates.FirstOrDefaultAsync(r => r.Currency == Currency.EUR, ct);
        if (rate is null) return;
        rate.Buy = buy.Value; rate.Sell = sell.Value; rate.Source = "bfa.ao"; rate.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static decimal? Parse(string s) =>
        decimal.TryParse(WebUtility.HtmlDecode(s).Trim().Replace(".", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    [GeneratedRegex(@"<td[^>]*>\s*EUR\s*</td>\s*<td[^>]*>\s*(?<buy>[\d.,]+)\s*</td>\s*<td[^>]*>\s*(?<sell>[\d.,]+)\s*</td>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EurRow();
}
