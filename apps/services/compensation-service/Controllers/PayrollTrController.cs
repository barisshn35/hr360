using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CompensationService.Data;
using CompensationService.Models;
using CompensationService.Payroll;
using CompensationService.Tenancy;

namespace CompensationService.Controllers;

/// <summary>
/// Bordro dalgası 8 (madde 58–65): bordro ayarları ve çalışan SGK bilgileri, APHB doğrulama raporu,
/// fark bordrosu, kıdem/ihbar hesabı ve ibraname, e-bordro yayımlama ve okundu/teslim alındı kaydı,
/// ücret bandı compa-ratio ve kapsama raporu.
///
/// Yetki: yazma uçları bordro yetkilisi (hr-admin, tenant-admin, platform-admin); okuma uçları ayrıca
/// ext-compensation-view. Hassas görüntüleme/dışa aktarma audit_log'a yazılır. Dış sistemlere (SGK, banka,
/// e-posta sağlayıcısı) doğrudan bağlanılmaz: dosya üretilir, e-posta notification-service kuyruğuna yazılır.
/// </summary>
[ApiController]
[Route("api/compensation")]
[Authorize]
public class PayrollTrController : ControllerBase
{
    private readonly CompensationDbContext _db;
    private readonly ITenantContext _tenant;
    public PayrollTrController(CompensationDbContext db, ITenantContext tenant) { _db = db; _tenant = tenant; }

