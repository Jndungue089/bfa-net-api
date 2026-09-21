using System.Globalization;
using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace BfaNet.Infrastructure.Services.Pdf;

/// <summary>Shared look of every PDF the bank issues: fonts, brand colours, logo and pt-AO number/date formatting.</summary>
internal static class PdfKit
{
    public const string Serif = "Liberation Serif";
    public const string Navy = "#0D1B5E", Orange = "#F05D1A", Muted = "#64748B", Line = "#E2E8F0", Ink = "#1E293B", Green = "#059669", Red = "#DC2626";
    public static readonly byte[] Logo;
    private static readonly CultureInfo Pt = new("pt-PT");
    private static readonly string[] Months = ["Jan", "Fev", "Mar", "Abr", "Mai", "Jun", "Jul", "Ago", "Set", "Out", "Nov", "Dez"];

    static PdfKit()
    {
        QuestPDF.Settings.License = LicenseType.Community; // free for OSS / small businesses (see backend README)
        var asm = Assembly.GetExecutingAssembly();
        // QuestPDF ignores system fonts by default: ship the serif with the assembly (Liberation Serif, SIL OFL,
        // metric-compatible with Times New Roman) so the PDF looks the same on every server.
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            FontManager.RegisterFont(stream);
        }
        using var logo = asm.GetManifestResourceStream("BfaNet.Infrastructure.Assets.logo-mark.png")
            ?? throw new InvalidOperationException("Logo do BFA em falta (recurso embebido).");
        using var ms = new MemoryStream();
        logo.CopyTo(ms);
        Logo = ms.ToArray();
    }

    /// <summary>Forces the static constructor (fonts + logo) to run.</summary>
    public static void Init() { }

    public static void PageDefaults(PageDescriptor page)
    {
        page.Margin(36);
        page.DefaultTextStyle(t => t.FontFamily(Serif).FontSize(11).FontColor(Ink));
    }

    /// <summary>Logo top-left, title and issue time top-right.</summary>
    public static void Header(PageDescriptor page, string title, string issuedAt) =>
        page.Header().PaddingBottom(12).BorderBottom(1).BorderColor(Line).Row(row =>
        {
            row.ConstantItem(150).AlignLeft().AlignMiddle().Image(Logo).FitWidth();
            row.RelativeItem().AlignRight().AlignMiddle().Column(col =>
            {
                col.Item().AlignRight().Text(title).FontSize(20).Bold().FontColor(Navy);
                col.Item().AlignRight().Text($"Emitido em {issuedAt}").FontSize(9.5f).FontColor(Muted);
            });
        });

    public static void Footer(PageDescriptor page, params string[] lines) =>
        page.Footer().BorderTop(1).BorderColor(Line).PaddingTop(8).Row(row =>
        {
            row.RelativeItem().Column(f => { foreach (var l in lines) f.Item().Text(l).FontSize(9).FontColor(Muted); });
            row.ConstantItem(90).AlignRight().AlignBottom().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(9).FontColor(Muted));
                t.Span("Página "); t.CurrentPageNumber(); t.Span(" de "); t.TotalPages();
            });
        });

    public static string Money(decimal v, string currency, bool sign = false)
    {
        var abs = Math.Abs(v).ToString("N2", Pt).Replace(' ', ' ').Replace(' ', ' ');
        var symbol = currency == "AOA" ? "Kz" : currency;
        return $"{(v < 0 ? "-" : sign && v > 0 ? "+" : "")}{abs} {symbol}";
    }

    /// <summary>Luanda time (UTC+1, no DST).</summary>
    public static string When(DateTimeOffset t)
    {
        var l = t.ToOffset(TimeSpan.FromHours(1));
        return $"{l.Day:D2} {Months[l.Month - 1]} {l.Year}, {l.Hour:D2}:{l.Minute:D2}";
    }

    public static string Day(DateTimeOffset t)
    {
        var l = t.ToOffset(TimeSpan.FromHours(1));
        return $"{l.Day:D2} {Months[l.Month - 1]} {l.Year}";
    }

    public static string Day(DateOnly d) => $"{d.Day:D2} {Months[d.Month - 1]} {d.Year}";
}
