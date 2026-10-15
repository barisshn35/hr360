using System.Text;
using System.Text.Json;
using CompensationService.Models;
using CompensationService.Payroll;
using Xunit;

namespace Compensation.Tests;

/// <summary>Bordro dalgası 8 (madde 58–65): parametre seçimi, APHB, IBAN/banka, kıdem/ihbar, fark bordrosu, fiş dengesi, e-bordro, compa-ratio.</summary>
public class PayrollTrTests
{
    static PayrollParameterSet Row(int year, int from, PayrollParams p, decimal? h1 = null, decimal? h2 = null) => new()
    {
        Year = year, ValidFromMonth = from, MinimumWageGross = p.MinimumWageGross, SgkEmployeeRate = p.SgkEmployeeRate,
        UnemploymentEmployeeRate = p.UnemploymentEmployeeRate, SgkEmployerRate = p.SgkEmployerRate, EmployerIncentivePoints = p.EmployerIncentivePoints,
        UnemploymentEmployerRate = p.UnemploymentEmployerRate, StampTaxRate = p.StampTaxRate, SgkCeilingMultiplier = p.SgkCeilingMultiplier,
        BracketsJson = JsonSerializer.Serialize(p.Brackets, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        SeveranceCeilingH1 = h1, SeveranceCeilingH2 = h2,
    };

    static Payslip Slip(PayrollResult r, PayrollInput i, Guid emp, Guid period, int year) => new()
    {
        EmployeeId = emp, PeriodId = period, Year = year, Month = i.Month, MonthlyBaseGross = i.MonthlyBaseGross, PaidDays = r.PaidDays,
        UnpaidDays = 30 - r.PaidDays, OvertimeHours = i.OvertimeHours, BaseGross = r.BaseGross, OvertimePay = r.OvertimePay, Additions = r.Additions,
        Gross = r.Gross, SgkBase = r.SgkBase, SgkEmployee = r.SgkEmployee, UnemploymentEmployee = r.UnemploymentEmployee, TaxBase = r.TaxBase,
        CumulativeTaxBase = r.CumulativeTaxBase, IncomeTax = r.IncomeTax, IncomeTaxExemption = r.IncomeTaxExemption, StampTax = r.StampTax,
        StampTaxExemption = r.StampTaxExemption, Deductions = r.Deductions, Net = r.Net, SgkEmployer = r.SgkEmployer,
        UnemploymentEmployer = r.UnemploymentEmployer, EmployerCost = r.EmployerCost,
    };

    /* ------------------------------------------------------------ 60 parametreler (kullanıcı girer) */

    [Theory]
    [InlineData(2025, 1, 26005.50)]
    [InlineData(2025, 9, 120_000)]
    [InlineData(2026, 1, 33030)]
    [InlineData(2026, 7, 400_000)]
    [InlineData(2026, 12, 75_000)]
    public void Satirdan_kurulan_parametreler_ayni_sonucu_verir(int year, int month, double gross)
    {
        var d = PayrollDefaults.For(year);
        var fromRows = PayrollParameterResolver.Resolve(new[] { Row(year, 1, d) }, year, month);
        Assert.NotNull(fromRows);
        var input = new PayrollInput(month, (decimal)gross, 3, 4, 1000, 250, 50_000);
        Assert.Equal(PayrollCalculator.Calculate(d, input), PayrollCalculator.Calculate(fromRows!, input));
        Assert.Equal(d.Brackets, fromRows!.Brackets);
        Assert.Equal(PayrollStatutory.OvertimeMultiplier, fromRows.OvertimeMultiplier);
        Assert.Equal(PayrollStatutory.MonthlyHours, fromRows.MonthlyHours);
    }

    [Fact]
    public void Satir_yoksa_parametre_yok_koddan_varsayilan_donmez()
    {
        Assert.Null(PayrollParameterResolver.Resolve(Array.Empty<PayrollParameterSet>(), 2026, 3));
        // Başka yılın satırı kullanılmaz.
        Assert.Null(PayrollParameterResolver.Resolve(new[] { Row(2025, 1, PayrollDefaults.For(2025)) }, 2026, 1));
        // Yıl içinde yalnızca Temmuz satırı varsa Ocak–Haziran eksiktir.
        var july = new[] { Row(2026, 7, PayrollDefaults.For(2026)) };
        Assert.Null(PayrollParameterResolver.Resolve(july, 2026, 6));
        Assert.NotNull(PayrollParameterResolver.Resolve(july, 2026, 7));
        // Dilimleri boş/bozuk satır eksik sayılır.
        var broken = Row(2026, 1, PayrollDefaults.For(2026));
        broken.BracketsJson = "[]";
        Assert.Null(PayrollParameterResolver.Resolve(new[] { broken }, 2026, 1));
        broken.BracketsJson = "{bozuk";
        Assert.Null(PayrollParameterResolver.Resolve(new[] { broken }, 2026, 1));
    }

    [Fact]
    public void Eksik_parametre_409_govdesi_yil_ve_ipucu_verir()
    {
        var json = JsonSerializer.Serialize(PayrollParameterResolver.Missing(2027, 3));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("payroll_parameters_missing", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(2027, doc.RootElement.GetProperty("year").GetInt32());
        var msg = doc.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("2027", msg);
        Assert.Contains("Bordro › Parametreler", msg);
        using var sev = JsonDocument.Parse(JsonSerializer.Serialize(PayrollParameterResolver.SeveranceCeilingMissing(new DateOnly(2027, 8, 1))));
        Assert.Contains("Temmuz–Aralık", sev.RootElement.GetProperty("message").GetString());
        Assert.Equal("severanceCeiling", sev.RootElement.GetProperty("missing").GetString());
    }

    [Fact]
    public void Yariyil_satiri_temmuzdan_itibaren_gecerli()
    {
        var d = PayrollDefaults.For(2026);
        var rows = new[] { Row(2026, 1, d), Row(2026, 7, d with { MinimumWageGross = 36_000m }) };
        Assert.Equal(33030m, PayrollParameterResolver.Resolve(rows, 2026, 6)!.MinimumWageGross);
        Assert.Equal(36_000m, PayrollParameterResolver.Resolve(rows, 2026, 7)!.MinimumWageGross);
        Assert.Equal(36_000m, PayrollParameterResolver.Resolve(rows, 2026, 12)!.MinimumWageGross);
    }

    [Fact]
    public void Kidem_tavani_yariyila_gore_yoksa_null()
    {
        var d = PayrollDefaults.For(2025);
        var rows = new[] { Row(2025, 1, d, 46_655.43m, 53_919.68m) };
        Assert.Equal(46_655.43m, PayrollParameterResolver.SeveranceCeilingOrNull(rows, new DateOnly(2025, 6, 30)));
        Assert.Equal(53_919.68m, PayrollParameterResolver.SeveranceCeilingOrNull(rows, new DateOnly(2025, 7, 1)));
        Assert.Null(PayrollParameterResolver.SeveranceCeilingOrNull(rows, new DateOnly(2024, 7, 1)));
        // H2 girilmemişse H1'e düşülmez (tavan Temmuz'da değişir).
        Assert.Null(PayrollParameterResolver.SeveranceCeilingOrNull(new[] { Row(2025, 1, d, 46_655.43m) }, new DateOnly(2025, 9, 1)));
        // Temmuz satırında tavan yoksa Ocak satırındaki H2 kullanılır.
        var two = new[] { Row(2025, 1, d, 46_655.43m, 53_919.68m), Row(2025, 7, d with { MinimumWageGross = 30_000m }) };
        Assert.Equal(53_919.68m, PayrollParameterResolver.SeveranceCeilingOrNull(two, new DateOnly(2025, 9, 1)));
    }

    [Fact]
    public void Asgari_ucret_istisnasi_kapatilinca_vergi_tam_kesilir()
    {
        var p = PayrollDefaults.For(2026) with { MinimumWageExemption = false };
        var r = PayrollCalculator.Calculate(p, new PayrollInput(1, 33030m, 0, 0, 0, 0, 0));
        Assert.Equal(0, r.IncomeTaxExemption);
        Assert.Equal(0, r.StampTaxExemption);
        Assert.True(r.Net < 28075.50m);
    }

    static List<string> Fields(PayrollParameterInput i, int year = 2026) =>
        PayrollParameterResolver.ValidateInput(year, i).Select(e => e.Field).ToList();

    [Fact]
    public void Parametre_dogrulamasi_gecerli_girdi()
    {
        Assert.Empty(PayrollParameterResolver.ValidateInput(2026, PayrollDefaults.Input(2026, 7)));
        // Fixture'dan kurulan parametreler aynı sonucu verir.
        var p = PayrollParameterResolver.FromInput(2026, PayrollDefaults.Input(2026));
        var i = new PayrollInput(1, 50_000m, 0, 0, 0, 0, 0);
        Assert.Equal(PayrollCalculator.Calculate(PayrollDefaults.For(2026), i), PayrollCalculator.Calculate(p, i));
    }

    [Fact]
    public void Parametre_dogrulamasi_zorunlu_alanlar_koddan_doldurulmaz()
    {
        var f = Fields(new PayrollParameterInput());
        foreach (var field in new[] { "minimumWageGross", "sgkEmployeeRate", "unemploymentEmployeeRate", "sgkEmployerRate",
                     "unemploymentEmployerRate", "stampTaxRate", "employerIncentivePoints", "sgkCeilingMultiplier", "brackets" })
            Assert.Contains(field, f);
        // İsteğe bağlı alanlar boş kalabilir.
        Assert.Empty(PayrollParameterResolver.ValidateInput(2026, PayrollDefaults.Input(2026, h1: null, h2: null)));
    }

    [Fact]
    public void Parametre_dogrulamasi_aralik_ve_tutarlilik_kurallari()
    {
        var ok = PayrollDefaults.Input(2026);
        Assert.Contains("validFromMonth", Fields(ok with { ValidFromMonth = 13 }));
        Assert.Contains("year", Fields(ok, 2019));
        Assert.Contains("sgkEmployeeRate", Fields(ok with { SgkEmployeeRate = 14m }));            // yüzde değil kesir
        Assert.Contains("sgkEmployerRate", Fields(ok with { SgkEmployerRate = -0.01m }));
        Assert.Contains("unemploymentEmployeeRate", Fields(ok with { UnemploymentEmployeeRate = 0.2m }));
        Assert.Contains("stampTaxRate", Fields(ok with { StampTaxRate = 0.5m }));
        Assert.Contains("employerIncentivePoints", Fields(ok with { EmployerIncentivePoints = 25m }));
        Assert.Contains("employerIncentivePoints", Fields(ok with { SgkEmployerRate = 0.01m, EmployerIncentivePoints = 2m })); // teşvik > oran
        Assert.Contains("sgkCeilingMultiplier", Fields(ok with { SgkCeilingMultiplier = 0.9m }));  // tavan < taban
        Assert.Contains("minimumWageGross", Fields(ok with { MinimumWageGross = 0 }));
        Assert.Contains("minimumWageNet", Fields(ok with { MinimumWageNet = 40_000m }));         // net > brüt
        Assert.Empty(Fields(ok with { MinimumWageNet = 28_075.50m }));
        Assert.Contains("severanceCeilingH1", Fields(ok with { SeveranceCeilingH1 = 0 }));
        Assert.Contains("source", Fields(ok with { Source = new string('x', 301) }));
    }

    [Fact]
    public void Parametre_dogrulamasi_vergi_dilimleri()
    {
        var ok = PayrollDefaults.Input(2026);
        TaxBracket Br(decimal? up, decimal r) => new(up, r);
        Assert.Contains("brackets", Fields(ok with { Brackets = new() }));
        Assert.Contains("brackets", Fields(ok with { Brackets = new() { Br(100, 0.15m), Br(50, 0.2m), Br(null, 0.4m) } }));     // artan değil
        Assert.Contains("brackets", Fields(ok with { Brackets = new() { Br(100, 0.15m), Br(200, 0.2m) } }));                  // son dilim sınırlı
        Assert.Contains("brackets", Fields(ok with { Brackets = new() { Br(null, 0.15m), Br(null, 0.2m) } }));                // ara dilim sınırsız
        Assert.Contains("brackets", Fields(ok with { Brackets = new() { Br(100, 0.15m), Br(null, 1.5m) } }));                 // oran > %100
        Assert.Contains("brackets", Fields(ok with { Brackets = new() { Br(100, 0.3m), Br(null, 0.2m) } }));                  // oran azalıyor
        Assert.Empty(Fields(ok with { Brackets = new() { Br(null, 0.2m) } }));                                               // tek dilim geçerli
    }

    /* ------------------------------------------------------------ 58 APHB */

    static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    static (PayrollPeriod, List<Payslip>, Dictionary<Guid, AphbPerson>) AphbSample()
    {
        var p = PayrollDefaults.For(2026);
        var period = new PayrollPeriod { Year = 2026, Month = 3 };
        var ia = new PayrollInput(3, 60_000m, 5, 2, 3_000, 0, 0);
        var ib = new PayrollInput(3, 33_030m, 0, 0, 0, 0, 0);
        var ic = new PayrollInput(3, 50_000m, 0, 0, 0, 0, 0);
        var slips = new List<Payslip>
        {
            Slip(PayrollCalculator.Calculate(p, ia), ia, A, period.Id, 2026),
            Slip(PayrollCalculator.Calculate(p, ib), ib, B, period.Id, 2026),
            Slip(PayrollCalculator.Calculate(p, ic), ic, C, period.Id, 2026),
        };
        var people = new Dictionary<Guid, AphbPerson>
        {
            [A] = new(A, "Ayşe", "Yılmaz", "10000000146", new DateOnly(2026, 3, 10), null, null, "2512.01", null, null, false),
            [B] = new(B, "İsmail", "Çelik", "10000000146", new DateOnly(2020, 1, 1), new DateOnly(2026, 3, 20), "Resignation", null, null, "05746", true),
            [C] = new(C, "Can", "Öz", "12345", new DateOnly(2020, 1, 1), null, null, "1120.03", null, null, false),
        };
        return (period, slips, people);
    }

    [Fact]
    public void Aphb_satirlari_ve_dogrulama_bulgulari()
    {
        var (period, slips, people) = AphbSample();
        var leave = new Dictionary<Guid, Dictionary<string, decimal>> { [A] = new() { ["Unpaid"] = 5 } };
        var (rows, issues) = Aphb.Build(period, slips, people, leave, new SgkSettings(), PayrollDefaults.For(2026));
        Assert.Equal(2, rows.Count);                                           // C: geçersiz TCKN, dosyaya girmez
        Assert.Contains(issues, i => i.EmployeeId == C && i.Level == "error" && i.Code == "tckn");
        Assert.Contains(issues, i => i.EmployeeId == B && i.Code == "occupation");
        Assert.Contains(issues, i => i.EmployeeId == B && i.Code == "sgdp");
        var a = rows.Single(r => r.EmployeeId == A);
        Assert.Equal(25, a.PrimDays);
        Assert.Equal(5, a.MissingDays);
        Assert.Equal("21", a.MissingReason);                                   // ücretsiz izin → 21
        Assert.Equal("10", a.EntryDay);
        Assert.Equal(a.Pek, a.Wage + a.Bonus);
        Assert.Equal("01", a.DocumentType);
        var b = rows.Single(r => r.EmployeeId == B);
        Assert.Equal("02", b.DocumentType);                                    // SGDP
        Assert.Equal("05746", b.LawNo);
        Assert.Equal("20", b.ExitDay);
        Assert.Equal("03", b.ExitReasonCode);                                  // istifa
    }

    [Fact]
    public void Aphb_pek_tavan_ve_gun_uyarilari()
    {
        var p = PayrollDefaults.For(2026);
        var period = new PayrollPeriod { Year = 2026, Month = 3 };
        var slip = new Payslip { EmployeeId = A, PaidDays = 31, UnpaidDays = 0, SgkBase = 400_000m, BaseGross = 400_000m, Year = 2026, Month = 3 };
        var low = new Payslip { EmployeeId = B, PaidDays = 30, UnpaidDays = 0, SgkBase = 1_000m, BaseGross = 1_000m, Year = 2026, Month = 3 };
        var people = new Dictionary<Guid, AphbPerson>
        {
            [A] = new(A, "A", "B", "10000000146", new DateOnly(2020, 1, 1), null, null, "2512.01", null, null, false),
            [B] = new(B, "C", "D", "10000000146", new DateOnly(2020, 1, 1), null, null, "2512.01", null, null, false),
        };
        var (rows, issues) = Aphb.Build(period, new[] { slip, low }, people, new Dictionary<Guid, Dictionary<string, decimal>>(), new SgkSettings(), p);
        Assert.Contains(issues, i => i.EmployeeId == A && i.Code == "days" && i.Level == "error");
        Assert.Contains(issues, i => i.EmployeeId == B && i.Code == "pek_floor");
        Assert.Single(rows);
        var (_, issues2) = Aphb.Build(period, new[] { new Payslip { EmployeeId = A, PaidDays = 30, SgkBase = 400_000m, BaseGross = 400_000m } }, people, new Dictionary<Guid, Dictionary<string, decimal>>(), new SgkSettings(), p);
        Assert.Contains(issues2, i => i.Code == "pek_ceiling");
    }

    [Theory]
    [InlineData(0, "", "")]
    [InlineData(3, "Unpaid", "21")]
    [InlineData(3, "Annual", "13")]
    public void Eksik_gun_nedeni(int days, string type, string expected)
    {
        var map = type == "" ? null : new Dictionary<string, decimal> { [type] = days };
        Assert.Equal(expected, Aphb.MissingReason(days, map, new SgkSettings()));
    }

    [Fact]
    public void Birden_fazla_eksik_gun_nedeni_12()
    {
        var s = new SgkSettings { MissingDayCodes = new() { new("Unpaid", "21", true), new("Sick", "01", true) } };
        Assert.Equal("12", Aphb.MissingReason(5, new Dictionary<string, decimal> { ["Unpaid"] = 2, ["Sick"] = 3 }, s));
        Assert.Equal(new[] { "Unpaid", "Sick" }, s.PayReducingLeaveTypes());
        Assert.Equal(new[] { "Unpaid" }, new SgkSettings().PayReducingLeaveTypes());   // varsayılan = önceki davranış
    }

    [Fact]
    public void Aphb_xml_ve_txt_yerlesimi()
    {
        var (period, slips, people) = AphbSample();
        var (rows, _) = Aphb.Build(period, slips, people, new Dictionary<Guid, Dictionary<string, decimal>>(), new SgkSettings { WorkplaceRegistryNo = "1234" }, PayrollDefaults.For(2026));
        var xml = Encoding.UTF8.GetString(Aphb.Xml(period, rows, people, "Demo A.Ş.", new SgkSettings { WorkplaceRegistryNo = "1234" }));
        Assert.Contains("<AYLIKPRIMHIZMETBELGESI", xml);
        Assert.Contains("encoding=\"UTF-8\"", xml);
        Assert.Contains("<TCKIMLIKNO>10000000146</TCKIMLIKNO>", xml);
        Assert.Contains("<AD>İSMAİL</AD>", xml);                              // Türkçe büyük harf (İ)
        Assert.Contains("<MESLEKKODU>2512.01</MESLEKKODU>", xml);
        Assert.Contains("belgeTuru=\"02\" kanunNo=\"05746\"", xml);
        Assert.Equal(2, xml.Split("<BELGE ").Length - 1);
        var txt = Encoding.UTF8.GetString(Aphb.Txt(rows, people)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, txt.Length);
        Assert.All(txt, l => Assert.Equal(Aphb.TxtColumns.Length, l.Split(';').Length));
        Assert.StartsWith("01;05510;10000000146;AYŞE;YILMAZ;25;", txt[0]);
    }

    /* ------------------------------------------------------------ 62 IBAN ve banka dosyası */

    [Theory]
    [InlineData("TR330006100519786457841326", true)]
    [InlineData("TR33 0006 1005 1978 6457 8413 26", true)]
    [InlineData("TR330006100519786457841327", false)]                      // kontrol hanesi bozuk
    [InlineData("TR3300061005197864578413", false)]                         // TR 26 karakter olmalı
    [InlineData("DE89370400440532013000", false)]                           // maaş dosyası yalnızca TR IBAN
    [InlineData("", false)]
    [InlineData("XX00", false)]
    public void Iban_mod97(string iban, bool ok) => Assert.Equal(ok, Exporters.ValidIban(iban));

    [Theory]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("GB82WEST12345698765433", false)]
    [InlineData("TR330006100519786457841326", true)]
    public void Iban_her_ulke_mod97(string iban, bool ok) => Assert.Equal(ok, Exporters.ValidIbanAny(iban));

    static (PayrollPeriod, List<Payslip>, Dictionary<Guid, ExportPerson>) BankSample()
    {
        var period = new PayrollPeriod { Year = 2026, Month = 4 };
        var slips = new List<Payslip>
        {
            new() { EmployeeId = A, Net = 28_075.50m, Currency = "TRY" },
            new() { EmployeeId = B, Net = 41_234.56m, Currency = "TRY" },
            new() { EmployeeId = C, Net = 10m, Currency = "TRY" },
        };
        var people = new Dictionary<Guid, ExportPerson>
        {
            [A] = new(A, "Ayşe", "Şahin", "10000000146", "TR330006100519786457841326", "Mühendislik", new DateOnly(2020, 1, 1), null),
            [B] = new(B, "Çağrı", "Öğüt", null, "TR33 0006 1005 1978 6457 8413 26", "Satış", new DateOnly(2020, 1, 1), null),
            [C] = new(C, "Can", "Er", null, "TR000000", "Satış", new DateOnly(2020, 1, 1), null),
        };
        return (period, slips, people);
    }

    [Fact]
    public void Banka_ornek_a_toplam_satiri_ve_ascii()
    {
        var (period, slips, people) = BankSample();
        var r = BankFiles.Build(period, slips, people, new BankSettings { Template = "ornek-a" }, new DateOnly(2026, 5, 1));
        var text = Encoding.Latin1.GetString(r.Content);
        Assert.Equal(2, r.Rows);
        Assert.Single(r.Warnings);                                              // C: geçersiz IBAN
        Assert.Contains("CAGRI OGUT", text);
        Assert.Contains("TOPLAM;2;;69310,06;TRY;;", text);
        Assert.Contains("01.05.2026", text);
    }

    [Fact]
    public void Banka_ornek_b_sabit_uzunluk()
    {
        var (period, slips, people) = BankSample();
        var r = BankFiles.Build(period, slips, people, new BankSettings { Template = "ornek-b", CompanyCode = "HR360", DebitIban = "TR330006100519786457841326" }, new DateOnly(2026, 5, 1));
        var lines = Encoding.Latin1.GetString(r.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal(66, lines[0].Length);
        Assert.All(lines.Skip(1).Take(2), l => Assert.Equal(121, l.Length));
        Assert.Equal("T000002000000006931006", lines[3]);                      // 2 kayıt, 69.310,06 TL = 6931006 kuruş
    }

    [Fact]
    public void Banka_ozel_duzen_virgullu_ondalik()
    {
        var (period, slips, people) = BankSample();
        var b = new BankSettings { Template = "custom", Custom = new BankCustomLayout { Delimiter = "|", DecimalSeparator = ",", Columns = new() { "seq", "iban", "amount", "name" }, Encoding = "utf-8", Ascii = false } };
        var text = Encoding.UTF8.GetString(BankFiles.Build(period, slips, people, b, new DateOnly(2026, 5, 1)).Content);
        Assert.StartsWith("SEQ|IBAN|AMOUNT|NAME\r\n", text);
        Assert.Contains("|TR330006100519786457841326|28075,50|AYŞE ŞAHİN\r\n", text);
        Assert.Contains("TOPLAM|", text);
        Assert.Contains("|69310,06|", text);
        Assert.Null(PayrollSettingsModel.Validate(null, null, null, b));
        Assert.NotNull(PayrollSettingsModel.Validate(null, null, null, b with { Custom = b.Custom with { Columns = new() { "name", "amount" } } }));
    }

    /* ------------------------------------------------------------ 61 kıdem ve ihbar */

    [Fact]
    public void Kidem_ve_ihbar_bir_yil_isveren_feshi()
    {
        var p = PayrollDefaults.For(2025);
        var r = SeveranceCalculator.Compute(new SeveranceInput(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), "Termination", 50_000m, 0, 0,
            53_919.68m, null, null, 0, 0, p.Brackets, p.StampTaxRate));
        Assert.Equal(365, r.TenureDays);
        Assert.True(r.SeveranceEligible);
        Assert.Equal(50_000m, r.SeveranceGross);
        Assert.Equal(379.50m, r.SeveranceStampTax);
        Assert.Equal(4, r.NoticeWeeks);
        Assert.Equal(46_666.67m, r.NoticeGross);
        Assert.Equal(7_000.00m, r.NoticeIncomeTax);
        Assert.Equal(354.20m, r.NoticeStampTax);
        Assert.Equal(49_620.50m + 39_312.47m, r.TotalNet);
    }

    [Fact]
    public void Kidem_tavani_giydirilmis_ucrete_uygulanir_ihbara_uygulanmaz()
    {
        var p = PayrollDefaults.For(2026);
        var r = SeveranceCalculator.Compute(new SeveranceInput(new DateOnly(2016, 1, 1), new DateOnly(2025, 12, 31), "Termination", 100_000m, 10_000m, 5_000m,
            64_948.77m, null, null, 10, 0, p.Brackets, p.StampTaxRate));
        Assert.Equal(115_000m, r.DressedMonthlyGross);
        Assert.True(r.CeilingApplied);
        Assert.Equal(64_948.77m, r.SeveranceBasis);
        Assert.Equal(Math.Round(64_948.77m * r.TenureDays / 365m, 2, MidpointRounding.AwayFromZero), r.SeveranceGross);
        Assert.Equal(8, r.NoticeWeeks);
        Assert.Equal(Math.Round(115_000m / 30m * 56, 2, MidpointRounding.AwayFromZero), r.NoticeGross);
        Assert.Equal(Math.Round(100_000m / 30m * 10, 2, MidpointRounding.AwayFromZero), r.UnusedLeaveGross);
    }

    [Theory]
    [InlineData(100, 2)]
    [InlineData(200, 4)]
    [InlineData(600, 6)]
    [InlineData(1095, 6)]   // tam 3 yıl: "3 yıldan fazla" değil
    [InlineData(1096, 8)]
    [InlineData(1200, 8)]
    public void Ihbar_haftalari(int days, int weeks) => Assert.Equal(weeks, SeveranceCalculator.NoticeWeeks(days));

    [Theory]
    [InlineData("2025-01-01", "2025-06-29", 2)]   // 6 aydan az
    [InlineData("2025-01-01", "2025-06-30", 4)]   // tam 6 ay: "altı aydan bir buçuk yıla kadar"
    [InlineData("2025-01-01", "2026-06-30", 4)]   // tam 1,5 yıl (dahil)
    [InlineData("2025-01-01", "2026-07-01", 6)]
    [InlineData("2023-01-01", "2025-12-31", 6)]   // tam 3 yıl (artık yıl içerse de) = 6 hafta
    [InlineData("2023-01-01", "2026-01-01", 8)]   // 3 yıldan fazla
    [InlineData("2016-01-01", "2025-12-31", 8)]
    public void Ihbar_haftalari_takvime_gore_sinirlar(string hire, string last, int weeks) =>
        Assert.Equal(weeks, SeveranceCalculator.NoticeWeeks(DateOnly.Parse(hire), DateOnly.Parse(last)));

    [Fact]
    public void Tam_uc_yilda_ihbar_alti_hafta()
    {
        var p = PayrollDefaults.For(2025);
        var r = SeveranceCalculator.Compute(new SeveranceInput(new DateOnly(2023, 1, 1), new DateOnly(2025, 12, 31), "Termination", 60_000m, 0, 0,
            53_919.68m, null, null, 0, 0, p.Brackets, p.StampTaxRate));
        Assert.Equal(1096, r.TenureDays);
        Assert.Equal(6, r.NoticeWeeks);
        Assert.Equal(Math.Round(60_000m / 30m * 42, 2, MidpointRounding.AwayFromZero), r.NoticeGross);
    }

    [Fact]
    public void Ihbar_GV_matrahi_yalnizca_kapanmis_ve_ayrilis_ayina_kadar_donemler()
    {
        var slips = new[]
        {
            new SeveranceCalculator.TaxBaseSlip(2026, 1, 40_000m, true),
            new SeveranceCalculator.TaxBaseSlip(2026, 2, 40_000m, true),
            new SeveranceCalculator.TaxBaseSlip(2026, 3, 40_000m, false),  // hesaplanmış ama kapanmamış: sayılmaz
            new SeveranceCalculator.TaxBaseSlip(2026, 5, 40_000m, true),   // ayrılıştan sonraki ay: sayılmaz
            new SeveranceCalculator.TaxBaseSlip(2025, 12, 99_000m, true),  // önceki yıl: sayılmaz
        };
        Assert.Equal(80_000m, SeveranceCalculator.PriorTaxBase(slips, new DateOnly(2026, 4, 15), 0));
        // Ayrılış ayının pusulası yalnızca kapanmışsa
        var withExitMonth = slips.Append(new SeveranceCalculator.TaxBaseSlip(2026, 4, 20_000m, true));
        Assert.Equal(100_000m, SeveranceCalculator.PriorTaxBase(withExitMonth, new DateOnly(2026, 4, 15), 0));
        // Devreden matrah eklenir, negatifi yok sayılır
        Assert.Equal(330_000m, SeveranceCalculator.PriorTaxBase(slips, new DateOnly(2026, 4, 15), 250_000m));
        Assert.Equal(80_000m, SeveranceCalculator.PriorTaxBase(slips, new DateOnly(2026, 4, 15), -5m));
    }

    [Fact]
    public void Kullanilmayan_izin_devreden_yillar_dahil_cift_sayim_yok()
    {
        var rows = new[]
        {
            new SeveranceCalculator.AnnualLeaveRow(2024, 14m, 10m, 0m),    // 4 gün devredilmemiş kalan: sayılır
            new SeveranceCalculator.AnnualLeaveRow(2025, 14m, 6m, 8m),     // 8 gün 2026'ya devretti: kalan 0
            new SeveranceCalculator.AnnualLeaveRow(2026, 22m, 3.5m, 0m),   // 14 + 8 devreden − 3,5 = 18,5
            new SeveranceCalculator.AnnualLeaveRow(2027, 14m, 0m, 0m),     // ayrılıştan sonraki yıl: sayılmaz
        };
        Assert.Equal(22.5m, SeveranceCalculator.UnusedAnnualLeave(rows, 2026));
        Assert.Equal(0m, SeveranceCalculator.UnusedAnnualLeave(new[] { new SeveranceCalculator.AnnualLeaveRow(2026, 14m, 20m, 0m) }, 2026));
    }

    [Fact]
    public void Devreden_kumulatif_matrah_dilimleri_dogru_yerden_baslatir()
    {
        var p = PayrollDefaults.For(2026);
        Assert.Equal(500_000m, PayrollCalculator.PriorCumulativeBase(500_000m, 0));
        Assert.Equal(560_000m, PayrollCalculator.PriorCumulativeBase(500_000m, 60_000m));
        Assert.Equal(60_000m, PayrollCalculator.PriorCumulativeBase(-1m, 60_000m));
        var fresh = PayrollCalculator.Calculate(p, new PayrollInput(7, 100_000m, 0, 0, 0, 0, PayrollCalculator.PriorCumulativeBase(0, 0)));
        var carried = PayrollCalculator.Calculate(p, new PayrollInput(7, 100_000m, 0, 0, 0, 0, PayrollCalculator.PriorCumulativeBase(600_000m, 0)));
        // 600.000 devreden matrahla Temmuz ayı ilk dilimden (%15) değil üst dilimden vergilenir.
        Assert.True(carried.IncomeTax > fresh.IncomeTax);
        Assert.Equal(600_000m + carried.TaxBase, carried.CumulativeTaxBase);
    }

    [Fact]
    public void Istifada_kidem_ve_ihbar_yok_ama_IK_karari_degistirebilir()
    {
        var p = PayrollDefaults.For(2026);
        SeveranceInput I(bool? elig, bool? notice) => new(new DateOnly(2020, 1, 1), new DateOnly(2026, 3, 31), "Resignation", 60_000m, 0, 0, 64_948.77m,
            elig, notice, 0, 100_000m, p.Brackets, p.StampTaxRate);
        var r = SeveranceCalculator.Compute(I(null, null));
        Assert.False(r.SeveranceEligible);
        Assert.Equal(0, r.SeveranceGross);
        Assert.Equal(0, r.NoticeGross);
        var o = SeveranceCalculator.Compute(I(true, null));                   // ör. evlilik nedeniyle fesih: İK kararı
        Assert.True(o.SeveranceEligible);
        Assert.True(o.SeveranceGross > 0);
        Assert.Equal("49.620,50 TL", SeveranceCalculator.Variables(SeveranceCalculator.Compute(new SeveranceInput(new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31), "Termination", 50_000m, 0, 0, 53_919.68m, null, null, 0, 0, p.Brackets, 0.00759m)), new DateOnly(2025, 12, 31), "x")["kidem.net"]);
    }

