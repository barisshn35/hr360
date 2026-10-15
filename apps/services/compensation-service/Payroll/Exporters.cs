using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>
/// Dışa aktarım dosyalarının şifrelenmesi ve TCKN/IBAN'ın (engagement-service'in şifrelediği
/// "enc1:" / "enc2:&lt;kimlik&gt;:" biçimi) açılması. Anahtarlar: TENANT_SECRET_KEYS / TENANT_SECRET_KEY
/// (bkz. <see cref="CompensationService.Security.KeyRing"/>).
/// </summary>
public static class ExportCrypto
{
    public static bool Enabled => CompensationService.Security.KeyRing.Enabled;

    public static byte[] Encrypt(byte[] plain)
    {
        if (!Enabled) throw new InvalidOperationException("TENANT_SECRET_KEY tanımlı değil; dosya şifrelenemiyor.");
        return CompensationService.Security.KeyRing.SealBytes(plain);
    }

    public static byte[] Decrypt(byte[] data)
    {
        if (!Enabled) throw new InvalidOperationException("TENANT_SECRET_KEY tanımlı değil.");
        return CompensationService.Security.KeyRing.OpenBytes(data);
    }

    /// <summary>engagement_profiles'taki şifreli TCKN/IBAN'ı açar (düz metinse olduğu gibi).</summary>
    public static string? OpenPii(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !CompensationService.Security.KeyRing.IsSealed(stored)) return stored;
        if (!Enabled) return null;
        try { return CompensationService.Security.KeyRing.Open(stored); }
        catch (Exception) { return null; }
    }
}

/// <summary>Dışa aktarım için çalışan satırı (yalnızca gerekli alanlar).</summary>
public sealed record ExportPerson(Guid Id, string FirstName, string LastName, string? NationalId, string? Iban,
    string? Department, DateOnly HireDate, DateOnly? TerminationDate);

public sealed record ExportResult(byte[] Content, string FileName, string ContentType, int Rows, List<string> Warnings);

public sealed record AccountMap(string Salary = "770.01", string EmployerSgk = "770.02", string NetPayable = "335",
    string IncomeTax = "360.01", string StampTax = "360.02", string Sgk = "361", string Advances = "196");

