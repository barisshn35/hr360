using System.Globalization;
using System.Text;
using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>
/// Madde 62 — banka toplu maaş ödeme dosyaları. Şablonlar:
///   generic  — HR360 genel CSV (önceki sürümle aynı).
///   ornek-a  — ÖRNEK ŞABLON A: noktalı virgüllü CSV, virgüllü ondalık, ASCII büyük harf ad, sıra no ve toplam satırı.
///   ornek-b  — ÖRNEK ŞABLON B: sabit uzunluklu TXT (H başlık / D detay / T toplam kaydı, tutarlar kuruş).
///   custom   — şirketin tanımladığı sütun düzeni (ayırıcı, ondalık, başlık, kodlama, ASCII).
/// Örnek şablonlar yaygın banka dosyalarına BENZER ama hiçbir bankanın resmî biçimi olarak DOĞRULANMAMIŞTIR;
/// bankanın güncel teknik dokümanıyla karşılaştırın. Bankaya bağlanılmaz; yalnızca dosya üretilir.
/// IBAN mod-97 ile doğrulanır; geçersiz IBAN'lı kişi dosyaya alınmaz (uyarı listesinde döner).
/// </summary>
public static class BankFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Özel düzende kullanılabilecek alan anahtarları.</summary>
    public static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "name", "iban", "amount", "currency", "description", "date", "tckn", "employeeNo", "seq" };

    public sealed record Line(int Seq, string Name, string Iban, decimal Amount, string Currency, string? Tckn, string EmployeeNo);

    /// <summary>Türkçe karakterleri ASCII'ye çevirir (banka dosyalarında yaygın gereksinim).</summary>
    public static string Ascii(string s)
    {
        var map = new Dictionary<char, char> { ['Ç'] = 'C', ['ç'] = 'c', ['Ğ'] = 'G', ['ğ'] = 'g', ['İ'] = 'I', ['ı'] = 'i', ['Ö'] = 'O', ['ö'] = 'o', ['Ş'] = 'S', ['ş'] = 's', ['Ü'] = 'U', ['ü'] = 'u', ['Â'] = 'A', ['â'] = 'a', ['Î'] = 'I', ['î'] = 'i', ['Û'] = 'U', ['û'] = 'u' };
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(map.TryGetValue(c, out var r) ? r : c < 128 ? c : '?');
        return sb.ToString();
    }

    public static string Describe(BankSettings b, PayrollPeriod period) =>
        (b.Description ?? "{yil}-{ay} maas").Replace("{yil}", period.Year.ToString(Inv)).Replace("{ay}", period.Month.ToString("00", Inv));

    /// <summary>Dosyaya girecek satırlar (net &gt; 0, IBAN geçerli); geçersiz IBAN uyarıya düşer.</summary>
    public static (List<Line> Lines, List<string> Warnings) Lines(IReadOnlyList<Payslip> slips, IReadOnlyDictionary<Guid, ExportPerson> people)
    {
        var warnings = new List<string>();
        var lines = new List<Line>();
        var seq = 0;
        foreach (var s in slips.Where(x => x.Net > 0).OrderBy(x => people.TryGetValue(x.EmployeeId, out var p) ? p.LastName : "").ThenBy(x => x.EmployeeId))
        {
            if (!people.TryGetValue(s.EmployeeId, out var p)) continue;
            var iban = p.Iban?.Replace(" ", "").ToUpperInvariant();
            if (!Exporters.ValidIban(iban)) { warnings.Add($"{p.FirstName} {p.LastName}: IBAN eksik ya da geçersiz (dosyaya alınmadı)"); continue; }
            lines.Add(new(++seq, Exporters.Upper(p.FirstName + " " + p.LastName), iban!, s.Net, s.Currency, p.NationalId, p.Id.ToString("N")[..8].ToUpperInvariant()));
        }
        return (lines, warnings);
    }

    private static Encoding Enc(string name)
    {
        if (name is "iso-8859-9" or "windows-1254")
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(name == "iso-8859-9" ? 28599 : 1254);
        }
        return new UTF8Encoding(false);
    }

    private static byte[] Bytes(string text, string encoding)
    {
        var b = Enc(encoding).GetBytes(text);
        return encoding == "utf-8-bom" ? Encoding.UTF8.GetPreamble().Concat(b).ToArray() : b;
    }

    private static string Fix(string s, int len, bool left = false) => s.Length >= len ? s[..len] : left ? s.PadLeft(len) : s.PadRight(len);
    private static string Kurus(decimal v, int len) => ((long)Math.Round(v * 100, MidpointRounding.AwayFromZero)).ToString(Inv).PadLeft(len, '0');

    public static ExportResult Build(PayrollPeriod period, IReadOnlyList<Payslip> slips, IReadOnlyDictionary<Guid, ExportPerson> people, BankSettings b, DateOnly payDate)
    {
        var template = b.Template ?? "generic";
        if (template == "generic") return Exporters.Bank(period, slips, people);
        var (lines, warnings) = Lines(slips, people);
        var desc = Describe(b, period);
        var total = lines.Sum(l => l.Amount);
        var name = $"banka-maas-{period.Year}-{period.Month:00}";
        switch (template)
        {
            case "ornek-a":
            {
                var sb = new StringBuilder("SIRA;AD SOYAD;IBAN;TUTAR;DOVIZ;ACIKLAMA;ODEME TARIHI\r\n");
                foreach (var l in lines)
                    sb.Append($"{l.Seq};{Ascii(l.Name).Replace(";", " ")};{l.Iban};{l.Amount.ToString("0.00", Inv).Replace('.', ',')};{l.Currency};{Ascii(desc).Replace(";", " ")};{payDate:dd.MM.yyyy}\r\n");
                sb.Append($"TOPLAM;{lines.Count};;{total.ToString("0.00", Inv).Replace('.', ',')};TRY;;\r\n");
                return new(Bytes(sb.ToString(), "iso-8859-9"), name + "-ornek-a.csv", "text/csv", lines.Count, warnings);
            }
            case "ornek-b":
            {
                var sb = new StringBuilder();
                sb.Append('H').Append(Fix(Ascii(b.CompanyCode ?? ""), 10)).Append(payDate.ToString("yyyyMMdd", Inv))
                  .Append(Fix((b.DebitIban ?? "").Replace(" ", "").ToUpperInvariant(), 26)).Append(lines.Count.ToString(Inv).PadLeft(6, '0'))
                  .Append(Kurus(total, 15)).Append("\r\n");
                foreach (var l in lines)
                    sb.Append('D').Append(l.Seq.ToString(Inv).PadLeft(6, '0')).Append(Fix(l.Iban, 26)).Append(Fix(Ascii(l.Name), 40))
                      .Append(Kurus(l.Amount, 15)).Append(Fix(l.Currency, 3)).Append(Fix(Ascii(desc), 30)).Append("\r\n");
                sb.Append('T').Append(lines.Count.ToString(Inv).PadLeft(6, '0')).Append(Kurus(total, 15)).Append("\r\n");
                return new(Bytes(sb.ToString(), "iso-8859-9"), name + "-ornek-b.txt", "text/plain", lines.Count, warnings);
            }
            default:
            {
                var c = b.Custom ?? new BankCustomLayout();
                var d = c.Delimiter;
                string Clean(string v) => (c.Ascii ? Ascii(v) : v).Replace(d, " ").Replace("\r", " ").Replace("\n", " ");
                string Amt(decimal v) => c.DecimalSeparator == "," ? v.ToString("0.00", Inv).Replace('.', ',') : v.ToString("0.00", Inv);
                string Val(Line l, string key) => key switch
                {
                    "name" => Clean(l.Name), "iban" => l.Iban, "amount" => Amt(l.Amount), "currency" => l.Currency,
                    "description" => Clean(desc), "date" => payDate.ToString(c.DateFormat, Inv), "tckn" => l.Tckn ?? "",
                    "employeeNo" => l.EmployeeNo, "seq" => l.Seq.ToString(Inv), _ => "",
                };
                var sb = new StringBuilder();
                if (c.Header) sb.Append(string.Join(d, c.Columns.Select(k => k.ToUpperInvariant()))).Append("\r\n");
                foreach (var l in lines) sb.Append(string.Join(d, c.Columns.Select(k => Val(l, k)))).Append("\r\n");
                if (c.TotalsLine)
                    sb.Append(string.Join(d, c.Columns.Select((k, i) => k == "amount" ? Amt(total) : i == 0 ? "TOPLAM" : k == "seq" ? lines.Count.ToString(Inv) : ""))).Append("\r\n");
                if (c.Columns.Contains("tckn")) warnings.Add("Dosya T.C. kimlik numarası içeriyor; yalnızca banka gerçekten istiyorsa kullanın (KVKK veri en aza indirme).");
                return new(Bytes(sb.ToString(), c.Encoding), name + (d == "\t" ? ".txt" : ".csv"), d == "\t" ? "text/plain" : "text/csv", lines.Count, warnings);
            }
        }
    }
}