    /* ------------------------------------------------------------ 63 fark bordrosu */

    [Fact]
    public void Fark_bordrosu_brut_farki_ve_odenen_fark_dusulur()
    {
        var p = PayrollDefaults.For(2026);
        var i = new PayrollInput(3, 50_000m, 0, 0, 0, 0, 100_000m);
        var old = Slip(PayrollCalculator.Calculate(p, i), i, A, Guid.NewGuid(), 2026);
        var c = RetroPay.Compute(p, old, 55_000m, 0)!;
        Assert.Equal(5_000m, c.DiffGross);
        Assert.Equal("Fark: 2026/03", c.Label);
        Assert.True(c.NewNet > c.OldNet);
        Assert.Equal(3_000m, RetroPay.Compute(p, old, 55_000m, 2_000m)!.DiffGross);
        Assert.Null(RetroPay.Compute(p, old, 55_000m, 5_000m));                // tamamı ödenmiş
        Assert.True(RetroPay.Compute(p, old, 45_000m, 0)!.DiffGross < 0);      // geriye dönük indirim: yalnızca gösterilir
    }

    [Fact]
    public void Fark_bordrosu_eksik_gun_ve_fazla_mesaiyi_korur()
    {
        var p = PayrollDefaults.For(2026);
        var i = new PayrollInput(5, 45_000m, 10, 6, 0, 0, 0);
        var old = Slip(PayrollCalculator.Calculate(p, i), i, A, Guid.NewGuid(), 2026);
        var c = RetroPay.Compute(p, old, 54_000m, 0)!;
        // 20 gün: 9.000 x 20/30 = 6.000 + fazla mesai farkı (9.000 / 225 x 1,5 x 6 = 360)
        Assert.Equal(6_360m, c.DiffGross);
    }

