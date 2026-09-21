using System.Globalization;
using System.Text;
using BfaNet.Domain;

namespace BfaNet.Application.Assistant;

public enum SpendCategory
{
    Alimentacao, Habitacao, Transportes, Saude, Educacao, Telecomunicacoes, Televisao, Energia, Estado,
    Credito, Comissoes, Compras, Servicos, Transferencias, Outros,
}

public static class SpendCategorizer
{
    public static string Label(SpendCategory c) => c switch
    {
        SpendCategory.Alimentacao => "Alimentação", SpendCategory.Habitacao => "Habitação", SpendCategory.Transportes => "Transportes",
        SpendCategory.Saude => "Saúde", SpendCategory.Educacao => "Educação", SpendCategory.Telecomunicacoes => "Telecomunicações",
        SpendCategory.Televisao => "Televisão", SpendCategory.Energia => "Energia", SpendCategory.Estado => "Impostos e Estado",
        SpendCategory.Credito => "Crédito", SpendCategory.Comissoes => "Comissões", SpendCategory.Compras => "Compras",
        SpendCategory.Servicos => "Serviços", SpendCategory.Transferencias => "Transferências", _ => "Outros",
    };

    // Accent-stripped, lower-case fragments. Order matters: the first category with a hit wins.
    private static readonly (SpendCategory Category, string[] Words)[] Rules =
    [
        (SpendCategory.Habitacao, ["renda", "aluguer", "condominio", "imobiliaria"]),
        (SpendCategory.Alimentacao, ["mercado", "super", "kero", "hiper", "shoprite", "restaurante", "pizza", "padaria", "cafe", "mercearia", "lanche", "talho"]),
        (SpendCategory.Transportes, ["combustivel", "gasolina", "gasoleo", "sonangol", "taxi", "uber", "candongueiro", "bilhete", "parque", "portagem"]),
        (SpendCategory.Saude, ["farmacia", "clinica", "hospital", "medic", "saude", "dentista"]),
        (SpendCategory.Educacao, ["escola", "propina", "universidade", "colegio", "curso", "creche", "livraria"]),
        (SpendCategory.Compras, ["compra", "loja", "moveis", "decor", "roupa", "sapataria", "eletro", "shopping"]),
    ];

    public static SpendCategory Classify(TransactionKind kind, string? description, string? counterparty, string? externalReference)
    {
        switch (kind)
        {
            case TransactionKind.StatePayment: return SpendCategory.Estado;
            case TransactionKind.LoanRepayment: return SpendCategory.Credito;
            case TransactionKind.Fee: return SpendCategory.Comissoes;
            case TransactionKind.TopUp:
                var provider = externalReference?.Split(':', 2)[0];
                return provider switch { "Dstv" or "Zap" => SpendCategory.Televisao, "Ende" => SpendCategory.Energia, _ => SpendCategory.Telecomunicacoes };
        }

        // Match on word starts, never on raw substrings: "renda" must hit "Renda de casa" but not "Prenda".
        var tokens = Tokenize(Normalize($"{description} {counterparty}"));
        foreach (var (category, words) in Rules)
            if (words.Any(w => tokens.Any(t => t.StartsWith(w, StringComparison.Ordinal)))) return category;

        return kind switch { TransactionKind.ServicePayment => SpendCategory.Servicos, TransactionKind.Transfer => SpendCategory.Transferencias, _ => SpendCategory.Outros };
    }

    private static string[] Tokenize(string normalized) =>
        normalized.Split([' ', '-', '.', ',', ';', ':', '/', '(', ')', '&', '_', '\'', '"', '!', '?', '+', '#', '@', '%'], StringSplitOptions.RemoveEmptyEntries);

    public static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }
}
