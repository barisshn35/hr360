namespace CompensationService.Payroll;

/// <summary>Gelir vergisi tarifesinde bir dilim: üst sınır (son dilimde null) ve oran.</summary>
public sealed record TaxBracket(decimal? UpTo, decimal Rate);

/// <summary>
/// Bir dönemin bordro parametreleri. Yıllık değerler (asgari ücret, oranlar, dilimler, tavan) KOD İÇİNDE
/// TUTULMAZ: bordro yetkilisinin Bordro › Parametreler'de girdiği satırdan (compensation_payroll_parameters)
/// <see cref="PayrollParameterResolver.FromRow"/> ile kurulur. Satır yoksa hesaplama yapılmaz (409
/// payroll_parameters_missing). Yalnızca kanunla sabit iki değer (<see cref="PayrollStatutory"/>) koddadır.
/// </summary>
public sealed record PayrollParams(
    int Year,
    decimal MinimumWageGross,            // aylık brüt asgari ücret
    decimal SgkEmployeeRate,             // SGK işçi payı
    decimal UnemploymentEmployeeRate,    // işsizlik sigortası işçi payı
    decimal SgkEmployerRate,             // SGK işveren payı (teşviksiz)
    decimal EmployerIncentivePoints,     // 5510 s. K. teşvik indirimi (puan)
    decimal UnemploymentEmployerRate,    // işsizlik sigortası işveren payı
    decimal StampTaxRate,                // damga vergisi
    decimal SgkCeilingMultiplier,        // SGK tavanı = asgari ücret x çarpan
    decimal OvertimeMultiplier,          // fazla mesai zammı (İş K. m.41: %50)
    decimal MonthlyHours,                // saatlik ücret böleni (225 saat)
    IReadOnlyList<TaxBracket> Brackets,
    bool MinimumWageExemption = true)    // asgari ücret GV/damga istisnası (2022'den beri)
{
    public decimal EffectiveEmployerSgkRate => Math.Max(0, SgkEmployerRate - EmployerIncentivePoints / 100m);
}

/// <summary>Yıldan yıla değişmeyen, kanunda sabit değerler (yıllık parametre değildir).</summary>
public static class PayrollStatutory
{
    /// <summary>Fazla çalışma ücreti: saatlik ücretin %150'si (4857 s. İş Kanunu m.41).</summary>
    public const decimal OvertimeMultiplier = 1.5m;
    /// <summary>Aylık ücretten saatlik ücrete bölen: 30 gün x 7,5 saat = 225 saat.</summary>
    public const decimal MonthlyHours = 225m;
}

/// <summary>Bir çalışanın bir aylık bordro girdisi.</summary>
public sealed record PayrollInput(
    int Month,
    decimal MonthlyBaseGross,
    int UnpaidDays,                 // ücretsiz izin / eksik gün (takvim günü)
    decimal OvertimeHours,          // onaylı fazla mesai
    decimal Additions,              // vergiye tabi ek ödemeler (prim, ikramiye)
    decimal Deductions,             // net ücretten kesintiler (avans, icra vb.)
    decimal PriorCumulativeTaxBase); // aynı yılın önceki aylarındaki GV matrahı toplamı

public sealed record PayrollResult(
    int PaidDays, decimal BaseGross, decimal OvertimePay, decimal Additions, decimal Gross,
    decimal SgkBase, decimal SgkEmployee, decimal UnemploymentEmployee,
    decimal TaxBase, decimal CumulativeTaxBase, decimal IncomeTax, decimal IncomeTaxExemption,
    decimal StampTax, decimal StampTaxExemption, decimal Deductions, decimal Net,
    decimal SgkEmployer, decimal UnemploymentEmployer, decimal EmployerCost);

/// <summary>
/// Türkiye aylık ücret bordrosu (brütten nete). Yöntem:
///   brüt = taban ücret x ödenen gün/30 + fazla mesai (saatlik ücret x 1,5) + ek ödemeler
///   SGK matrahı = brüt, ödenen güne göre taban (asgari ücret) ve tavan (asgari ücret x çarpan) arasında
///   GV matrahı = brüt - SGK işçi - işsizlik işçi; GV kümülatif matraha göre artan oranlı
///   asgari ücret istisnası (GVK m.23/18): asgari ücretin GV'si ve damga vergisi kadar
///   (ödenen güne oranlanır) — çalışanın vergisini aşamaz
/// Her adım kuruşa yuvarlanır.
/// </summary>
public static class PayrollCalculator
{
    static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Kümülatif matrah için toplam gelir vergisi.</summary>
    public static decimal Tax(decimal income, IReadOnlyList<TaxBracket> brackets)
    {
        if (income <= 0) return 0;
        decimal tax = 0, lower = 0;
        foreach (var b in brackets)
        {
            var upper = b.UpTo ?? decimal.MaxValue;
            if (income <= lower) break;
            tax += (Math.Min(income, upper) - lower) * b.Rate;
            lower = upper;
        }
        return tax;
    }