    [Fact]
    public void Donem_icin_gecerli_ucret_kaydi()
    {
        var recs = new[]
        {
            new CompensationRecord { EmployeeId = A, BaseSalary = 40_000, EffectiveFrom = new DateOnly(2025, 1, 1), EffectiveTo = new DateOnly(2026, 1, 31) },
            new CompensationRecord { EmployeeId = A, BaseSalary = 50_000, EffectiveFrom = new DateOnly(2026, 2, 1) },
        };
        Assert.Equal(40_000, RetroPay.RecordFor(recs, 2026, 1)!.BaseSalary);
        Assert.Equal(50_000, RetroPay.RecordFor(recs, 2026, 2)!.BaseSalary);
    }

    /* ------------------------------------------------------------ 64 muhasebe fişi */

    [Fact]
    public void Muhasebe_fisi_dengeli_ve_masraf_merkezi_eslenir()
    {
        var p = PayrollDefaults.For(2026);
        var period = new PayrollPeriod { Year = 2026, Month = 2 };
        var inputs = new[] { new PayrollInput(2, 87_654.32m, 3, 7, 1_234.56m, 999.99m, 80_000m), new PayrollInput(2, 33_030m, 0, 0, 0, 0, 0) };
        var slips = inputs.Select((x, k) => Slip(PayrollCalculator.Calculate(p, x), x, k == 0 ? A : B, period.Id, 2026)).ToList();
        var people = new Dictionary<Guid, ExportPerson>
        {
            [A] = new(A, "a", "b", null, null, "Mühendislik", new DateOnly(2020, 1, 1), null),
            [B] = new(B, "c", "d", null, null, "Satış", new DateOnly(2020, 1, 1), null),
        };
        var lines = Exporters.Journal(period, slips, people, new AccountMap(Salary: "770.10"), new Dictionary<string, string> { ["Mühendislik"] = "MM-100" });
        Assert.True(Exporters.JournalBalanced(lines));
        Assert.Equal(0, Exporters.JournalDifference(lines));
        Assert.Contains(lines, l => l.CostCenter == "MM-100" && l.Account == "770.10");
        Assert.Contains(lines, l => l.CostCenter == "Satış");
        var csv = Encoding.UTF8.GetString(Exporters.Accounting(period, slips, people, "mikro", new AccountMap()).Content);
        Assert.Contains(";B\n", csv);
        Assert.False(Exporters.JournalBalanced(lines.Append(new Exporters.JournalLine("1", "x", "y", 1m, 0, "z"))));
    }

