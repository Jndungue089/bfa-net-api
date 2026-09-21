using BfaNet.Application.Abstractions;
using BfaNet.Application.Contracts;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BfaNet.Infrastructure.Services;

public sealed class PublicInfoService(BankDbContext db) : IPublicInfoService
{
    private const string Site = "https://www.bfa.ao";

    // Catalogue mirrored from the public bfa.ao navigation (names/URLs only).
    private static readonly IReadOnlyList<ProductDto> Catalogue =
    [
        new("conta-ordem", "Contas", "Conta à Ordem", "A conta do dia-a-dia para movimentar o seu dinheiro.", $"{Site}/pt/particulares/contas/conta-a-ordem/"),
        new("conta-bankita", "Contas", "Conta Bankita", "Conta simplificada, sem complicações.", $"{Site}/pt/particulares/contas/conta-bankita/"),
        new("conta-ordenado", "Contas", "Conta Ordenado", "Receba o seu salário com vantagens exclusivas.", $"{Site}/pt/particulares/contas/conta-ordenado/"),
        new("credito-pessoal", "Crédito", "Crédito Pessoal", "Financie os seus projectos.", $"{Site}/pt/particulares/credito/credito-pessoal/"),
        new("credito-automovel", "Crédito", "Crédito Automóvel", "O seu próximo carro mais perto.", $"{Site}/pt/particulares/credito/credito-automovel/"),
        new("credito-habitacao", "Crédito", "Crédito Habitação", "A casa que sempre sonhou.", $"{Site}/pt/particulares/credito/credito-habitacao/"),
        new("cartao-debito", "Cartões", "Cartão de Débito BFA", "Pague e levante com segurança.", $"{Site}/pt/particulares/cartoes/cartao-de-debito-bfa/"),
        new("cartao-kandandu", "Cartões", "Cartão Pré-pago Kandandu", "Controle os seus gastos com um cartão pré-pago.", $"{Site}/pt/particulares/cartoes/cartao-pre-pago-kandandu/"),
        new("super-poupanca", "Poupança", "Super Poupança", "Faça o seu dinheiro crescer.", $"{Site}/pt/particulares/poupancainvestimento/super-poupanca/"),
        new("plano-poupanca", "Poupança", "Plano de Poupança", "Poupe de forma regular e planeada.", $"{Site}/pt/particulares/poupancainvestimento/plano-de-poupanca/"),
        new("conta-kandengue", "Poupança", "Conta Kandengue BFA", "Poupança para os mais novos.", $"{Site}/pt/particulares/poupancainvestimento/conta-kandengue-bfa/"),
        new("kwik", "Serviços", "KWiK", "Envie e receba dinheiro de forma imediata.", $"{Site}/pt/particulares/servicos/kwik/"),
        new("e-pin", "Serviços", "E-PIN BFA", "Compras online mais seguras.", $"{Site}/pt/particulares/servicos/e-pin-bfa/"),
    ];

    public async Task<IReadOnlyList<ExchangeRateDto>> ExchangeRatesAsync(CancellationToken ct) =>
        await db.ExchangeRates.AsNoTracking().OrderBy(r => r.Currency)
            .Select(r => new ExchangeRateDto(r.Currency, r.Buy, r.Sell, r.Source, r.UpdatedAt)).ToListAsync(ct);

    public IReadOnlyList<ProductDto> Products() => Catalogue;

    // Taken from the public "Contactos" page of bfa.ao.
    private static readonly IReadOnlyList<ContactDto> ContactList =
    [
        new("phone", "Linha de Atendimento BFA", "923 120 120", "tel:923120120"),
        new("email", "Informações e reclamações", "bfa@bfa.ao", "mailto:bfa@bfa.ao"),
        new("email", "Recrutamento", "recrutamento@bfa.ao", "mailto:recrutamento@bfa.ao"),
        new("web", "Website", "www.bfa.ao", "https://www.bfa.ao"),
        new("branch", "Rede de balcões", "Encontre o balcão BFA mais próximo", $"{Site}/pt/particulares/lojas/"),
    ];

    public IReadOnlyList<ContactDto> Contacts() => ContactList;

    public AboutInfo About() => new("Quem somos", $"{Site}/pt/o-bfa/conheca-o-bfa/quem-somos/");
}
