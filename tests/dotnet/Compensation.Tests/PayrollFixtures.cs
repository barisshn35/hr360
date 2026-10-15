using CompensationService.Payroll;

namespace Compensation.Tests;

/// <summary>
/// YALNIZCA TEST GİRDİSİ: hesap motorunun regresyon testleri için sabit parametre kümeleri. Uygulama bu
/// değerleri kullanmaz ve varsaymaz (ürün kararı: yıllık bordro parametrelerini bordro yetkilisi girer).
/// "2026" kümesi önceki sürümün kod varsayılanlarının kopyasıdır; resmî kaynakla doğrulanmış değildir.
/// </summary>
public static class PayrollDefaults
{
    public static PayrollParams For(int year) => year switch
    {
        <= 2025 => new PayrollParams(year, 26005.50m, 0.14m, 0.01m, 0.2075m, 5m, 0.02m, 0.00759m, 7.5m,
            PayrollStatutory.OvertimeMultiplier, PayrollStatutory.MonthlyHours, new[]
            {
                new TaxBracket(158_000m, 0.15m), new TaxBracket(330_000m, 0.20m), new TaxBracket(1_200_000m, 0.27m),
                new TaxBracket(4_300_000m, 0.35m), new TaxBracket(null, 0.40m),
            }),
        _ => new PayrollParams(year, 33030.00m, 0.14m, 0.01m, 0.2175m, 2m, 0.02m, 0.00759m, 9m,
            PayrollStatutory.OvertimeMultiplier, PayrollStatutory.MonthlyHours, new[]
            {
                new TaxBracket(190_000m, 0.15m), new TaxBracket(400_000m, 0.20m), new TaxBracket(1_500_000m, 0.27m),
                new TaxBracket(5_300_000m, 0.35m), new TaxBracket(null, 0.40m),
            }),
    };

    /// <summary>Fixture kümesinin geçerli bir API girdisi karşılığı.</summary>
    public static PayrollParameterInput Input(int year, int from = 1, decimal? h1 = 64_948.77m, decimal? h2 = 73_729.87m)
    {
        var p = For(year);
        return new PayrollParameterInput(from, p.MinimumWageGross, null, p.SgkEmployeeRate, p.UnemploymentEmployeeRate, p.SgkEmployerRate,
            p.EmployerIncentivePoints, p.UnemploymentEmployerRate, p.StampTaxRate, p.SgkCeilingMultiplier, p.Brackets.ToList(), true, null, h1, h2);
    }
}