    /* ------------------------------------------------------------ 59 e-bordro */

    [Fact]
    public void E_bordro_ozeti_kararli_ve_degisiklige_duyarli()
    {
        var s = new Payslip { Id = A, EmployeeId = B, Year = 2026, Month = 1, Net = 28_075.5m, Gross = 33_030m };
        var h1 = EPayslip.Hash(s);
        Assert.Equal(h1, EPayslip.Hash(new Payslip { Id = A, EmployeeId = B, Year = 2026, Month = 1, Net = 28_075.50m, Gross = 33_030.00m }));
        Assert.Equal(64, h1.Length);
        s.Net += 0.01m;
        Assert.NotEqual(h1, EPayslip.Hash(s));
        Assert.Contains("\"net\":\"28075.51\"", EPayslip.Canonical(s));
        Assert.Equal("10.1.2.0/24", EPayslip.IpPrefix("10.1.2.3"));
        Assert.Null(EPayslip.IpPrefix("x"));
        Assert.EndsWith("::/48", EPayslip.IpPrefix("2001:db8:1234:5678::1"));
    }

    /* ------------------------------------------------------------ 65 bantlar ve compa-ratio */

    [Fact]
    public void Bant_secimi_yururluk_tarihine_ve_para_birimine_gore()
    {
        var bands = new[]
        {
            new SalaryBand { Grade = "L3", Year = 2026, MinAmount = 40_000, MidAmount = 50_000, MaxAmount = 60_000, Currency = "TRY" },
            new SalaryBand { Grade = "L3", Year = 2026, EffectiveFrom = new DateOnly(2026, 7, 1), MinAmount = 45_000, MidAmount = 55_000, MaxAmount = 65_000, Currency = "TRY" },
            new SalaryBand { Grade = "L3", Year = 2026, MinAmount = 1, MidAmount = 2, MaxAmount = 3, Currency = "EUR" },
        };
        Assert.Equal(50_000, CompaRatio.BandFor(bands, "l3", "TRY", new DateOnly(2026, 6, 30))!.MidAmount);
        Assert.Equal(55_000, CompaRatio.BandFor(bands, "L3", "TRY", new DateOnly(2026, 7, 1))!.MidAmount);
        Assert.Null(CompaRatio.BandFor(bands, "L4", "TRY", new DateOnly(2026, 7, 1)));
        var b = bands[0];
        Assert.Equal(1.1m, CompaRatio.Ratio(55_000, b));
        Assert.Equal("below", CompaRatio.Position(39_000, b));
        Assert.Equal("within", CompaRatio.Position(60_000, b));
        Assert.Equal("above", CompaRatio.Position(60_001, b));
        Assert.Equal("none", CompaRatio.Position(1, null));
    }