    /// <summary>
    /// Ayın hesabına giren önceki kümülatif GV matrahı: devreden (HR360 dışı, ör. yıl ortası geçiş ya da önceki
    /// işveren) matrah + aynı yılın önceki aylarındaki pusulaların matrahı. Negatif devreden matrah yok sayılır.
    /// </summary>
    public static decimal PriorCumulativeBase(decimal openingTaxBase, decimal priorPayslipsTaxBase) =>
        Math.Max(0, openingTaxBase) + Math.Max(0, priorPayslipsTaxBase);

    /// <summary>Kümülatif matrah önceki + bu ay iken bu aya düşen vergi.</summary>
    public static decimal MonthlyTax(decimal priorCumulative, decimal monthBase, IReadOnlyList<TaxBracket> brackets) =>
        R(Tax(priorCumulative + monthBase, brackets) - Tax(priorCumulative, brackets));

    public static PayrollResult Calculate(PayrollParams p, PayrollInput i)
    {
        if (i.Month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(i), "Ay 1-12 arasında olmalı");
        var paidDays = Math.Clamp(30 - i.UnpaidDays, 0, 30);
        var dayRatio = paidDays / 30m;

        var baseGross = R(i.MonthlyBaseGross * dayRatio);
        var hourly = i.MonthlyBaseGross / p.MonthlyHours;
        var overtimePay = R(hourly * p.OvertimeMultiplier * Math.Max(0, i.OvertimeHours));
        var additions = R(Math.Max(0, i.Additions));
        var gross = baseGross + overtimePay + additions;

        var floor = R(p.MinimumWageGross * dayRatio);
        var ceiling = R(p.MinimumWageGross * p.SgkCeilingMultiplier * dayRatio);
        var sgkBase = paidDays == 0 ? 0 : Math.Clamp(gross, floor, ceiling);
        var sgkEmp = R(sgkBase * p.SgkEmployeeRate);
        var unempEmp = R(sgkBase * p.UnemploymentEmployeeRate);

        var taxBase = Math.Max(0, gross - sgkEmp - unempEmp);
        var incomeTax = MonthlyTax(i.PriorCumulativeTaxBase, taxBase, p.Brackets);

        // Asgari ücret istisnası: asgari ücretin (ödenen güne oranlanmış) kendi GV matrahı ve
        // aynı yılın önceki aylarındaki asgari ücret matrahı üzerinden hesaplanan vergi.
        var mwBaseFull = p.MinimumWageGross * (1 - p.SgkEmployeeRate - p.UnemploymentEmployeeRate);
        var mwMonthBase = R(mwBaseFull * dayRatio);
        var mwPrior = R(mwBaseFull * (i.Month - 1));
        var taxExemption = p.MinimumWageExemption ? Math.Min(incomeTax, MonthlyTax(mwPrior, mwMonthBase, p.Brackets)) : 0;

        var stamp = R(gross * p.StampTaxRate);
        var stampExemption = p.MinimumWageExemption ? Math.Min(stamp, R(p.MinimumWageGross * dayRatio * p.StampTaxRate)) : 0;

        var deductions = R(Math.Max(0, i.Deductions));
        var net = gross - sgkEmp - unempEmp - (incomeTax - taxExemption) - (stamp - stampExemption) - deductions;

        var sgkEr = R(sgkBase * p.EffectiveEmployerSgkRate);
        var unempEr = R(sgkBase * p.UnemploymentEmployerRate);

        return new PayrollResult(paidDays, baseGross, overtimePay, additions, gross, sgkBase, sgkEmp, unempEmp,
            taxBase, i.PriorCumulativeTaxBase + taxBase, incomeTax, taxExemption, stamp, stampExemption, deductions,
            net, sgkEr, unempEr, gross + sgkEr + unempEr);
    }
}
