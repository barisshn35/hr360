using System.Text.Json;
using CompensationService.Models;
using CompensationService.Payroll;
using Xunit;

namespace Compensation.Tests;

/// <summary>ML dalgası 2: bordro denetimi isteği (takma ad, geçmiş süzgeci), işaretlerin yazılması, ücret adaleti satırları.</summary>
public class PayrollMlTests
{
    static readonly Guid A = Guid.Parse("0e879b9e-d72b-489f-aa5b-8291e0bcbefb");
    static readonly Guid B = Guid.Parse("11111111-2222-3333-4444-555555555555");

    static Payslip Slip(Guid emp, decimal ot = 5, decimal add = 0, decimal ded = 0, decimal gross = 50000) =>
        new() { EmployeeId = emp, Year = 2026, Month = 8, OvertimeHours = ot, Additions = add, Deductions = ded, Gross = gross };

    static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    [Fact]
    public void Istek_kimlik_icermez_ve_gelecek_donemi_gecmise_katmaz()
    {
        var slips = new List<Payslip> { Slip(A), Slip(B, ot: 40) };
        var grades = new Dictionary<Guid, string?> { [A] = "g3", [B] = null };
        var history = new[]
        {
            new PayslipHistoryRow(A, 2026, 7, 4, 0, 0, 50000, 0),
            new PayslipHistoryRow(A, 2026, 9, 99, 0, 0, 50000, 0),   // sonraki dönem: gönderilmez
        };
        var body = Json(PayrollAnomalyClient.BuildRequest("demo", 2026, 8, slips, grades, history));
        Assert.Equal("2026-08", body.GetProperty("period").GetString());
        var text = body.GetRawText();
        Assert.DoesNotContain(A.ToString(), text);
        Assert.DoesNotContain(A.ToString("N"), text);
        Assert.DoesNotContain("g3", text, StringComparison.OrdinalIgnoreCase);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("0", items[0].GetProperty("id").GetString());
        Assert.Equal(PayrollAnomalyClient.Pseudonym("demo", A.ToString("N")), items[0].GetProperty("employee").GetString());
        // Kademe büyük/küçük harf ve boşluktan bağımsız aynı gruba düşer; kademesiz çalışanın grubu yok.
        Assert.Equal(PayrollAnomalyClient.Pseudonym("demo", "grade:G3"), items[0].GetProperty("group").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("group").ValueKind);
        var hist = body.GetProperty("history").EnumerateArray().ToList();
        Assert.Single(hist);
        Assert.Equal("2026-07", hist[0].GetProperty("period").GetString());
    }

    [Fact]
    public void Takma_ad_kiraciya_ozgu()
    {
        Assert.NotEqual(PayrollAnomalyClient.Pseudonym("a", A.ToString("N")), PayrollAnomalyClient.Pseudonym("b", A.ToString("N")));
        Assert.Equal(16, PayrollAnomalyClient.Pseudonym("a", "x").Length);
    }

    [Fact]
    public void Isaretler_sirayla_yazilir_gecersiz_satirlar_atlanir()
    {
        var slips = new List<Payslip> { Slip(A), Slip(B) };
        slips[0].AnomalyFlagsJson = "[{\"code\":\"ESKI\"}]";
        var resp = Json(new
        {
            items = new object[]
            {
                new { id = "1", flags = new[] { new { code = "OVERTIME_SPIKE_OWN", severity = "low" }, new { code = "OVERTIME_ANNUAL_LIMIT", severity = "high" } } },
                new { id = "0", flags = Array.Empty<object>() },
                new { id = "7", flags = new[] { new { code = "X", severity = "low" } } },
            },
        });
        Assert.Equal(2, PayrollAnomalyClient.ApplyFlags(slips, resp));
        Assert.Null(slips[0].AnomalyFlagsJson);
        Assert.Contains("OVERTIME_ANNUAL_LIMIT", slips[1].AnomalyFlagsJson);
        Assert.Equal(0, PayrollAnomalyClient.ApplyFlags(slips, Json(new { other = 1 })));
    }

    [Fact]
    public void Isaretler_calisanin_pusula_yanitinda_yer_almaz()
    {
        var s = Slip(A);
        s.AnomalyFlagsJson = "[{\"code\":\"OVERTIME_SPIKE_OWN\"}]";
        var json = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("Anomaly", json);
        Assert.DoesNotContain("OVERTIME_SPIKE_OWN", json);
    }

    [Fact]
    public void Ucret_adaleti_satirlari_kimliksiz_ve_tek_para_birimi()
    {
        var today = new DateOnly(2026, 10, 6);
        var rows = new List<PayEquityRow>
        {
            new(50000, "TRY", "G1", "Yazılımcı", "Mühendislik", new DateOnly(2021, 10, 6)),
            new(60000, "TRY", " ", null, null, new DateOnly(2026, 10, 6)),
            new(4000, "EUR", "G1", "Yazılımcı", "Mühendislik", new DateOnly(2020, 1, 1)),
        };
        var (body, included, excluded, currency) = PayEquityClient.BuildRequest(rows, today);
        Assert.Equal(("TRY", 2, 1), (currency, included, excluded));
        var json = Json(body);
        var r = json.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(5.0, r[0].GetProperty("tenure_years").GetDouble(), 1);
        Assert.Equal(JsonValueKind.Null, r[1].GetProperty("grade").ValueKind);
        Assert.Equal(0.0, r[1].GetProperty("tenure_years").GetDouble());
        Assert.False(r[0].TryGetProperty("employeeId", out _));
    }

    [Fact]
    public void Ml_hata_iletisi_okunur()
    {
        Assert.Equal("en az 20", PayEquityClient.Detail("{\"detail\":\"en az 20\"}"));
        Assert.Null(PayEquityClient.Detail("{\"detail\":{\"code\":1}}"));
        Assert.Null(PayEquityClient.Detail("bozuk"));
    }
}