    [Fact]
    public void Kapsama_raporu_bes_kisiden_kucuk_gruplari_gizler()
    {
        var items = Enumerable.Range(0, 6).Select(k => new CompaRatio.Item("L3 (2026)", "Mühendislik", 1.0m + k / 100m, k == 0 ? "below" : "within"))
            .Concat(Enumerable.Range(0, 3).Select(_ => new CompaRatio.Item("L4 (2026)", "Satış", 1.2m, "above")))
            .Append(new CompaRatio.Item(null, "Satış", null, "none")).ToList();
        var json = JsonSerializer.Serialize(CompaRatio.Coverage(items), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(10, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("noBand").GetInt32());
        Assert.Equal(4, root.GetProperty("outsideBand").GetInt32());
        var byBand = root.GetProperty("byBand").EnumerateArray().ToList();
        var l3 = byBand.Single(g => g.GetProperty("key").GetString() == "L3 (2026)");
        Assert.False(l3.GetProperty("hidden").GetBoolean());
        Assert.Equal(1, l3.GetProperty("below").GetInt32());
        var l4 = byBand.Single(g => g.GetProperty("key").GetString() == "L4 (2026)");
        Assert.True(l4.GetProperty("hidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, l4.GetProperty("above").ValueKind);
        Assert.Equal(JsonValueKind.Null, l4.GetProperty("avgCompaRatio").ValueKind);
    }

    /* ------------------------------------------------------------ ayarlar */

    [Fact]
    public void Ayarlar_bozuk_jsonda_varsayilana_doner_ve_dogrulanir()
    {
        var m = PayrollSettingsModel.From(new PayrollSettings { SgkJson = "{bozuk", AccountMapJson = "{\"salary\":\"770.99\"}", CostCentersJson = "{\"mühendislik\":\"MM1\"}" });
        Assert.Equal("01", m.Sgk.DefaultDocumentType);
        Assert.Equal("770.99", m.Accounts.Salary);
        Assert.Equal("335", m.Accounts.NetPayable);                             // verilmeyen hesap varsayılan
        Assert.Equal("MM1", m.CostCenters["Mühendislik"]);                      // büyük/küçük harf duyarsız
        Assert.Null(PayrollSettingsModel.Validate(new SgkSettings(), new AccountMap(), new(), new BankSettings()));
        Assert.NotNull(PayrollSettingsModel.Validate(new SgkSettings { DefaultLawNo = "55" }, null, null, null));
        Assert.NotNull(PayrollSettingsModel.Validate(new SgkSettings { MissingDayCodes = new() { new("Unpaid", "2a", true) } }, null, null, null));
        Assert.NotNull(PayrollSettingsModel.Validate(null, null, null, new BankSettings { DebitIban = "TR000" }));
    }
}
