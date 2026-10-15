using CompensationService.Payroll;
using Xunit;

namespace Compensation.Tests;

public class PayrollCalculatorTests
{
    static readonly PayrollParams P26 = PayrollDefaults.For(2026);

    static PayrollResult Calc(decimal gross, int month = 1, int unpaid = 0, decimal ot = 0, decimal add = 0, decimal ded = 0, decimal prior = 0) =>
        PayrollCalculator.Calculate(P26, new PayrollInput(month, gross, unpaid, ot, add, ded, prior));

    [Fact]
    public void Asgari_ucret_2026_resmi_net_ve_isveren_maliyeti()
    {
        var r = Calc(33030m);
        Assert.Equal(4624.20m, r.SgkEmployee);
        Assert.Equal(330.30m, r.UnemploymentEmployee);
        Assert.Equal(28075.50m, r.TaxBase);
        Assert.Equal(r.IncomeTax, r.IncomeTaxExemption);       // GV tamamen istisna
        Assert.Equal(r.StampTax, r.StampTaxExemption);         // damga tamamen istisna
        Assert.Equal(28075.50m, r.Net);                         // resmi net asgari ücret
        Assert.Equal(40214.03m, r.EmployerCost);                // imalat dışı, 2 puan teşvik
    }

    [Theory]
    [InlineData(7)]
    [InlineData(12)]
    public void Asgari_ucretli_dilim_atlasa_da_vergi_odemez(int month)
    {
        // Önceki ayların asgari ücret matrahı ikinci dilime taşısa da istisna aynı kümülatifle hesaplanır.
        var prior = 28075.50m * (month - 1);
        var r = Calc(33030m, month, prior: prior);
        Assert.Equal(28075.50m, r.Net);
    }

    [Fact]
    public void Ust_ucret_tavana_takilir_ve_vergi_dilimi_artar()
    {
        var r = Calc(400_000m);
        Assert.Equal(297_270m, r.SgkBase);                      // 9 x asgari ücret
        Assert.Equal(41_617.80m, r.SgkEmployee);
        var taxBase = 400_000m - 41_617.80m - 2_972.70m;
        Assert.Equal(taxBase, r.TaxBase);
        // 0-190k %15, 190k-355.4k %20
        var expected = Math.Round(190_000m * 0.15m + (taxBase - 190_000m) * 0.20m, 2);
        Assert.Equal(expected, r.IncomeTax);
        Assert.True(r.Net < r.Gross - r.SgkEmployee - r.UnemploymentEmployee);
    }

    [Fact]
    public void Kumulatif_matrah_ikinci_dilime_gecer()
    {
        var first = Calc(100_000m, 1);
        var march = Calc(100_000m, 3, prior: first.TaxBase * 2);
        Assert.True(march.IncomeTax > first.IncomeTax);         // 190 bin aşıldı
        Assert.Equal(first.TaxBase * 3, march.CumulativeTaxBase);
    }

    [Fact]
    public void Eksik_gun_brut_ve_istisnayi_oranlar()
    {
        var r = Calc(60_000m, unpaid: 10);
        Assert.Equal(20, r.PaidDays);
        Assert.Equal(40_000m, r.BaseGross);
        Assert.Equal(Math.Round(33030m * 20 / 30 * 0.00759m, 2), r.StampTaxExemption);
    }

    [Fact]
    public void Tam_ay_ucretsiz_izinde_prim_ve_vergi_yok()
    {
        var r = Calc(50_000m, unpaid: 30);
        Assert.Equal(0, r.Gross);
        Assert.Equal(0, r.SgkBase);
        Assert.Equal(0, r.Net);
    }

    [Fact]
    public void Fazla_mesai_yuzde_elli_zamli_saatlik_ucret()
    {
        var r = Calc(45_000m, ot: 10);
        Assert.Equal(Math.Round(45_000m / 225m * 1.5m * 10, 2), r.OvertimePay);   // 3.000,00
        Assert.Equal(48_000m, r.Gross);
    }

    [Fact]
    public void Kesinti_netten_dusulur_ek_odeme_brute_eklenir()
    {
        var baseR = Calc(50_000m);
        var r = Calc(50_000m, add: 5_000m, ded: 2_000m);
        Assert.Equal(55_000m, r.Gross);
        Assert.Equal(2_000m, r.Deductions);
        Assert.True(r.Net < baseR.Net + 5_000m - 2_000m);       // ek ödeme vergilendi
    }

    [Fact]
    public void Vergi_tarifesi_dilim_sinirlari()
    {
        Assert.Equal(28_500m, PayrollCalculator.Tax(190_000m, P26.Brackets));
        Assert.Equal(70_500m, PayrollCalculator.Tax(400_000m, P26.Brackets));
        Assert.Equal(367_500m, PayrollCalculator.Tax(1_500_000m, P26.Brackets));
        Assert.Equal(1_697_500m, PayrollCalculator.Tax(5_300_000m, P26.Brackets));
    }

    [Fact]
    public void Gecersiz_ay_reddedilir() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc(30_000m, month: 13));
}

public class ExporterTests
{
    private static readonly CompensationService.Models.PayrollPeriod Period = new() { Year = 2026, Month = 10, Status = CompensationService.Models.PayrollPeriodStatus.Closed };

