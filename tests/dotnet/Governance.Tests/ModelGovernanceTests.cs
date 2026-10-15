using GovernanceService.Infrastructure;
using Xunit;

namespace Governance.Tests;

/// <summary>Devir riski modeli adillik denetimi girdisi ve inceleme eşiği (ML dalgası 1, madde 37 + 39).</summary>
public class ModelGovernanceTests
{
    private const string Header = "employee_id,tenure_years,compa_ratio,last_rating,months_since_promotion,overtime_hours_month,training_hours_year";

    private static Person P(Guid id, string? dept, DateOnly hire) =>
        new(id, "Ad Soyad", null, null, null, dept, hire, "Active", null, null);

    [Fact]
    public void Csv_parses_rows_with_optional_label()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (rows, errors) = AttritionFairness.ParseCsv($"{Header},label\n{a},3,0.95,4,12,8,20,1\n{b},1.5,1.1,3,30,0,5,\n");
        Assert.Empty(errors);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].Label);
        Assert.Null(rows[1].Label);
        Assert.Equal(0.95, rows[0].Features[1]);
    }

    [Theory]
    [InlineData(",gender")]
    [InlineData(",name")]
    [InlineData(",birth_date")]
    public void Csv_rejects_extra_columns(string extra)
    {
        var (rows, errors) = AttritionFairness.ParseCsv($"{Header}{extra}\n{Guid.NewGuid()},3,1,3,12,8,20,x\n");
        Assert.Empty(rows);
        Assert.Contains(errors, e => e.StartsWith("Beklenmeyen sütun"));
    }

    [Fact]
    public void Csv_reports_bad_values_and_missing_columns()
    {
        Assert.Contains(AttritionFairness.ParseCsv("employee_id,tenure_years\n").Errors, e => e.StartsWith("Eksik sütun"));
        var (_, errors) = AttritionFairness.ParseCsv($"{Header},label\nnot-a-guid,3,1,3,12,8,20,1\n{Guid.NewGuid()},3,abc,3,12,8,20,1\n{Guid.NewGuid()},3,1,3,12,8,20,2\n");
        Assert.Equal(3, errors.Count);
        Assert.Contains(AttritionFairness.ParseCsv("").Errors, e => e == "CSV boş.");
    }

    [Theory]
    [InlineData(2026, 6, 1, "0-1")]
    [InlineData(2024, 10, 1, "1-3")]
    [InlineData(2022, 1, 1, "3-5")]
    [InlineData(2018, 1, 1, "5-10")]
    [InlineData(2010, 1, 1, "10+")]
    public void Tenure_band(int y, int m, int d, string band)
        => Assert.Equal(band, AttritionFairness.TenureBand(new DateOnly(y, m, d), new DateOnly(2026, 10, 6)));

    [Fact]
    public void Rows_carry_groups_but_no_identity_and_skip_unknown_or_duplicate()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var people = new Dictionary<Guid, Person> { [a] = P(a, "Mühendislik", new DateOnly(2020, 1, 1)) };
        var (rows, _) = AttritionFairness.ParseCsv($"{Header},label\n{a},3,1,3,12,8,20,0\n{a},3,1,3,12,8,20,0\n{b},3,1,3,12,8,20,1\n");
        var (payload, skipped) = AttritionFairness.BuildRows(rows, people, new DateOnly(2026, 10, 6));
        Assert.Single(payload);
        Assert.Equal(2, skipped);
        var row = payload[0];
        Assert.DoesNotContain("employee_id", row.Keys);
        Assert.Equal(0, row["label"]);
        var groups = Assert.IsType<Dictionary<string, string?>>(row["groups"]);
        Assert.Equal("Mühendislik", groups["department"]);
        Assert.Equal("5-10", groups["tenure_band"]);
        Assert.Equal(new[] { "department", "tenure_band" }, groups.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData("0.5", true)]
    [InlineData("0.01", true)]
    [InlineData("0.99", true)]
    [InlineData("0.375", true)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("0.3755", false)]
    public void Threshold_validation(string value, bool ok)
        => Assert.Equal(ok, AttritionFairness.ValidThreshold(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
}