    private bool IsPayrollAdmin => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin");
    private bool IsPayrollViewer => IsPayrollAdmin || User.IsInRole("ext-compensation-view");
    private string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value ?? "unknown";
    private string UserName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? UserId;
    private string Tenant => _tenant.TenantSlug ?? "";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task AuditAsync(string entityType, string entityId, string action, object changes, string? tenant = null)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_log (\"TenantSlug\",\"Service\",\"EntityType\",\"EntityId\",\"Action\",\"Changes\",\"UserId\",\"UserName\",\"CorrelationId\",\"IpAddress\",\"OccurredAt\") " +
                "VALUES ({0},'compensation-service',{1},{2},{3},{4}::jsonb,{5},{6},{7},{8},now())",
                (object?)(tenant ?? _tenant.TenantSlug) ?? DBNull.Value, entityType, entityId, action, JsonSerializer.Serialize(changes, Json),
                UserId, UserName, (object?)(Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? HttpContext.TraceIdentifier) ?? DBNull.Value,
                (object?)Request.Headers["X-Real-IP"].FirstOrDefault() ?? DBNull.Value);
        }
        catch (Exception ex)
        {
            // Denetim yazılamazsa iş akışı bozulmaz; ama sessizce yutulmaz (KVKK erişim kaydı eksik kalır).
            HttpContext.RequestServices.GetService<ILogger<PayrollAudit>>()?.LogWarning(ex, "audit_log yazılamadı: {EntityType} {Action}", entityType, action);
        }
    }

    private async Task<Guid?> MyEmployeeIdAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Tenant)) return null;
        return await _db.Database.SqlQueryRaw<Guid?>(
            "SELECT \"Id\" AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"KeycloakUserId\" = {1} LIMIT 1",
            Tenant, UserId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Kiracının görevler ayrılığı ayarı (satır yoksa ya da okunamazsa kural AÇIK).</summary>
    private async Task<bool> SodEnforcedAsync(CancellationToken ct)
    {
        try
        {
            var v = await _db.Database.SqlQueryRaw<bool>(
                "SELECT \"PayrollSod\" AS \"Value\" FROM governance_security_settings WHERE \"TenantSlug\" = {0}", Tenant).ToListAsync(ct);
            return v.Count == 0 || v[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return true; }
    }

    /// <summary>Ay başına parametreler; satırı olmayan ay için değer null (çağıran 409 verir, koddan varsayılan yok).</summary>
    private async Task<Dictionary<(int, int), PayrollParams?>> ParamCacheAsync(IEnumerable<(int Year, int Month)> months, CancellationToken ct)
    {
        var list = months.Distinct().ToList();
        var years = list.Select(x => x.Year).Distinct().ToList();
        var rows = await _db.PayrollParameters.AsNoTracking().Where(p => years.Contains(p.Year)).ToListAsync(ct);
        return list.ToDictionary(x => x, x => PayrollParameterResolver.Resolve(rows, x.Year, x.Month));
    }

    /* ================================================================== ayarlar (58/62/64) */

    [HttpGet("payroll/settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var row = await _db.PayrollSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var m = PayrollSettingsModel.From(row);
        return Ok(new
        {
            sgk = m.Sgk, accounts = m.Accounts, costCenters = m.CostCenters, bank = m.Bank, row?.UpdatedBy, row?.UpdatedAt,
            bankTemplates = new[] { "generic", "ornek-a", "ornek-b", "custom" }, bankFields = BankFiles.Fields.OrderBy(x => x)
        });
    }

    public record SettingsInput(SgkSettings? Sgk, AccountMap? Accounts, Dictionary<string, string>? CostCenters, BankSettings? Bank);

    /// <summary>Verilen bölümleri günceller (verilmeyenler değişmez). Değişiklik denetim kaydına yazılır.</summary>
    [HttpPut("payroll/settings")]
    public async Task<IActionResult> PutSettings([FromBody] SettingsInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var err = PayrollSettingsModel.Validate(body.Sgk, body.Accounts, body.CostCenters, body.Bank);
        if (err is not null) return BadRequest(new { message = err });
        var row = await _db.PayrollSettings.FirstOrDefaultAsync(ct);
        if (row is null) { row = new PayrollSettings(); _db.PayrollSettings.Add(row); }
        var changed = new List<string>();
        if (body.Sgk is not null) { row.SgkJson = PayrollSettingsModel.Write(body.Sgk); changed.Add("sgk"); }
        if (body.Accounts is not null) { row.AccountMapJson = PayrollSettingsModel.Write(body.Accounts); changed.Add("accounts"); }
        if (body.CostCenters is not null) { row.CostCentersJson = PayrollSettingsModel.Write(body.CostCenters); changed.Add("costCenters"); }
        if (body.Bank is not null) { row.BankTemplateJson = PayrollSettingsModel.Write(body.Bank); changed.Add("bank"); }
        row.UpdatedBy = UserName; row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("PayrollSettings", row.Id.ToString(), "Updated", new { sections = changed, body.Sgk, body.Accounts, body.CostCenters, body.Bank }, row.TenantSlug);
        return await GetSettings(ct);
    }

    /* ================================================================== çalışan SGK bilgileri (58) */

    [HttpGet("payroll/sgk/employees")]
    public async Task<IActionResult> SgkEmployees(CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var list = await _db.EmployeeSgk.AsNoTracking().ToListAsync(ct);
        return Ok(list.Select(x => new { x.EmployeeId, x.OccupationCode, x.DocumentType, x.LawNo, x.Sgdp, x.UpdatedBy, x.UpdatedAt }));
    }

    public record SgkEmployeeInput(string? OccupationCode, string? DocumentType, string? LawNo, bool Sgdp);
    private static readonly Regex OccupationPattern = new(@"^\d{4}\.\d{2}$", RegexOptions.Compiled);

    [HttpPut("payroll/sgk/employees/{employeeId:guid}")]
    public async Task<IActionResult> PutSgkEmployee(Guid employeeId, [FromBody] SgkEmployeeInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var occ = string.IsNullOrWhiteSpace(body.OccupationCode) ? null : body.OccupationCode.Trim();
        if (occ is not null && !OccupationPattern.IsMatch(occ)) return BadRequest(new { message = "Meslek kodu 0000.00 biçiminde olmalı (ör. 2512.01)" });
        var doc = string.IsNullOrWhiteSpace(body.DocumentType) ? null : body.DocumentType.Trim();
        var law = string.IsNullOrWhiteSpace(body.LawNo) ? null : body.LawNo.Trim();
        if (doc is not null && (doc.Length > 2 || !doc.All(char.IsAsciiDigit))) return BadRequest(new { message = "Belge türü en fazla iki haneli rakam olmalı" });
        if (law is not null && (law.Length is < 4 or > 5 || !law.All(char.IsAsciiDigit))) return BadRequest(new { message = "Kanun numarası 4–5 haneli rakam olmalı" });
        var exists = await _db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}", Tenant, employeeId).FirstAsync(ct);
        if (exists == 0) return NotFound(new { message = "Çalışan bulunamadı" });
        var row = await _db.EmployeeSgk.FirstOrDefaultAsync(x => x.EmployeeId == employeeId, ct);
        if (row is null) { row = new EmployeeSgkInfo { EmployeeId = employeeId }; _db.EmployeeSgk.Add(row); }
        row.OccupationCode = occ; row.DocumentType = doc; row.LawNo = law; row.Sgdp = body.Sgdp;
        row.UpdatedBy = UserName; row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.EmployeeId, row.OccupationCode, row.DocumentType, row.LawNo, row.Sgdp, row.UpdatedBy, row.UpdatedAt });
    }

    /* ================================================================== devreden kümülatif GV matrahı */

    private static object OpeningView(OpeningTaxBase o) => new { o.EmployeeId, o.Year, o.Amount, o.Note, o.UpdatedBy, o.UpdatedAt };

    /// <summary>Yılın devreden kümülatif GV matrahları (yalnızca bordro yetkilisi; ücret verisi, erişim kaydına yazılır).</summary>
    [HttpGet("payroll/opening-tax-bases")]
    public async Task<IActionResult> OpeningTaxBases([FromQuery] int year, CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        if (year is < 2020 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        var list = await _db.OpeningTaxBases.AsNoTracking().Where(o => o.Year == year).ToListAsync(ct);
        if (list.Count > 0) await AuditAsync("OpeningTaxBase", year.ToString(), "SensitiveViewed", new { field = "openingTaxBases", year, count = list.Count });
        return Ok(list.Select(OpeningView));
    }

    public record OpeningTaxBaseInput(decimal Amount, string? Note);

    /// <summary>
    /// Çalışanın yıl için devreden kümülatif GV matrahını yazar (0 girilirse kayıt silinir). Bordro hesabı ve
    /// ihbar GV'si bu tutarı yılın kümülatif matrahına ekler. Kapanmış dönemler değişmez; açık dönemler
    /// yeniden hesaplanınca girer. Eski ve yeni değer denetim kaydına yazılır.
    /// </summary>
    [HttpPut("payroll/opening-tax-bases/{employeeId:guid}/{year:int}")]
    public async Task<IActionResult> PutOpeningTaxBase(Guid employeeId, int year, [FromBody] OpeningTaxBaseInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        if (year is < 2020 or > 2100) return BadRequest(new { message = "Geçersiz yıl" });
        if (body.Amount is < 0 or > 1_000_000_000) return BadRequest(new { message = "Matrah 0 ile 1.000.000.000 arasında olmalı" });
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > 300 }) return BadRequest(new { message = "Not en fazla 300 karakter olabilir" });
        var exists = await _db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}", Tenant, employeeId).FirstAsync(ct);
        if (exists == 0) return NotFound(new { message = "Çalışan bulunamadı" });
        var row = await _db.OpeningTaxBases.FirstOrDefaultAsync(o => o.EmployeeId == employeeId && o.Year == year, ct);
        var before = row?.Amount;
        var amount = Math.Round(body.Amount, 2);
        if (amount == 0)
        {
            if (row is null) return NoContent();
            _db.OpeningTaxBases.Remove(row);
            await _db.SaveChangesAsync(ct);
            await AuditAsync("OpeningTaxBase", employeeId.ToString(), "Deleted", new { year, before }, row.TenantSlug);
            return NoContent();
        }
        if (row is null) { row = new OpeningTaxBase { EmployeeId = employeeId, Year = year }; _db.OpeningTaxBases.Add(row); }
        row.Amount = amount; row.Note = note; row.UpdatedBy = UserName; row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("OpeningTaxBase", employeeId.ToString(), before is null ? "Created" : "Updated", new { year, before, after = amount, note }, row.TenantSlug);
        return Ok(OpeningView(row));
    }

    /* ================================================================== APHB doğrulama raporu (58) */

    /// <summary>
    /// İndirmeden önce APHB doğrulaması: eksik/geçersiz TCKN (dosyaya alınmaz), eksik meslek kodu, gün &gt; 30,
    /// PEK asgari ücretin altında / tavanın üstünde, nedeni bulunamayan eksik gün, SGDP. Ad soyad içerdiği
    /// için görüntüleme erişim kaydına yazılır (TCKN yanıtta yer almaz).
    /// </summary>
    [HttpGet("payroll/periods/{id:guid}/sgk/validation")]
    public async Task<IActionResult> SgkValidation(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var period = await _db.PayrollPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (period is null) return NotFound();
        if (period.Status == PayrollPeriodStatus.Open) return BadRequest(new { message = "Önce dönemi hesaplayın", code = "period_open" });
        var slips = await _db.Payslips.AsNoTracking().Where(s => s.PeriodId == id).ToListAsync(ct);
        var (people, leave, settings, prm) = await PayrollData.AphbInputAsync(_db, Tenant, period, ct);
        // PEK taban/tavan denetimi dönemin parametreleriyle yapılır; girilmemişse varsayım yapılmaz.
        if (prm is null) return Conflict(PayrollParameterResolver.Missing(period.Year, period.Month));
        var (rows, issues) = Aphb.Build(period, slips, people, leave, settings.Sgk, prm);
        await AuditAsync("Payslip", id.ToString(), "SensitiveViewed", new { field = "sgkValidation", count = slips.Count }, period.TenantSlug);
        return Ok(new
        {
            periodId = id, period.Year, period.Month, payslips = slips.Count, included = rows.Count,
            errors = issues.Count(i => i.Level == "error"), warnings = issues.Count(i => i.Level == "warning"),
            totals = new { pek = rows.Sum(r => r.Pek), days = rows.Sum(r => r.PrimDays) },
            documents = rows.GroupBy(r => new { r.DocumentType, r.LawNo }).Select(g => new { g.Key.DocumentType, g.Key.LawNo, count = g.Count(), pek = g.Sum(r => r.Pek) }),
            issues = issues.Select(i => new { i.EmployeeId, i.Name, i.Level, i.Code, i.Message }),
        });
    }

    /* ================================================================== fark bordrosu (63) */

    /// <summary>
    /// Fark adayları. Yeniden hesap gereken bir pusulanın ayı için parametre satırı yoksa hesap yapılmaz;
    /// ilk eksik ay <c>Missing</c> olarak döner (çağıran 409 payroll_parameters_missing verir).
    /// </summary>
    private async Task<(List<RetroCandidate> List, (int Year, int Month)? Missing)> RetroCandidatesAsync(Guid? employeeId, CancellationToken ct)
    {
        var now = DateTime.UtcNow.AddHours(3);
        var from = now.Year * 12 + now.Month - 24;
        var closed = await _db.PayrollPeriods.AsNoTracking()
            .Where(p => p.Status == PayrollPeriodStatus.Closed && p.Year * 12 + p.Month >= from).ToListAsync(ct);
        var ids = closed.Select(p => p.Id).ToList();
        var slips = await _db.Payslips.AsNoTracking().Where(s => ids.Contains(s.PeriodId) && (employeeId == null || s.EmployeeId == employeeId)).ToListAsync(ct);
        if (slips.Count == 0) return (new(), null);
        var emps = slips.Select(s => s.EmployeeId).Distinct().ToList();
        var records = (await _db.Records.AsNoTracking().Where(r => emps.Contains(r.EmployeeId)).ToListAsync(ct)).ToLookup(r => r.EmployeeId);
        var paid = (await _db.RetroDiffs.AsNoTracking().Where(r => r.Status == "Approved" && emps.Contains(r.EmployeeId)).ToListAsync(ct))
            .GroupBy(r => (r.EmployeeId, r.SourcePeriodId)).ToDictionary(g => g.Key, g => g.Sum(r => r.DiffGross));
        var prm = await ParamCacheAsync(slips.Select(s => (s.Year, s.Month)), ct);
        var list = new List<RetroCandidate>();
        (int Year, int Month)? missing = null;
        foreach (var s in slips.OrderBy(x => x.Year).ThenBy(x => x.Month))
        {
            var rec = RetroPay.RecordFor(records[s.EmployeeId], s.Year, s.Month);
            if (rec is null || rec.Currency != s.Currency) continue;
            var already = paid.GetValueOrDefault((s.EmployeeId, s.PeriodId));
            if (rec.BaseSalary == s.MonthlyBaseGross && already == 0) continue;
            if (prm[(s.Year, s.Month)] is not { } p) { missing ??= (s.Year, s.Month); continue; }
            var c = RetroPay.Compute(p, s, rec.BaseSalary, already);
            if (c is not null) list.Add(c);
        }
        return (list.OrderBy(c => c.Year).ThenBy(c => c.Month).ThenBy(c => c.EmployeeId).ToList(), missing);
    }

    /// <summary>
    /// Fark bordrosu önizlemesi: son 24 aydaki kapanmış dönemlerden, ücret kaydı sonradan (geriye dönük)
    /// değişen pusulalar. Kapanmış dönemler açılmaz. Negatif fark (geriye dönük indirim) yalnızca gösterilir,
    /// uygulanmaz (İK elle değerlendirir). Ücret verisi içerdiği için erişim kaydına yazılır.
    /// </summary>
    [HttpGet("payroll/retro/candidates")]
    public async Task<IActionResult> RetroCandidates([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var (list, missing) = await RetroCandidatesAsync(employeeId, ct);
        if (missing is { } mm) return Conflict(PayrollParameterResolver.Missing(mm.Year, mm.Month));
        if (list.Count > 0) await AuditAsync("Payslip", employeeId?.ToString() ?? "retro", "SensitiveViewed", new { field = "retroCandidates", count = list.Count });
        return Ok(list.Select(c => new
        {
            c.EmployeeId, sourcePeriodId = c.SourcePeriodId, c.Year, c.Month, c.OldBase, c.NewBase, c.OldGross, c.NewGross, c.AlreadyPaid,
            c.DiffGross, estimatedNetDiff = c.NewNet - c.OldNet, label = c.Label, applicable = c.DiffGross > 0,
        }));
    }

    public record RetroItem(Guid EmployeeId, Guid SourcePeriodId);
    public record RetroApplyInput(Guid TargetPeriodId, List<RetroItem> Items);

    /// <summary>
    /// Seçilen farkları İK onayıyla hedef (açık) döneme "Fark: YYYY/AA" ek ödemesi olarak ekler. Tutarlar
    /// sunucuda yeniden hesaplanır. Onaylayan bu dönemin hazırlayanı sayılır (görevler ayrılığı: dönemi
    /// başka bir bordro yetkilisi kapatır). Hedef dönem "Açık"a döner, yeniden hesaplanmalıdır.
    /// </summary>
    [HttpPost("payroll/retro/apply")]
    public async Task<IActionResult> RetroApply([FromBody] RetroApplyInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        if (body.Items is null || body.Items.Count is 0 or > 500) return BadRequest(new { message = "1–500 fark seçin" });
        var target = await _db.PayrollPeriods.FirstOrDefaultAsync(p => p.Id == body.TargetPeriodId, ct);
        if (target is null) return NotFound(new { message = "Hedef dönem bulunamadı" });
        if (target.Status == PayrollPeriodStatus.Closed) return Conflict(new { message = "Fark kapanmış döneme eklenemez; açık bir dönem seçin", code = "period_closed" });
        var (candidates, missing) = await RetroCandidatesAsync(null, ct);
        if (missing is { } mm) return Conflict(PayrollParameterResolver.Missing(mm.Year, mm.Month));
        var applied = new List<RetroDiff>();
        foreach (var item in body.Items.DistinctBy(i => (i.EmployeeId, i.SourcePeriodId)))
        {
            var c = candidates.FirstOrDefault(x => x.EmployeeId == item.EmployeeId && x.SourcePeriodId == item.SourcePeriodId);
            if (c is null || c.DiffGross <= 0) continue;
            if (c.Year * 12 + c.Month >= target.Year * 12 + target.Month) continue;
            var rd = new RetroDiff
            {
                EmployeeId = c.EmployeeId, SourcePeriodId = c.SourcePeriodId, SourceYear = c.Year, SourceMonth = c.Month, OldBase = c.OldBase,
                NewBase = c.NewBase, OldGross = c.OldGross, NewGross = c.NewGross, DiffGross = c.DiffGross, TargetPeriodId = target.Id,
                CreatedBy = UserId, CreatedByName = UserName,
            };
            var adj = new PayrollAdjustment
            {
                PeriodId = target.Id, EmployeeId = c.EmployeeId, Kind = PayrollAdjustmentKind.Addition, Amount = c.DiffGross,
                Description = c.Label, SourceId = rd.Id, CreatedBy = UserId,
            };
            rd.AdjustmentId = adj.Id;
            _db.RetroDiffs.Add(rd);
            _db.PayrollAdjustments.Add(adj);
            applied.Add(rd);
        }
        if (applied.Count == 0) return BadRequest(new { message = "Uygulanabilir fark bulunamadı (fark yok, negatif ya da hedef dönemden sonra)" });
        if (target.Status == PayrollPeriodStatus.Calculated) target.Status = PayrollPeriodStatus.Open;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("RetroDiff", target.Id.ToString(), "Approved", new
        {
            count = applied.Count, target.Year, target.Month,
            items = applied.Select(a => new { a.EmployeeId, a.SourceYear, a.SourceMonth })
        }, target.TenantSlug);
        return Ok(new { applied = applied.Count, total = applied.Sum(a => a.DiffGross), targetPeriodId = target.Id });
    }

    [HttpGet("payroll/retro")]
    public async Task<IActionResult> RetroList([FromQuery] Guid? targetPeriodId, CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var q = _db.RetroDiffs.AsNoTracking();
        if (targetPeriodId is { } t) q = q.Where(r => r.TargetPeriodId == t);
        var list = await q.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        if (list.Count > 0) await AuditAsync("RetroDiff", targetPeriodId?.ToString() ?? "list", "SensitiveViewed", new { field = "retroDiffs", count = list.Count });
        return Ok(list);
    }

    /* ================================================================== kıdem ve ihbar (61) */

    public record SeveranceRequest(Guid EmployeeId, Guid? OffboardingCaseId, DateOnly? LastWorkingDay, string? Reason,
        decimal? RegularAdditionsMonthly, decimal? OtherBenefitsMonthly, bool? SeveranceEligible, bool? NoticePaid, decimal? UnusedLeaveDays);

    private sealed class OffRow { public Guid EmployeeId { get; set; } public string Reason { get; set; } = ""; public DateOnly LastWorkingDay { get; set; } }
    private sealed class LeaveRow { public int Year { get; set; } public decimal EntitledDays { get; set; } public decimal UsedDays { get; set; } public decimal CarriedOutDays { get; set; } }
    private sealed class EmpRow { public DateOnly HireDate { get; set; } public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; }

    private static readonly string[] Reasons = { "Resignation", "Termination", "Retirement", "ContractEnd", "Other" };

    /// <summary>Hesap girdilerini toplar: offboarding kaydı, işe giriş, ücret, giydirme önerisi, izin, kümülatif matrah, tavan.</summary>
    private async Task<(IActionResult? Error, SeveranceInput? Input, object? Sources)> SeveranceInputAsync(SeveranceRequest b, CancellationToken ct)
    {
        DateOnly? last = b.LastWorkingDay;
        var reason = b.Reason;
        if (b.OffboardingCaseId is { } oc)
        {
            var off = await _db.Database.SqlQueryRaw<OffRow>("""
                SELECT "EmployeeId", "Reason", "LastWorkingDay" FROM engagement_offboarding_cases WHERE "TenantSlug" = {0} AND "Id" = {1}
                """, Tenant, oc).FirstOrDefaultAsync(ct);
            if (off is null || off.EmployeeId != b.EmployeeId) return (NotFound(new { message = "Ayrılış kaydı bulunamadı" }), null, null);
            last ??= off.LastWorkingDay;
            reason ??= off.Reason;
        }
        if (last is null || reason is null || !Reasons.Contains(reason)) return (BadRequest(new { message = "Son iş günü ve geçerli ayrılış nedeni gerekli" }), null, null);
        var emp = await _db.Database.SqlQueryRaw<EmpRow>("""
            SELECT "HireDate", "FirstName", "LastName" FROM employee_employees WHERE "TenantSlug" = {0} AND "Id" = {1}
            """, Tenant, b.EmployeeId).FirstOrDefaultAsync(ct);
        if (emp is null) return (NotFound(new { message = "Çalışan bulunamadı" }), null, null);
        if (last < emp.HireDate) return (BadRequest(new { message = "Son iş günü işe giriş tarihinden önce olamaz" }), null, null);
        var recs = await _db.Records.AsNoTracking().Where(r => r.EmployeeId == b.EmployeeId).ToListAsync(ct);
        var rec = recs.Where(r => r.EffectiveFrom <= last.Value).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault()
                  ?? recs.OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        if (rec is null) return (BadRequest(new { message = "Çalışanın ücret kaydı yok", code = "no_salary" }), null, null);
        var closedIds = _db.PayrollPeriods.Where(p => p.Status == PayrollPeriodStatus.Closed).Select(p => p.Id);
        var lastSlips = await _db.Payslips.AsNoTracking().Where(s => s.EmployeeId == b.EmployeeId && closedIds.Contains(s.PeriodId))
            .OrderByDescending(s => s.Year).ThenByDescending(s => s.Month).Take(12).ToListAsync(ct);
        var suggestedAdditions = lastSlips.Count == 0 ? 0 : Math.Round(lastSlips.Average(s => s.Additions), 2);
        // İhbar GV'si: ayrılış yılının yalnızca KAPANMIŞ dönemleri (ayrılış ayına kadar) + devreden matrah.
        var exitYear = last.Value.Year;
        var yearSlips = await (from s in _db.Payslips.AsNoTracking()
                               join p in _db.PayrollPeriods.AsNoTracking() on s.PeriodId equals p.Id
                               where s.EmployeeId == b.EmployeeId && s.Year == exitYear
                               select new SeveranceCalculator.TaxBaseSlip(s.Year, s.Month, s.TaxBase, p.Status == PayrollPeriodStatus.Closed))
                              .ToListAsync(ct);
        var opening = await _db.OpeningTaxBases.AsNoTracking().Where(o => o.EmployeeId == b.EmployeeId && o.Year == exitYear)
            .Select(o => (decimal?)o.Amount).FirstOrDefaultAsync(ct) ?? 0;
        var prior = SeveranceCalculator.PriorTaxBase(yearSlips, last.Value, opening);
        // Kullanılmayan yıllık izin: ayrılış yılına kadar tüm yılların kalanı (devredilen kısım iki kez sayılmaz).
        var leaveRows = await _db.Database.SqlQueryRaw<LeaveRow>("""
            SELECT "Year", "EntitledDays", "UsedDays", "CarriedOutDays" FROM leave_balances
            WHERE "TenantSlug" = {0} AND "EmployeeId" = {1} AND "Type" = 'Annual'
            """, Tenant, b.EmployeeId).ToListAsync(ct);
        var leave = SeveranceCalculator.UnusedAnnualLeave(
            leaveRows.Select(r => new SeveranceCalculator.AnnualLeaveRow(r.Year, r.EntitledDays, r.UsedDays, r.CarriedOutDays)), exitYear);
        // Ürün kararı: GV dilimleri, damga oranı ve kıdem tavanı kullanıcının girdiği parametrelerden; yoksa hesap yapılmaz.
        var paramRows = await _db.PayrollParameters.AsNoTracking().Where(p => p.Year == last.Value.Year).ToListAsync(ct);
        var prm = PayrollParameterResolver.Resolve(paramRows, last.Value.Year, last.Value.Month);
        if (prm is null) return (Conflict(PayrollParameterResolver.Missing(last.Value.Year, last.Value.Month)), null, null);
        if (PayrollParameterResolver.SeveranceCeilingOrNull(paramRows, last.Value) is not { } ceiling)
            return (Conflict(PayrollParameterResolver.SeveranceCeilingMissing(last.Value)), null, null);
        if (b.RegularAdditionsMonthly is < 0 || b.OtherBenefitsMonthly is < 0 || b.UnusedLeaveDays is < 0 or > 365)
            return (BadRequest(new { message = "Girdi değerleri geçersiz" }), null, null);
        var input = new SeveranceInput(emp.HireDate, last.Value, reason, rec.BaseSalary, b.RegularAdditionsMonthly ?? suggestedAdditions,
            b.OtherBenefitsMonthly ?? 0, ceiling, b.SeveranceEligible, b.NoticePaid, b.UnusedLeaveDays ?? Math.Max(0, leave), prior, prm.Brackets, prm.StampTaxRate);
        var sources = new
        {
            suggestedAdditions, payslipsUsed = lastSlips.Count, ceiling, ceilingFromParameters = true, remainingLeave = leave,
            priorCumulativeTaxBase = prior, openingTaxBase = opening, currency = rec.Currency, employee = $"{emp.FirstName} {emp.LastName}"
        };
        return (null, input, sources);
    }

    private static object ResultView(SeveranceInput i, SeveranceResult r, object? sources) => new
    {
        input = new
        {
            i.HireDate, i.LastWorkingDay, i.Reason, i.MonthlyBaseGross, i.RegularAdditionsMonthly, i.OtherBenefitsMonthly, i.SeveranceCeiling,
            severanceEligibleOverride = i.SeveranceEligibleOverride, noticePaidOverride = i.NoticePayOverride, i.UnusedLeaveDays, i.PriorCumulativeTaxBase
        },
        result = r, sources,
        basis = new
        {
            severance = "1475 s. Kanun m.14 (4857 s. Kanun geçici m.6): her tam yıl için 30 günlük giydirilmiş brüt ücret, kıdem tavanıyla sınırlı; gelir vergisinden istisna (GVK m.25/7), damga vergisine tabi.",
            notice = "4857 s. İş Kanunu m.17: 6 aydan az 2, 6 ay–1,5 yıl 4, 1,5–3 yıl 6, 3 yıldan fazla 8 hafta; SGK primine tabi değil, gelir ve damga vergisine tabi.",
            leave = "4857 s. İş Kanunu m.59: kullanılmayan yıllık izin ücreti son bordroda ek ödeme olarak işlenir (SGK, GV ve damga orada hesaplanır).",
        },
        disclaimer = "Hesap bir öneridir; kesin tutarı bordro yetkilisi onaylar (otomatik karar yok). Ek ödemeler ve yan haklar giydirilmiş ücrete elle eklenebilir.",
    };

    [HttpPost("severance/preview")]
    public async Task<IActionResult> SeverancePreview([FromBody] SeveranceRequest body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var (err, input, sources) = await SeveranceInputAsync(body, ct);
        if (err is not null) return err;
        var r = SeveranceCalculator.Compute(input!);
        await AuditAsync("SeveranceCalc", body.EmployeeId.ToString(), "SensitiveViewed", new { field = "severancePreview" });
        return Ok(ResultView(input!, r, sources));
    }

    [HttpPost("severance")]
    public async Task<IActionResult> SeveranceSave([FromBody] SeveranceRequest body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var (err, input, sources) = await SeveranceInputAsync(body, ct);
        if (err is not null) return err;
        var r = SeveranceCalculator.Compute(input!);
        var c = new SeveranceCalc
        {
            EmployeeId = body.EmployeeId, OffboardingCaseId = body.OffboardingCaseId, HireDate = input!.HireDate, LastWorkingDay = input.LastWorkingDay,
            Reason = input.Reason, InputJson = JsonSerializer.Serialize(body, Json), ResultJson = JsonSerializer.Serialize(ResultView(input, r, sources), Json),
            TotalNet = r.TotalNet, PreparedBy = UserId, PreparedByName = UserName,
        };
        _db.SeveranceCalcs.Add(c);
        await _db.SaveChangesAsync(ct);
        await AuditAsync("SeveranceCalc", c.Id.ToString(), "Created", new { c.EmployeeId, c.LastWorkingDay, c.Reason }, c.TenantSlug);
        return Ok(SeveranceView(c));
    }

    private static object SeveranceView(SeveranceCalc c) => new
    {
        c.Id, c.EmployeeId, c.OffboardingCaseId, c.HireDate, c.LastWorkingDay, c.Reason, c.TotalNet, c.Status, c.PreparedByName, c.DecidedByName,
        c.DecisionNote, c.DecidedAt, c.CreatedAt, detail = JsonDocument.Parse(c.ResultJson).RootElement.Clone(),
    };

    [HttpGet("severance")]
    public async Task<IActionResult> SeveranceList([FromQuery] Guid? employeeId, CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var q = _db.SeveranceCalcs.AsNoTracking();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        var list = await q.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
        if (list.Count > 0) await AuditAsync("SeveranceCalc", employeeId?.ToString() ?? "list", "SensitiveViewed", new { field = "severanceList", count = list.Count });
        return Ok(list.Select(SeveranceView));
    }

    public record DecideInput(bool Approve, string? Note);

    /// <summary>
    /// Onay/ret (otomatik karar yok). Dört göz: hazırlayan kendi hesabını onaylayamaz (görevler ayrılığı
    /// kuralı kiracıda kapatılmışsa izin verilir). Ret gerekçesi zorunlu.
    /// </summary>
    [HttpPost("severance/{id:guid}/decide")]
    public async Task<IActionResult> SeveranceDecide(Guid id, [FromBody] DecideInput body, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var c = await _db.SeveranceCalcs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Draft") return Conflict(new { message = "Hesap zaten karara bağlanmış" });
        if (c.PreparedBy == UserId && await SodEnforcedAsync(ct))
            return Conflict(new { message = "Görevler ayrılığı: hazırladığınız hesabı başka bir bordro yetkilisi onaylamalı.", code = SegregationOfDuties.SameUserCode });
        if (c.EmployeeId == await MyEmployeeIdAsync(ct)) return StatusCode(403, new { message = "Kendi hesabınıza karar veremezsiniz" });
        if (!body.Approve && string.IsNullOrWhiteSpace(body.Note)) return BadRequest(new { message = "Ret gerekçesi yazın" });
        c.Status = body.Approve ? "Approved" : "Rejected";
        c.DecidedBy = UserId; c.DecidedByName = UserName; c.DecidedAt = DateTimeOffset.UtcNow;
        c.DecisionNote = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()[..Math.Min(body.Note.Trim().Length, 300)];
        await _db.SaveChangesAsync(ct);
        await AuditAsync("SeveranceCalc", c.Id.ToString(), c.Status, new { c.EmployeeId }, c.TenantSlug);
        return Ok(SeveranceView(c));
    }

    private sealed class DocPerson { public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; public DateOnly HireDate { get; set; } public string? PositionTitle { get; set; } }

    /// <summary>
    /// Onaylı hesaptan ibraname (HTML): şirketin "İbraname" belge şablonu (governance › Belge şablonları;
    /// yalnızca OKUNUR) yoksa varsayılan metin. Ücret içerdiği için dışa aktarma kaydına yazılır.
    /// </summary>
    [HttpGet("severance/{id:guid}/document")]
    public async Task<IActionResult> SeveranceDocument(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var c = await _db.SeveranceCalcs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status != "Approved") return Conflict(new { message = "Belge yalnızca onaylı hesaptan üretilir", code = "not_approved" });
        var detail = JsonDocument.Parse(c.ResultJson).RootElement;
        var r = detail.GetProperty("result").Deserialize<SeveranceResult>(Json)!;
        var template = await _db.Database.SqlQueryRaw<string>("""
            SELECT "Body" AS "Value" FROM governance_doc_templates WHERE "TenantSlug" = {0} AND lower("Name") = lower({1}) LIMIT 1
            """, Tenant, "İbraname").FirstOrDefaultAsync(ct) ?? SeveranceCalculator.DefaultReleaseTemplate;
        var p = await _db.Database.SqlQueryRaw<DocPerson>("""
            SELECT e."FirstName", e."LastName", e."HireDate", a."PositionTitle" FROM employee_employees e
            LEFT JOIN LATERAL (SELECT x."PositionTitle" FROM employee_assignments x WHERE x."EmployeeId" = e."Id"
                               ORDER BY (x."EffectiveTo" IS NULL) DESC, x."EffectiveFrom" DESC LIMIT 1) a ON true
            WHERE e."TenantSlug" = {0} AND e."Id" = {1}
            """, Tenant, c.EmployeeId).FirstOrDefaultAsync(ct);
        var company = (await _db.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM platform_tenants WHERE \"Slug\" = {0}", Tenant).FirstOrDefaultAsync(ct)) ?? Tenant;
        var vars = SeveranceCalculator.Variables(r, c.LastWorkingDay, SeveranceCalculator.ReasonTr(c.Reason));
        vars["sirket.ad"] = company;
        vars["calisan.adSoyad"] = p is null ? "" : $"{p.FirstName} {p.LastName}";
        vars["calisan.ad"] = p?.FirstName ?? "";
        vars["calisan.iseGiris"] = (p?.HireDate ?? c.HireDate).ToString("dd.MM.yyyy");
        vars["calisan.pozisyon"] = p?.PositionTitle ?? "";
        vars["bugun"] = DateTime.UtcNow.AddHours(3).ToString("dd.MM.yyyy");
        var html = Regex.Replace(template, @"\{\{\s*([\w.]+)\s*\}\}", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? WebUtility.HtmlEncode(v) : "");
        var trace = CompensationService.Auditing.TraceCode.New();
        await AuditAsync("SeveranceCalc", c.Id.ToString(), "Exported", new { field = "releaseDocument", c.EmployeeId, traceCode = trace }, c.TenantSlug);
        Response.Headers["X-HR360-Trace"] = trace;
        return Ok(new { html, traceCode = trace, sha256 = EPayslip.Sha256(html) });
    }

    /* ================================================================== e-bordro (59) */

    private async Task NotifyAsync(Guid employeeId, string subject, string body, string channel, string? email, CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("""
                INSERT INTO notification_messages ("Id","TenantSlug","RecipientEmployeeId","RecipientEmail","Channel","TemplateCode","Subject","Body","Status","AttemptCount","CreatedAt")
                VALUES ({0},{1},{2},{3},{4},'compensation.epayslip',{5},{6},'Pending',0,now())
                """, Guid.NewGuid(), Tenant, employeeId, (object?)email ?? DBNull.Value, channel, subject, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<PayrollAudit>>()?.LogWarning(ex, "e-bordro bildirimi yazılamadı");
        }
    }

    private sealed class EmailRow { public Guid Id { get; set; } public string? Email { get; set; } }

    /// <summary>
    /// Kapanmış dönemin pusulalarını e-bordro olarak yayımlar: içerik özeti (SHA-256) ve şifreli kopya saklanır,
    /// çalışana uygulama içi ve (kayıtlı e-postası varsa) e-posta bildirimi gider. Bildirimde TUTAR YAZMAZ;
    /// pusula yalnızca uygulamada görülür. Dönem yeniden açılıp pusula değiştiyse yeniden yayımlanır (onay sıfırlanır).
    /// </summary>
    [HttpPost("payroll/periods/{id:guid}/e-payslips/publish")]
    public async Task<IActionResult> PublishEPayslips(Guid id, CancellationToken ct)
    {
        if (!IsPayrollAdmin) return Forbid();
        var period = await _db.PayrollPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (period is null) return NotFound();
        if (period.Status != PayrollPeriodStatus.Closed) return BadRequest(new { message = "e-Bordro yalnızca kapanmış dönem için yayımlanır", code = "period_not_closed" });
        var slips = await _db.Payslips.AsNoTracking().Where(s => s.PeriodId == id).ToListAsync(ct);
        var existing = await _db.PayslipDeliveries.Where(d => d.PeriodId == id).ToDictionaryAsync(d => d.PayslipId, ct);
        var empIds = slips.Select(s => s.EmployeeId).ToArray();
        var emails = (await _db.Database.SqlQueryRaw<EmailRow>(
            "SELECT \"Id\", \"Email\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = ANY({1})", Tenant, empIds).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Email);
        int created = 0, republished = 0, unchanged = 0;
        var label = $"{period.Year}/{period.Month:00}";
        foreach (var s in slips)
        {
            var canonical = EPayslip.Canonical(s);
            var hash = EPayslip.Sha256(canonical);
            byte[]? sealedCopy = ExportCrypto.Enabled ? ExportCrypto.Encrypt(Encoding.UTF8.GetBytes(canonical)) : null;
            if (existing.TryGetValue(s.Id, out var d))
            {
                if (d.ContentSha256 == hash) { unchanged++; continue; }
                d.ContentSha256 = hash; d.SealedCopy = sealedCopy; d.PublishedAt = DateTimeOffset.UtcNow; d.PublishedBy = UserName;
                d.AcknowledgedAt = null; d.AcknowledgedSha256 = null; d.AckIpPrefix = null; d.FirstOpenedAt = null; d.LastOpenedAt = null; d.OpenCount = 0;
                republished++;
            }
            else
            {
                d = new PayslipDelivery { PayslipId = s.Id, PeriodId = id, EmployeeId = s.EmployeeId, ContentSha256 = hash, SealedCopy = sealedCopy, PublishedBy = UserName };
                _db.PayslipDeliveries.Add(d);
                created++;
            }
            var email = emails.GetValueOrDefault(s.EmployeeId);
            d.EmailQueued = !string.IsNullOrWhiteSpace(email);
            const string body = "Bordro pusulanız HR360'ta yayımlandı. Bordrolarım ekranından görüntüleyip \"Okudum, teslim aldım\" ile onaylayabilirsiniz. Güvenliğiniz için tutarlar bildirimde yer almaz.";
            await NotifyAsync(s.EmployeeId, $"e-Bordro: {label}", body, "InApp", null, ct);
            if (d.EmailQueued) await NotifyAsync(s.EmployeeId, $"e-Bordro: {label}", body, "Email", email, ct);
        }
        await _db.SaveChangesAsync(ct);
        await AuditAsync("PayslipDelivery", id.ToString(), "Published", new { period.Year, period.Month, created, republished, unchanged }, period.TenantSlug);
        return Ok(new { created, republished, unchanged });
    }

    [HttpGet("payroll/periods/{id:guid}/e-payslips")]
    public async Task<IActionResult> EPayslipStatus(Guid id, CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var list = await _db.PayslipDeliveries.AsNoTracking().Where(d => d.PeriodId == id).ToListAsync(ct);
        var slips = await _db.Payslips.AsNoTracking().Where(s => s.PeriodId == id).ToListAsync(ct);
        var hashes = slips.ToDictionary(s => s.Id, EPayslip.Hash);
        return Ok(new
        {
            payslips = slips.Count, published = list.Count, opened = list.Count(d => d.FirstOpenedAt != null), acknowledged = list.Count(d => d.AcknowledgedAt != null),
            items = list.Select(d => new
            {
                d.EmployeeId, d.PayslipId, d.PublishedAt, d.PublishedBy, d.EmailQueued, d.FirstOpenedAt, d.LastOpenedAt, d.OpenCount, d.AcknowledgedAt,
                integrity = hashes.TryGetValue(d.PayslipId, out var h) ? (h == d.ContentSha256 ? "ok" : "changed") : "missing",
            }),
        });
    }

    /// <summary>Teslim edilen şifreli kopyanın özeti kayıtlı özetle aynı mı (kopya yoksa null).</summary>
    private static bool? SealOk(PayslipDelivery d)
    {
        if (d.SealedCopy is null || !ExportCrypto.Enabled) return null;
        try { return EPayslip.Sha256Bytes(ExportCrypto.Decrypt(d.SealedCopy)) == d.ContentSha256; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Pusulanın e-bordro durumu. Çalışan kendi pusulasını açtığında "okundu" (ilk/son açılma, sayaç) kaydedilir;
    /// bordro yetkilisinin görüntülemesi okundu saymaz.
    /// </summary>
    [HttpGet("payslips/{id:guid}/e-payslip")]
    public async Task<IActionResult> EPayslipOf(Guid id, CancellationToken ct)
    {
        var s = await _db.Payslips.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        var me = await MyEmployeeIdAsync(ct);
        var own = me == s.EmployeeId;
        if (!own && !IsPayrollViewer) return NotFound();
        var periodClosed = await _db.PayrollPeriods.AnyAsync(p => p.Id == s.PeriodId && p.Status == PayrollPeriodStatus.Closed, ct);
        if (own && !periodClosed) return NotFound();
        var d = await _db.PayslipDeliveries.FirstOrDefaultAsync(x => x.PayslipId == id, ct);
        var current = EPayslip.Hash(s);
        if (d is not null && own)
        {
            var now = DateTimeOffset.UtcNow;
            d.FirstOpenedAt ??= now; d.LastOpenedAt = now; d.OpenCount++;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new
        {
            published = d is not null, d?.PublishedAt, contentSha256 = d?.ContentSha256, currentSha256 = current,
            integrity = d is null ? "not_published" : d.ContentSha256 == current ? "ok" : "changed",
            sealVerified = d is null ? null : SealOk(d), d?.FirstOpenedAt, d?.AcknowledgedAt, canAcknowledge = own && periodClosed && d is not null && d.AcknowledgedAt is null && d.ContentSha256 == current,
        });
    }

    /// <summary>"Okudum, teslim aldım": yalnızca çalışanın kendisi; onaylanan içerik özeti, zaman ve IP /24 öneki kaydedilir.</summary>
    [HttpPost("payslips/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var s = await _db.Payslips.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        if (await MyEmployeeIdAsync(ct) != s.EmployeeId) return NotFound();
        // Dönem yeniden açıldıysa pusula kesin değildir (yeniden hesaplanabilir): teslim alınamaz.
        if (!await _db.PayrollPeriods.AnyAsync(p => p.Id == s.PeriodId && p.Status == PayrollPeriodStatus.Closed, ct))
            return Conflict(new { message = "Bordro dönemi yeniden açıldı; dönem kapanıp pusula yeniden yayımlanınca teslim alabilirsiniz", code = "period_not_closed" });
        var d = await _db.PayslipDeliveries.FirstOrDefaultAsync(x => x.PayslipId == id, ct);
        if (d is null) return Conflict(new { message = "Bu pusula henüz e-bordro olarak yayımlanmadı", code = "not_published" });
        if (d.AcknowledgedAt is not null) return Conflict(new { message = "Pusula zaten teslim alındı", code = "already_acknowledged" });
        var current = EPayslip.Hash(s);
        if (current != d.ContentSha256) return Conflict(new { message = "Pusula yayımlandıktan sonra değişti; bordro yetkilisi yeniden yayımlamalı", code = "changed" });
        d.AcknowledgedAt = DateTimeOffset.UtcNow; d.AcknowledgedSha256 = current;
        d.AckIpPrefix = EPayslip.IpPrefix(Request.Headers["X-Real-IP"].FirstOrDefault() ?? HttpContext.Connection.RemoteIpAddress?.ToString());
        d.FirstOpenedAt ??= d.AcknowledgedAt;
        await _db.SaveChangesAsync(ct);
        await AuditAsync("PayslipDelivery", d.Id.ToString(), "Acknowledged", new { s.Year, s.Month, sha256 = current, ipPrefix = d.AckIpPrefix }, d.TenantSlug);
        return Ok(new { d.AcknowledgedAt, contentSha256 = current });
    }

    /* ================================================================== ücret bantları (65) */

    private sealed record BandRow(Guid EmployeeId, string Name, string? Department, string? PositionTitle, string? Grade, decimal Salary, string Currency,
        SalaryBand? Band, decimal? CompaRatio, string Position);

    private async Task<List<BandRow>> BandRowsAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var people = (await PayrollData.PeopleAsync(_db, Tenant, ct)).Where(p => p.Status != "Terminated").ToDictionary(p => p.Id);
        var recs = await _db.Records.AsNoTracking().Where(r => r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today)).ToListAsync(ct);
        var current = recs.GroupBy(r => r.EmployeeId).Select(g => g.OrderByDescending(r => r.EffectiveFrom).First()).Where(r => people.ContainsKey(r.EmployeeId)).ToList();
        var bands = await _db.SalaryBands.AsNoTracking().ToListAsync(ct);
        return current.Select(r =>
        {
            var p = people[r.EmployeeId];
            var band = CompaRatio.BandFor(bands, r.Grade, r.Currency, today);
            var compa = CompaRatio.Ratio(r.BaseSalary, band);
            return new BandRow(r.EmployeeId, $"{p.FirstName} {p.LastName}", p.Department, p.PositionTitle, r.Grade, r.BaseSalary, r.Currency, band, compa,
                CompaRatio.Position(r.BaseSalary, band));
        }).ToList();
    }

    /// <summary>Çalışan bazında compa-ratio (ücret / bant ortası). Yalnızca İK ve ücret görme izni; erişim kaydına yazılır.</summary>
    [HttpGet("bands/compa-ratios")]
    public async Task<IActionResult> CompaRatios(CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var rows = await BandRowsAsync(ct);
        await AuditAsync("CompensationRecord", "compa-ratio", "SensitiveViewed", new { field = "compaRatio", count = rows.Count });
        return Ok(rows.OrderBy(r => r.Department).ThenBy(r => r.Name).Select(r => new
        {
            r.EmployeeId, r.Name, r.Department, r.PositionTitle, r.Grade, r.Salary, r.Currency, r.CompaRatio, r.Position,
            band = r.Band is null ? null : new { r.Band.Id, r.Band.Grade, r.Band.Title, r.Band.MinAmount, r.Band.MidAmount, r.Band.MaxAmount, r.Band.Currency, r.Band.Year, r.Band.EffectiveFrom },
        }));
    }

    /// <summary>
    /// Bant kapsama raporu: bant ve bölüm bazında bandın altı/içi/üstü sayıları. KVKK: 5 kişiden az gruplarda
    /// sayılar ve ortalama gizlenir (bireysel ücret çıkarılamasın). Kişi bazlı veri içermez.
    /// </summary>
    [HttpGet("bands/coverage")]
    public async Task<IActionResult> BandCoverage(CancellationToken ct)
    {
        if (!IsPayrollViewer) return Forbid();
        var rows = await BandRowsAsync(ct);
        var report = CompaRatio.Coverage(rows.Select(r => new CompaRatio.Item(r.Band is null ? null : $"{r.Band.Grade} ({r.Band.Year})", r.Department, r.CompaRatio, r.Position)).ToList());
        await AuditAsync("CompensationRecord", "band-coverage", "SensitiveViewed", new { field = "bandCoverage", count = rows.Count });
        return Ok(report);
    }
}