    private static CompensationService.Models.Payslip Slip(Guid emp, decimal gross, decimal net) => new()
    {
        EmployeeId = emp, Year = 2026, Month = 10, PaidDays = 30, Gross = gross, SgkBase = gross, Net = net,
        SgkEmployee = gross * 0.14m, UnemploymentEmployee = gross * 0.01m, IncomeTax = gross - net - gross * 0.15m, IncomeTaxExemption = 0,
        StampTax = 0, StampTaxExemption = 0, SgkEmployer = gross * 0.1975m, UnemploymentEmployer = gross * 0.02m,
    };

    [Theory]
    [InlineData("10000000146", true)]
    [InlineData("10000000147", false)]
    [InlineData("01234567890", false)]
    [InlineData(null, false)]
    public void Tckn_dogrulama(string? t, bool ok) => Assert.Equal(ok, CompensationService.Payroll.Exporters.ValidTckn(t));

    [Theory]
    [InlineData("TR330006100519786457841326", true)]
    [InlineData("TR330006100519786457841327", false)]
    [InlineData("DE89370400440532013000", false)]
    public void Iban_dogrulama(string iban, bool ok) => Assert.Equal(ok, CompensationService.Payroll.Exporters.ValidIban(iban));

    [Fact]
    public void Taksit_son_taksit_farki_tasir()
    {
        Assert.Equal(333.33m, CompensationService.Payroll.Exporters.Installment(1000, 3, 0));
        Assert.Equal(333.34m, CompensationService.Payroll.Exporters.Installment(1000, 3, 2));
        var a = new CompensationService.Models.SalaryAdvance { Amount = 1000, Installments = 3, StartYear = 2026, StartMonth = 11 };
        Assert.Equal(-1, CompensationService.Payroll.Exporters.InstallmentIndex(a, 2026, 10));
        Assert.Equal(0, CompensationService.Payroll.Exporters.InstallmentIndex(a, 2026, 11));
        Assert.Equal(2, CompensationService.Payroll.Exporters.InstallmentIndex(a, 2027, 1));
        Assert.Equal(-1, CompensationService.Payroll.Exporters.InstallmentIndex(a, 2027, 2));
    }

    [Fact]
    public void Sgk_xml_ve_eksik_tckn_uyarisi()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var people = new Dictionary<Guid, CompensationService.Payroll.ExportPerson>
        {
            [a] = new(a, "Ayşe", "Yılmaz", "10000000146", null, "Mühendislik", new DateOnly(2026, 10, 15), null),
            [b] = new(b, "İsmail", "Işık", null, null, "Satış", new DateOnly(2020, 1, 1), null),
        };
        var r = CompensationService.Payroll.Exporters.SgkAphb(Period, new[] { Slip(a, 50000, 38000), Slip(b, 40000, 31000) }, people, "Demo A.Ş.");
        var xml = System.Text.Encoding.UTF8.GetString(r.Content);
        Assert.Equal(1, r.Rows);
        Assert.Contains("<TCKIMLIKNO>10000000146</TCKIMLIKNO>", xml);
        Assert.Contains("<AD>AYŞE</AD>", xml);
        Assert.Contains("<ISEGIRISGUN>15</ISEGIRISGUN>", xml);
        Assert.Contains("<PRIMEESASKAZANC>50000.00</PRIMEESASKAZANC>", xml);
        Assert.Single(r.Warnings);
        Assert.Contains("İsmail", r.Warnings[0]);
    }

    [Fact]
    public void Banka_dosyasi_gecersiz_ibani_almaz()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var people = new Dictionary<Guid, CompensationService.Payroll.ExportPerson>
        {
            [a] = new(a, "Ayşe", "Yılmaz", null, "TR33 0006 1005 1978 6457 8413 26", null, new DateOnly(2020, 1, 1), null),
            [b] = new(b, "Can", "Demir", null, "TR00", null, new DateOnly(2020, 1, 1), null),
        };
        var r = CompensationService.Payroll.Exporters.Bank(Period, new[] { Slip(a, 50000, 38000.5m), Slip(b, 40000, 31000) }, people);
        var csv = System.Text.Encoding.UTF8.GetString(r.Content);
        Assert.Equal(1, r.Rows);
        Assert.Contains("TR330006100519786457841326;38000.50;", csv);
        Assert.DoesNotContain("CAN DEMİR", csv);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Muhasebe_fisi_dengeli_ve_kisi_icermez()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var people = new Dictionary<Guid, CompensationService.Payroll.ExportPerson>
        {
            [a] = new(a, "Ayşe", "Yılmaz", "10000000146", null, "Mühendislik", new DateOnly(2020, 1, 1), null),
            [b] = new(b, "Can", "Demir", null, null, "Satış", new DateOnly(2020, 1, 1), null),
        };
        var slips = new[] { Slip(a, 50000, 38000), Slip(b, 40000, 31000) };
        var lines = CompensationService.Payroll.Exporters.Journal(Period, slips, people, new CompensationService.Payroll.AccountMap());
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        foreach (var f in new[] { "generic", "logo", "mikro", "netsis" })
        {
            var r = CompensationService.Payroll.Exporters.Accounting(Period, slips, people, f, new CompensationService.Payroll.AccountMap());
            var csv = System.Text.Encoding.UTF8.GetString(r.Content);
            Assert.Empty(r.Warnings);
            Assert.DoesNotContain("Ayşe", csv);
            Assert.DoesNotContain("10000000146", csv);
            Assert.Contains("Mühendislik", csv);
        }
    }
}