public static class Exporters
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string M(decimal v) => v.ToString("0.00", Inv);
    private static string Csv(string? v) => v is null ? "" : v.Contains(';') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>Türkçe karakterleri SGK'nın beklediği büyük harfe çevirir (İ/I ayrımıyla).</summary>
    public static string Upper(string s) => s.Trim().ToUpper(new CultureInfo("tr-TR"));

    public static bool ValidTckn(string? t)
    {
        if (t is null || t.Length != 11 || !t.All(char.IsDigit) || t[0] == '0') return false;
        var d = t.Select(c => c - '0').ToArray();
        var c10 = ((d[0] + d[2] + d[4] + d[6] + d[8]) * 7 - (d[1] + d[3] + d[5] + d[7])) % 10;
        if (c10 < 0) c10 += 10;
        return c10 == d[9] && d.Take(10).Sum() % 10 == d[10];
    }

    /// <summary>
    /// SGK Aylık Prim ve Hizmet Belgesi (e-Bildirge) aktarım taslağı, XML. Eksik gün nedeni:
    /// ücretsiz izin için 13 ("diğer nedenler"); işe giriş/çıkış günü dönem içindeyse yazılır.
    /// Belge türü 1 (tüm sigorta kolları), kanun 05510. TCKN'si eksik/geçersiz kişi dosyaya
    /// alınmaz, uyarı listesinde döner. SGK'ya yüklemeden önce güncel şemayla kontrol edilmelidir.
    /// </summary>
    public static ExportResult SgkAphb(PayrollPeriod period, IReadOnlyList<Payslip> slips, IReadOnlyDictionary<Guid, ExportPerson> people, string employerTitle)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
        {
            w.WriteStartDocument();
            w.WriteComment(" HR360 - Aylik Prim ve Hizmet Belgesi aktarim taslagi. e-Bildirge'ye yuklemeden once kontrol edin. ");
            w.WriteStartElement("AYLIKPRIMHIZMETBELGESI");
            w.WriteAttributeString("yil", period.Year.ToString(Inv));
            w.WriteAttributeString("ay", period.Month.ToString("00", Inv));
            w.WriteAttributeString("belgeTuru", "1");
            w.WriteAttributeString("kanunNo", "05510");
            w.WriteAttributeString("isveren", employerTitle);
            var start = new DateOnly(period.Year, period.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var rows = 0;
            foreach (var s in slips.OrderBy(x => people.TryGetValue(x.EmployeeId, out var p) ? p.LastName : ""))
            {
                if (!people.TryGetValue(s.EmployeeId, out var p)) continue;
                if (!ValidTckn(p.NationalId)) { warnings.Add($"{p.FirstName} {p.LastName}: T.C. kimlik numarası eksik ya da geçersiz"); continue; }
                w.WriteStartElement("SIGORTALI");
                w.WriteElementString("TCKIMLIKNO", p.NationalId);
                w.WriteElementString("AD", Upper(p.FirstName));
                w.WriteElementString("SOYAD", Upper(p.LastName));
                w.WriteElementString("PRIMGUN", s.PaidDays.ToString(Inv));
                w.WriteElementString("PRIMEESASKAZANC", M(s.SgkBase));
                w.WriteElementString("EKSIKGUNSAYISI", s.UnpaidDays.ToString(Inv));
                w.WriteElementString("EKSIKGUNNEDENI", s.UnpaidDays > 0 ? "13" : "");
                w.WriteElementString("ISEGIRISGUN", p.HireDate >= start && p.HireDate <= end ? p.HireDate.Day.ToString("00", Inv) : "");
                w.WriteElementString("ISTENCIKISGUN", p.TerminationDate is { } t && t >= start && t <= end ? t.Day.ToString("00", Inv) : "");
                w.WriteEndElement();
                rows++;
            }
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
            return new ExportResult(Encoding.UTF8.GetBytes(sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"")),
                $"sgk-aphb-{period.Year}-{period.Month:00}.xml", "application/xml", rows, warnings);
        }
    }

    /// <summary>Dönem içindeki işe giriş ve işten ayrılışlar (SGK işe giriş/ayrılış bildirgesi için liste).</summary>
    public static ExportResult SgkHires(PayrollPeriod period, IEnumerable<ExportPerson> people)
    {
        var start = new DateOnly(period.Year, period.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var warnings = new List<string>();
        var sb = new StringBuilder("Hareket;TCKN;Ad;Soyad;Tarih\n");
        var rows = 0;
        foreach (var p in people.OrderBy(x => x.LastName))
        {
            var hire = p.HireDate >= start && p.HireDate <= end;
            var left = p.TerminationDate is { } t && t >= start && t <= end;
            if (!hire && !left) continue;
            if (!ValidTckn(p.NationalId)) warnings.Add($"{p.FirstName} {p.LastName}: T.C. kimlik numarası eksik ya da geçersiz");
            if (hire) { sb.Append($"İşe giriş;{p.NationalId};{Csv(p.FirstName)};{Csv(p.LastName)};{p.HireDate:dd.MM.yyyy}\n"); rows++; }
            if (left) { sb.Append($"İşten ayrılış;{p.NationalId};{Csv(p.FirstName)};{Csv(p.LastName)};{p.TerminationDate:dd.MM.yyyy}\n"); rows++; }
        }
        return new ExportResult(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            $"sgk-giris-cikis-{period.Year}-{period.Month:00}.csv", "text/csv", rows, warnings);
    }

    /// <summary>
    /// Maaş ödemesi IBAN denetimi: TR IBAN'ı (26 karakter) ve mod-97 = 1 (ISO 13616). Türkiye'deki bankalara
    /// verilen toplu ödeme dosyaları yalnızca TR IBAN'ı kabul ettiği için yabancı IBAN geçersiz sayılır.
    /// </summary>
    public static bool ValidIban(string? raw)
    {
        var iban = raw?.Replace(" ", "").ToUpperInvariant();
        return iban is { Length: 26 } && iban.StartsWith("TR") && ValidIbanAny(iban);
    }

    /// <summary>Herhangi bir ülkenin IBAN'ı: biçim (2 harf + 2 rakam, 15–34 karakter) ve mod-97 = 1.</summary>
    public static bool ValidIbanAny(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var iban = raw.Replace(" ", "").ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]) || !iban.All(char.IsAsciiLetterOrDigit)) return false;
        return IbanMod97(iban) == 1;
    }

    /// <summary>IBAN'ın mod-97 kalanı (ilk dört karakter sona taşınır, harfler 10–35).</summary>
    public static int IbanMod97(string iban)
    {
        var r = iban[4..] + iban[..4];
        var mod = 0;
        foreach (var c in r)
        {
            if (char.IsAsciiDigit(c)) mod = (mod * 10 + (c - '0')) % 97;
            else { var v = c - 'A' + 10; mod = (mod * 100 + v) % 97; }
        }
        return mod;
    }

    /// <summary>Banka toplu maaş ödeme dosyası (genel CSV): ad soyad, IBAN, net tutar, açıklama.</summary>
    public static ExportResult Bank(PayrollPeriod period, IReadOnlyList<Payslip> slips, IReadOnlyDictionary<Guid, ExportPerson> people)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder("AdSoyad;IBAN;Tutar;ParaBirimi;Aciklama\n");
        var rows = 0;
        decimal total = 0;
        foreach (var s in slips.Where(x => x.Net > 0).OrderBy(x => people.TryGetValue(x.EmployeeId, out var p) ? p.LastName : ""))
        {
            if (!people.TryGetValue(s.EmployeeId, out var p)) continue;
            var iban = p.Iban?.Replace(" ", "").ToUpperInvariant();
            if (!ValidIban(iban)) { warnings.Add($"{p.FirstName} {p.LastName}: IBAN eksik ya da geçersiz (dosyaya alınmadı)"); continue; }
            sb.Append($"{Csv(Upper(p.FirstName + " " + p.LastName))};{iban};{M(s.Net)};{s.Currency};{period.Year}-{period.Month:00} maas\n");
            rows++; total += s.Net;
        }
        sb.Append($"TOPLAM;;{M(total)};;{rows} kayit\n");
        return new ExportResult(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            $"banka-maas-{period.Year}-{period.Month:00}.csv", "text/csv", rows, warnings);
    }

    public sealed record JournalLine(string Account, string AccountName, string CostCenter, decimal Debit, decimal Credit, string Description);

    /// <summary>
    /// Muhasebe fişi: masraf merkezi (bölüm) bazında özet; kişi bazlı veri içermez.
    /// Borç: ücret gideri (brüt) ve işveren SGK/işsizlik payı. Alacak: personele borçlar (net),
    /// ödenecek GV ve damga vergisi (istisna düşülmüş), ödenecek SGK (işçi + işveren), avans kesintileri.
    /// </summary>
    public static List<JournalLine> Journal(PayrollPeriod period, IReadOnlyList<Payslip> slips, IReadOnlyDictionary<Guid, ExportPerson> people, AccountMap a,
        IReadOnlyDictionary<string, string>? costCenters = null)
    {
        var lines = new List<JournalLine>();
        var desc = $"{period.Year}-{period.Month:00} bordro";
        // Madde 64: bölüm adı → şirketin masraf merkezi kodu (eşleme yoksa bölüm adı).
        string Cc(string dept) => costCenters is not null && costCenters.TryGetValue(dept, out var code) && !string.IsNullOrWhiteSpace(code) ? code.Trim() : dept;
        foreach (var g in slips.GroupBy(s => Cc(people.TryGetValue(s.EmployeeId, out var p) ? p.Department ?? "Genel" : "Genel")).OrderBy(g => g.Key))
        {
            var cc = g.Key;
            lines.Add(new(a.Salary, "Ücret giderleri", cc, g.Sum(s => s.Gross), 0, desc));
            lines.Add(new(a.EmployerSgk, "SGK işveren payı giderleri", cc, g.Sum(s => s.SgkEmployer + s.UnemploymentEmployer), 0, desc));
            lines.Add(new(a.NetPayable, "Personele borçlar", cc, 0, g.Sum(s => s.Net), desc));
            lines.Add(new(a.IncomeTax, "Ödenecek gelir vergisi", cc, 0, g.Sum(s => s.IncomeTax - s.IncomeTaxExemption), desc));
            lines.Add(new(a.StampTax, "Ödenecek damga vergisi", cc, 0, g.Sum(s => s.StampTax - s.StampTaxExemption), desc));
            lines.Add(new(a.Sgk, "Ödenecek SGK primleri", cc, 0, g.Sum(s => s.SgkEmployee + s.UnemploymentEmployee + s.SgkEmployer + s.UnemploymentEmployer), desc));
            var adv = g.Sum(s => s.Deductions);
            if (adv != 0) lines.Add(new(a.Advances, "Personel avansları ve diğer kesintiler", cc, 0, adv, desc));
        }
        return lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
    }

    /// <summary>Muhasebe yazılımına aktarım CSV'si. format: generic | logo | mikro | netsis (sütun adları/sırası).</summary>
    /// <summary>Fişin borç-alacak farkı (kuruş yuvarlaması dahil; dengeli fişte 0).</summary>
    public static decimal JournalDifference(IEnumerable<JournalLine> lines)
    {
        var list = lines.ToList();
        return list.Sum(l => l.Debit) - list.Sum(l => l.Credit);
    }

    public static bool JournalBalanced(IEnumerable<JournalLine> lines) => Math.Abs(JournalDifference(lines)) < 0.005m;

    public static ExportResult Accounting(PayrollPeriod period, IReadOnlyList<Payslip> slips, IReadOnlyDictionary<Guid, ExportPerson> people, string format, AccountMap map,
        IReadOnlyDictionary<string, string>? costCenters = null)
    {
        var lines = Journal(period, slips, people, map, costCenters);
        var date = new DateOnly(period.Year, period.Month, 1).AddMonths(1).AddDays(-1).ToString("dd.MM.yyyy");
        var sb = new StringBuilder();
        switch (format)
        {
            case "logo":
                sb.Append("FIS TARIHI;HESAP KODU;MASRAF MERKEZI;ACIKLAMA;BORC;ALACAK\n");
                foreach (var l in lines) sb.Append($"{date};{l.Account};{Csv(l.CostCenter)};{Csv(l.Description)};{M(l.Debit)};{M(l.Credit)}\n");
                break;
            case "mikro":
                sb.Append("Tarih;Hesap Kodu;Hesap Adi;Proje/Masraf Merkezi;Aciklama;Tutar;B/A\n");
                foreach (var l in lines) sb.Append($"{date};{l.Account};{Csv(l.AccountName)};{Csv(l.CostCenter)};{Csv(l.Description)};{M(l.Debit + l.Credit)};{(l.Debit != 0 ? "B" : "A")}\n");
                break;
            case "netsis":
                sb.Append("TARIH;HESAPKODU;ACIKLAMA;MASRAFMERKEZI;BORC;ALACAK\n");
                foreach (var l in lines) sb.Append($"{date};{l.Account};{Csv(l.Description)};{Csv(l.CostCenter)};{M(l.Debit)};{M(l.Credit)}\n");
                break;
            default:
                sb.Append("Tarih;HesapKodu;HesapAdi;MasrafMerkezi;Borc;Alacak;Aciklama\n");
                foreach (var l in lines) sb.Append($"{date};{l.Account};{Csv(l.AccountName)};{Csv(l.CostCenter)};{M(l.Debit)};{M(l.Credit)};{Csv(l.Description)}\n");
                break;
        }
        var warnings = new List<string>();
        var diff = JournalDifference(lines);
        if (!JournalBalanced(lines)) warnings.Add($"Fiş dengesiz: borç-alacak farkı {M(diff)}");
        return new ExportResult(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
            $"muhasebe-{format}-{period.Year}-{period.Month:00}.csv", "text/csv", lines.Count, warnings);
    }

    /// <summary>Taksit tutarı: son taksit yuvarlama farkını taşır.</summary>
    public static decimal Installment(decimal amount, int count, int index)
    {
        var each = Math.Round(amount / count, 2, MidpointRounding.AwayFromZero);
        return index == count - 1 ? amount - each * (count - 1) : each;
    }

    /// <summary>Dönem (yıl, ay) avansın kaçıncı taksidine denk geliyor (yoksa -1).</summary>
    public static int InstallmentIndex(SalaryAdvance a, int year, int month)
    {
        var idx = (year - a.StartYear) * 12 + (month - a.StartMonth);
        return idx >= 0 && idx < a.Installments ? idx : -1;
    }
}
