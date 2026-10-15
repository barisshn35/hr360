using CompensationService.Data;
using CompensationService.Models;
using Microsoft.EntityFrameworkCore;

namespace CompensationService.Payroll;

/// <summary>
/// Bordro dosyaları ve dalga 8 uçları için ortak okumalar (başka servislerin tablolarından yalnızca OKUMA:
/// employee_employees, engagement_profiles, employee_assignments, organization_departments,
/// engagement_offboarding_cases, leave_requests).
/// </summary>
public static class PayrollData
{
    public sealed class PersonRow
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string? NationalId { get; set; }
        public string? Iban { get; set; }
        public string? Department { get; set; }
        public Guid? DepartmentId { get; set; }
        public Guid? HeadId { get; set; }
        public DateOnly HireDate { get; set; }
        public string Status { get; set; } = "";
        public DateOnly? EndDate { get; set; }
        /// <summary>En son (iptal edilmemiş) offboarding kaydının ayrılış nedeni ve son iş günü.</summary>
        public string? ExitReason { get; set; }
        public DateOnly? ExitDate { get; set; }
        public string? PositionTitle { get; set; }
    }

    public static Task<List<PersonRow>> PeopleAsync(CompensationDbContext db, string tenant, CancellationToken ct) => db.Database.SqlQueryRaw<PersonRow>("""
        SELECT e."Id", e."FirstName", e."LastName", p."NationalId", p."Iban", d."Name" AS "Department", d."Id" AS "DepartmentId",
               d."HeadEmployeeId" AS "HeadId", e."HireDate", e."Status",
               CASE WHEN e."Status" = 'Terminated' THEN (SELECT max(x."EffectiveTo") FROM employee_assignments x WHERE x."EmployeeId" = e."Id") END AS "EndDate",
               ob."Reason" AS "ExitReason", ob."LastWorkingDay" AS "ExitDate", a."PositionTitle"
        FROM employee_employees e
        LEFT JOIN engagement_profiles p ON p."TenantSlug" = e."TenantSlug" AND p."EmployeeId" = e."Id"
        LEFT JOIN LATERAL (SELECT a."DepartmentId", a."PositionTitle" FROM employee_assignments a WHERE a."EmployeeId" = e."Id"
                           ORDER BY (a."EffectiveTo" IS NULL) DESC, a."EffectiveFrom" DESC LIMIT 1) a ON true
        LEFT JOIN organization_departments d ON d."Id" = a."DepartmentId"
        LEFT JOIN LATERAL (SELECT o."Reason", o."LastWorkingDay" FROM engagement_offboarding_cases o
                           WHERE o."TenantSlug" = e."TenantSlug" AND o."EmployeeId" = e."Id" AND o."Status" <> 'Cancelled'
                           ORDER BY o."CreatedAt" DESC LIMIT 1) ob ON true
        WHERE e."TenantSlug" = {0}
        """, tenant).ToListAsync(ct);

    public static ExportPerson ToExport(PersonRow r) => new(r.Id, r.FirstName, r.LastName,
        ExportCrypto.OpenPii(r.NationalId)?.Trim(), ExportCrypto.OpenPii(r.Iban), r.Department, r.HireDate, r.EndDate ?? (r.Status == "Terminated" ? r.ExitDate : null));

    public static async Task<PayrollSettingsModel> SettingsAsync(CompensationDbContext db, CancellationToken ct) =>
        PayrollSettingsModel.From(await db.PayrollSettings.AsNoTracking().FirstOrDefaultAsync(ct));

    public sealed class LeaveDaysRow { public Guid EmployeeId { get; set; } public string Type { get; set; } = ""; public decimal Value { get; set; } }

    /// <summary>Dönemde onaylı izin günleri (takvim günü), çalışan ve tür bazında; yalnızca verilen türler.</summary>
    public static async Task<Dictionary<Guid, Dictionary<string, decimal>>> LeaveDaysAsync(CompensationDbContext db, string tenant, PayrollPeriod period,
        string[] types, CancellationToken ct)
    {
        var start = new DateOnly(period.Year, period.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        if (types.Length == 0) return new();
        var rows = await db.Database.SqlQueryRaw<LeaveDaysRow>("""
            SELECT "EmployeeId", "Type"::text AS "Type", sum(least("EndDate", {2}) - greatest("StartDate", {1}) + 1)::numeric AS "Value"
            FROM leave_requests
            WHERE "TenantSlug" = {0} AND "Type" = ANY({3}) AND "Status" = 'Approved' AND "StartDate" <= {2} AND "EndDate" >= {1}
              AND NOT ("StartDate" = "EndDate" AND "Days" < 1) -- kısmi gün izni eksik gün değildir (dalga 9)
            GROUP BY "EmployeeId", "Type"
            """, tenant, start, end, types).ToListAsync(ct);
        return rows.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Type, r => r.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// APHB girdisi: kişiler (TCKN açılmış), SGK bilgileri, izin günleri, ayarlar ve dönem parametreleri
    /// (dönem için parametre satırı yoksa null — çağıran 409 payroll_parameters_missing verir).
    /// </summary>
    public static async Task<(IReadOnlyDictionary<Guid, AphbPerson> People, Dictionary<Guid, Dictionary<string, decimal>> Leave, PayrollSettingsModel Settings, PayrollParams? Params)>
        AphbInputAsync(CompensationDbContext db, string tenant, PayrollPeriod period, CancellationToken ct)
    {
        var settings = await SettingsAsync(db, ct);
        var sgk = await db.EmployeeSgk.AsNoTracking().ToDictionaryAsync(x => x.EmployeeId, ct);
        var rows = await PeopleAsync(db, tenant, ct);
        var people = rows.ToDictionary(r => r.Id, r =>
        {
            var e = ToExport(r);
            sgk.TryGetValue(r.Id, out var s);
            return new AphbPerson(r.Id, r.FirstName, r.LastName, e.NationalId, r.HireDate, e.TerminationDate, r.ExitReason,
                s?.OccupationCode, s?.DocumentType, s?.LawNo, s?.Sgdp ?? false);
        });
        var types = settings.Sgk.MissingDayCodes.Select(m => m.LeaveType).Distinct().ToArray();
        var leave = await LeaveDaysAsync(db, tenant, period, types, ct);
        var paramRows = await db.PayrollParameters.AsNoTracking().Where(p => p.Year == period.Year).ToListAsync(ct);
        return (people, leave, settings, PayrollParameterResolver.Resolve(paramRows, period.Year, period.Month));
    }
}
