using System.Globalization;
using System.Text;
using System.Xml;
using CompensationService.Models;

namespace CompensationService.Payroll;

/// <summary>APHB satırı için çalışan bilgisi (yalnızca bildirimde gereken alanlar).</summary>
public sealed record AphbPerson(Guid Id, string FirstName, string LastName, string? NationalId, DateOnly HireDate, DateOnly? TerminationDate,
    string? ExitReason, string? OccupationCode, string? DocumentType, string? LawNo, bool Sgdp);

/// <summary>Bildirime girecek tek sigortalı satırı (hesaplanmış).</summary>
public sealed record AphbRow(Guid EmployeeId, string Name, string? NationalId, string DocumentType, string LawNo, int PrimDays, int MissingDays,
    string MissingReason, decimal Wage, decimal Bonus, decimal Pek, string? EntryDay, string? ExitDay, string? ExitReasonCode, string? OccupationCode);

/// <summary>Doğrulama bulgusu. Level: error (dosyaya alınmaz) | warning (dosyaya alınır, kontrol edin).</summary>
public sealed record AphbIssue(Guid EmployeeId, string Name, string Level, string Code, string Message);

/// <summary>
/// Madde 58 — SGK Aylık Prim ve Hizmet Belgesi (MUHSGK'nın SGK bölümü) aktarım dosyası. Kapanmış dönemin
/// pusulalarından üretilir: TCKN (şifreli alandan açılır), ad/soyad, prim günü, eksik gün sayısı ve nedeni
/// (izin türü eşlemesinden; birden fazla neden = 12), hak edilen ücret + prim/ikramiye = PEK, işe giriş/
/// işten çıkış günü ve çıkış nedeni, meslek kodu, belge türü ve kanun (çalışan > şirket varsayılanı; SGDP = 02).
///
/// UYARI: e-Bildirge "toplu dosya" yerleşimi kamuya açık kaynaklardan derlenmiştir; SGK'ya yüklemeden önce
/// güncel şemayla karşılaştırın (bkz. docs/bordro/README.md — doğrulanmamış alanlar). Gerçek SGK sistemine
/// bağlanılmaz; yalnızca dosya üretilir.
/// </summary>
public static class Aphb
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string M(decimal v) => v.ToString("0.00", Inv);

    /// <summary>Eksik gün nedeni: ücretten düşen izin türlerinden gün > 0 olanların kodu; birden fazlaysa 12, yoksa 13 (diğer).</summary>
    public static string MissingReason(int missingDays, IReadOnlyDictionary<string, decimal>? leaveDaysByType, SgkSettings s)
    {
        if (missingDays <= 0) return "";
        var codes = (leaveDaysByType ?? new Dictionary<string, decimal>())
            .Where(kv => kv.Value > 0)
            .Select(kv => s.MissingDayCodes.FirstOrDefault(m => m.ReducesPay && string.Equals(m.LeaveType, kv.Key, StringComparison.OrdinalIgnoreCase))?.Code)
            .Where(c => c is not null).Distinct().ToList();
        return codes.Count switch { 0 => "13", 1 => codes[0]!, _ => SgkSettings.MultipleReasonsCode };
    }

    public static (List<AphbRow> Rows, List<AphbIssue> Issues) Build(PayrollPeriod period, IReadOnlyList<Payslip> slips,
        IReadOnlyDictionary<Guid, AphbPerson> people, IReadOnlyDictionary<Guid, Dictionary<string, decimal>> leaveDays,
        SgkSettings settings, PayrollParams p)
    {
        var rows = new List<AphbRow>();
        var issues = new List<AphbIssue>();
        var start = new DateOnly(period.Year, period.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        foreach (var s in slips.OrderBy(x => people.TryGetValue(x.EmployeeId, out var pp) ? pp.LastName : "").ThenBy(x => x.EmployeeId))
        {
            if (!people.TryGetValue(s.EmployeeId, out var person))
            {
                issues.Add(new(s.EmployeeId, "—", "error", "no_person", "Çalışan kaydı bulunamadı"));
                continue;
            }
            var name = $"{person.FirstName} {person.LastName}".Trim();
            void Issue(string level, string code, string msg) => issues.Add(new(s.EmployeeId, name, level, code, msg));
            var ok = true;
            if (!Exporters.ValidTckn(person.NationalId)) { Issue("error", "tckn", "T.C. kimlik numarası eksik ya da geçersiz (dosyaya alınmaz)"); ok = false; }
            if (s.PaidDays is < 0 or > 30) { Issue("error", "days", $"Prim günü {s.PaidDays}: 0–30 arasında olmalı"); ok = false; }
            if (s.PaidDays + s.UnpaidDays != 30) Issue("warning", "days_sum", $"Prim günü + eksik gün 30 değil ({s.PaidDays} + {s.UnpaidDays})");
            if (string.IsNullOrWhiteSpace(person.OccupationCode)) Issue("warning", "occupation", "Meslek kodu girilmemiş");
            var ratio = s.PaidDays / 30m;
            var floor = Math.Round(p.MinimumWageGross * ratio, 2, MidpointRounding.AwayFromZero);
            var ceiling = Math.Round(p.MinimumWageGross * p.SgkCeilingMultiplier * ratio, 2, MidpointRounding.AwayFromZero);
            if (s.PaidDays > 0 && s.SgkBase < floor - 0.01m) Issue("warning", "pek_floor", $"PEK ({M(s.SgkBase)}) asgari ücretin altında ({M(floor)})");
            if (s.SgkBase > ceiling + 0.01m) Issue("warning", "pek_ceiling", $"PEK ({M(s.SgkBase)}) SGK tavanını aşıyor ({M(ceiling)})");
            leaveDays.TryGetValue(s.EmployeeId, out var ld);
            var reason = MissingReason(s.UnpaidDays, ld, settings);
            if (s.UnpaidDays > 0 && reason == "13") Issue("warning", "missing_reason", "Eksik gün var ama izin türünden neden bulunamadı; 13 (diğer nedenler) yazıldı");
            if (person.Sgdp) Issue("warning", "sgdp", "SGDP kapsamında (emekli çalışan): bordro SGDP oranlarıyla hesaplanmıyor; prim tutarlarını kontrol edin");
            if (!ok) continue;
            var docType = person.Sgdp ? "02" : string.IsNullOrWhiteSpace(person.DocumentType) ? settings.DefaultDocumentType : person.DocumentType!;
            var law = string.IsNullOrWhiteSpace(person.LawNo) ? settings.DefaultLawNo : person.LawNo!;
            // PEK = hak edilen ücret + prim/ikramiye: ücret kısmı dönem ücreti + fazla mesai (PEK'i aşamaz), kalan prim/ikramiye.
            var wage = Math.Min(s.SgkBase, s.BaseGross + s.OvertimePay);
            var bonus = s.SgkBase - wage;
            var entry = person.HireDate >= start && person.HireDate <= end ? person.HireDate.Day.ToString("00", Inv) : null;
            var exit = person.TerminationDate is { } t && t >= start && t <= end ? t.Day.ToString("00", Inv) : null;
            string? exitCode = null;
            if (exit is not null)
            {
                exitCode = person.ExitReason is not null && settings.ExitReasonCodes.TryGetValue(person.ExitReason, out var c) ? c : null;
                if (exitCode is null) Issue("warning", "exit_reason", "İşten çıkış nedeni kodu bulunamadı (offboarding kaydı yok ya da eşlenmemiş)");
            }
            rows.Add(new(s.EmployeeId, name, person.NationalId, docType.PadLeft(2, '0'), law.PadLeft(5, '0'), s.PaidDays, s.UnpaidDays, reason,
                wage, bonus, s.SgkBase, entry, exit, exitCode, person.OccupationCode?.Trim()));
        }
        return (rows, issues);
    }

    /// <summary>XML aktarım dosyası: belge türü + kanun başına bir BELGE, içinde SIGORTALI satırları.</summary>
    public static byte[] Xml(PayrollPeriod period, IReadOnlyList<AphbRow> rows, IReadOnlyDictionary<Guid, AphbPerson> people, string employerTitle, SgkSettings s)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            w.WriteStartDocument();
            w.WriteComment(" HR360 - Aylik Prim ve Hizmet Belgesi aktarim dosyasi. Yerlesim kamuya acik kaynaklardan derlenmistir; e-Bildirge'ye yuklemeden once guncel semayla kontrol edin. ");
            w.WriteStartElement("AYLIKPRIMHIZMETBELGESI");
            w.WriteAttributeString("yil", period.Year.ToString(Inv));
            w.WriteAttributeString("ay", period.Month.ToString("00", Inv));
            w.WriteAttributeString("belgeTuru", s.DefaultDocumentType);
            w.WriteAttributeString("kanunNo", s.DefaultLawNo);
            w.WriteAttributeString("isveren", employerTitle);
            if (!string.IsNullOrWhiteSpace(s.WorkplaceRegistryNo)) w.WriteAttributeString("isyeriSicil", s.WorkplaceRegistryNo.Trim());
            foreach (var g in rows.GroupBy(r => (r.DocumentType, r.LawNo)).OrderBy(g => g.Key.DocumentType).ThenBy(g => g.Key.LawNo))
            {
                w.WriteStartElement("BELGE");
                w.WriteAttributeString("belgeTuru", g.Key.DocumentType);
                w.WriteAttributeString("kanunNo", g.Key.LawNo);
                w.WriteAttributeString("sigortaliSayisi", g.Count().ToString(Inv));
                w.WriteAttributeString("toplamPek", M(g.Sum(r => r.Pek)));
                w.WriteAttributeString("toplamGun", g.Sum(r => r.PrimDays).ToString(Inv));
                foreach (var r in g)
                {
                    var p = people[r.EmployeeId];
                    w.WriteStartElement("SIGORTALI");
                    w.WriteElementString("TCKIMLIKNO", r.NationalId);
                    w.WriteElementString("AD", Exporters.Upper(p.FirstName));
                    w.WriteElementString("SOYAD", Exporters.Upper(p.LastName));
                    w.WriteElementString("PRIMGUN", r.PrimDays.ToString(Inv));
                    w.WriteElementString("HAKEDILENUCRET", M(r.Wage));
                    w.WriteElementString("PRIMIKRAMIYE", M(r.Bonus));
                    w.WriteElementString("PRIMEESASKAZANC", M(r.Pek));
                    w.WriteElementString("EKSIKGUNSAYISI", r.MissingDays.ToString(Inv));
                    w.WriteElementString("EKSIKGUNNEDENI", r.MissingReason);
                    w.WriteElementString("ISEGIRISGUN", r.EntryDay ?? "");
                    w.WriteElementString("ISTENCIKISGUN", r.ExitDay ?? "");
                    w.WriteElementString("ISTENCIKISNEDENI", r.ExitReasonCode ?? "");
                    w.WriteElementString("MESLEKKODU", r.OccupationCode ?? "");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return Encoding.UTF8.GetBytes(sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\""));
    }

    /// <summary>TXT sütun sırası (noktalı virgülle ayrılmış; başlık satırı yok). Belgede: docs/bordro/README.md.</summary>
    public static readonly string[] TxtColumns =
    {
        "BelgeTuru", "KanunNo", "TCKimlikNo", "Ad", "Soyad", "PrimGun", "HakEdilenUcret", "PrimIkramiye", "EksikGunSayisi",
        "EksikGunNedeni", "IseGirisGun", "IstenCikisGun", "IstenCikisNedeni", "MeslekKodu",
    };

    /// <summary>TXT aktarım dosyası (Windows-1254 değil UTF-8; tutarlar virgüllü ondalık).</summary>
    public static byte[] Txt(IReadOnlyList<AphbRow> rows, IReadOnlyDictionary<Guid, AphbPerson> people)
    {
        static string Tr(decimal v) => v.ToString("0.00", Inv).Replace('.', ',');
        var sb = new StringBuilder();
        foreach (var r in rows.OrderBy(r => r.DocumentType).ThenBy(r => r.LawNo))
        {
            var p = people[r.EmployeeId];
            sb.Append(string.Join(';', new[]
            {
                r.DocumentType, r.LawNo, r.NationalId ?? "", Exporters.Upper(p.FirstName).Replace(";", " "), Exporters.Upper(p.LastName).Replace(";", " "),
                r.PrimDays.ToString(Inv), Tr(r.Wage), Tr(r.Bonus), r.MissingDays.ToString(Inv), r.MissingReason, r.EntryDay ?? "", r.ExitDay ?? "",
                r.ExitReasonCode ?? "", r.OccupationCode ?? "",
            })).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
