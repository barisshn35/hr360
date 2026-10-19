using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;

namespace ExpenseService.Controllers;

[ApiController]
[Route("api/expense-claims")]
[Authorize]
public class ExpenseClaimsController : ControllerBase
{
    private readonly ExpenseDbContext _db;
    private readonly ApprovalWorkflowClient _approvals;
    private readonly FxService _fx;
    private readonly ExpenseService.Tenancy.ITenantContext _tenant;
    private readonly ExpenseAnomalyClient? _anomaly;
    public ExpenseClaimsController(ExpenseDbContext db, ApprovalWorkflowClient approvals, FxService fx, ExpenseService.Tenancy.ITenantContext tenant,
        ExpenseAnomalyClient? anomaly = null)
    {
        _anomaly = anomaly;
        _db = db;
        _approvals = approvals;
        _fx = fx;
        _tenant = tenant;
    }

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin");

    /// <summary>
    /// Tum beyanlari okuyabilen roller. Odendi isaretleyen (muhasebe veya "odeme
    /// isaretleme" ek izni olan) kisi, isaretleyecegi beyanlari gorebilmeli.
    /// </summary>
    private bool CanReadAll => IsHr || User.IsInRole("manager") || User.IsInRole("accounting")
        || User.IsInRole("ext-expense-markPaid") || User.IsInRole("ext-expense-manage");

    /// <summary>Muhasebe/ödeme rolleri: gönderilmiş tüm beyanları görür (taslakları değil).</summary>
    private bool IsFinance => User.IsInRole("accounting") || User.IsInRole("ext-expense-markPaid")
        || User.IsInRole("ext-expense-manage");

    /// <summary>
    /// Okuma kapsamı. İK: hepsi. Muhasebe: gönderilmiş hepsi + kendi taslakları. Yönetici:
    /// ekibi (başı olduğu departmanlarda bugün geçerli ataması olan aktif çalışanlar) ve
    /// kendisi. Taslaklar (Draft) yalnızca sahibine ve İK'ya görünür — önceden yönetici
    /// çalışanın gönderilmemiş taslaklarını da görüyordu.
    /// </summary>
    private sealed record ReadScope(Guid? Me, HashSet<Guid>? Employees);

    private async Task<ReadScope?> ReadScopeAsync(CancellationToken ct)
    {
        if (IsHr) return null;
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (IsFinance) return new ReadScope(me, null);
        var visible = new HashSet<Guid>();
        if (me is not null)
        {
            visible.Add(me.Value);
            if (User.IsInRole("manager"))
                foreach (var id in await _db.Database.SqlQueryRaw<Guid>(
                    """
                    SELECT DISTINCT a."EmployeeId" AS "Value" FROM employee_assignments a
                    JOIN organization_departments d ON d."Id" = a."DepartmentId"
                    JOIN employee_employees e ON e."Id" = a."EmployeeId"
                    WHERE a."TenantSlug" = {0} AND d."HeadEmployeeId" = {1} AND e."Status" <> 'Terminated'
                      AND a."EffectiveFrom" <= current_date AND (a."EffectiveTo" IS NULL OR a."EffectiveTo" >= current_date)
                    """, _db.CurrentTenantSlug ?? "", me.Value).ToListAsync(ct))
                    visible.Add(id);
        }
        return new ReadScope(me, visible);
    }

    private static bool Visible(ReadScope? scope, ExpenseClaim c) =>
        scope is null
        || ((scope.Employees is null || scope.Employees.Contains(c.EmployeeId))
            && (c.Status != ClaimStatus.Draft || c.EmployeeId == scope.Me));

    /// <summary>
    /// Masraf beyanlarini listeler.
    ///
    /// GUVENLIK: Onceden employeeId filtresi serbestti; bir calisan baskasinin
    /// beyanlarini (tutar, kalemler), filtresiz cagirarak da tum sirketinkileri
    /// gorebiliyordu. Simdi:
    ///   - Yonetici, IK, muhasebe ve ilgili ek izinler: istedigi employeeId'yi
    ///     (ya da hicbirini) sorgulayabilir.
    ///   - Calisan: yalnizca kendi beyanlarini gorur; employeeId vermezse kendisine
    ///     sabitlenir, baska birininkini verirse 403 doner.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? employeeId, [FromQuery] ClaimStatus? status, CancellationToken ct)
    {
        if (!CanReadAll)
        {
            var myEmployeeId = await _approvals.FindMyEmployeeIdAsync(ct);
            if (myEmployeeId is null) return Forbid();
            if (employeeId.HasValue && employeeId.Value != myEmployeeId.Value) return Forbid();
            employeeId = myEmployeeId;
        }

        var q = _db.Claims.Include(c => c.Items).AsQueryable();
        if (CanReadAll && await ReadScopeAsync(ct) is { } scope)
        {
            if (scope.Employees is { } emps)
            {
                if (employeeId.HasValue && !emps.Contains(employeeId.Value)) return Forbid();
                var ids = emps.ToList();
                q = q.Where(c => ids.Contains(c.EmployeeId));
            }
            var mine = scope.Me;
            q = q.Where(c => c.Status != ClaimStatus.Draft || c.EmployeeId == mine);
        }
        if (employeeId.HasValue) q = q.Where(c => c.EmployeeId == employeeId.Value);
        if (status.HasValue) q = q.Where(c => c.Status == status.Value);
        return Ok(await q.OrderByDescending(c => c.CreatedAt).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var c = await _db.Claims.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        // GUVENLIK: Onceden herhangi bir calisan, kimligini bildigi her beyani
        // okuyabiliyordu (liste ucundaki sahiplik kurali burada yoktu).
        if (!CanReadAll)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != c.EmployeeId) return NotFound();
        }
        else if (!Visible(await ReadScopeAsync(ct), c)) return NotFound();
        return Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClaimRequest request, CancellationToken ct)
    {
        // GUVENLIK: EmployeeId istek govdesinden geliyordu - herkes baskasi adina
        // beyan acabiliyordu. IK disindakiler (muhasebe dahil) yalnizca kendi adina
        // acabilir; baskasi adina acilan taslagi onaya yalnizca sahibi ya da IK gonderebilir.
        if (!IsHr)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (request.EmployeeId != me.Value)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca kendi adınıza masraf beyanı oluşturabilirsiniz" });
        }
        var (error, claim) = await CreateCoreAsync(_db, _fx, _tenant.TenantSlug, request, ct);
        if (error is not null) return error;
        return CreatedAtAction(nameof(GetById), new { id = claim!.Id }, claim);
    }

    /// <summary>
    /// Taslak beyan oluşturmanın ortak çekirdeği: web ucu (<see cref="Create"/>) ve sohbet
    /// botunun iç ucu (InternalChatController) aynı doğrulamayı (para birimi, kalem sayısı,
    /// gelecek tarih, tutar sınırı, km/döviz hesabı) buradan geçirir. Yetki kontrolü çağırandadır.
    /// </summary>
    [NonAction]
    public static async Task<(IActionResult? Error, ExpenseClaim? Claim)> CreateCoreAsync(
        ExpenseDbContext db, FxService fx, string? tenantSlug, CreateClaimRequest request, CancellationToken ct)
    {
        var (error, currency, items) = await BuildItemsAsync(db, fx, tenantSlug, request.Title, request.Currency, request.Items, ct);
        if (error is not null) return (error, null);

        var claim = new ExpenseClaim
        {
            EmployeeId = request.EmployeeId,
            Title = request.Title.Trim(),
            Currency = currency!
        };
        claim.Items.AddRange(items!);
        claim.TotalAmount = claim.Items.Sum(i => i.Amount);
        db.Claims.Add(claim);
        await db.SaveChangesAsync(ct);
        return (null, claim);
    }

    /// <summary>
    /// Başlık, para birimi ve kalemleri doğrular; kalem varlıklarını (km/döviz hesabı
    /// yapılmış hâlde) üretir. Oluşturma ve taslak düzenleme aynı kuralları kullanır.
    /// </summary>
    private static async Task<(IActionResult? Error, string? Currency, List<ExpenseItem>? Items)> BuildItemsAsync(
        ExpenseDbContext db, FxService fx, string? tenantSlug, string? title, string? currencyRaw,
        List<ExpenseItemInput>? inputs, CancellationToken ct)
    {
        static IActionResult BadRequest(object message) => new BadRequestObjectResult(message);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            return (BadRequest("Başlık zorunlu ve en fazla 200 karakter olabilir"), null, null);
        var currency = (currencyRaw ?? "").Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Z]{3}$"))
            return (BadRequest("Para birimi 3 harfli ISO kodu olmalı (örn. TRY)"), null, null);
        if (inputs is null || inputs.Count == 0 || inputs.Count > 50)
            return (BadRequest("Beyanda 1-50 arası kalem olmalı"), null, null);
        var latestAllowed = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        if (inputs.Any(i => i.ExpenseDate > latestAllowed))
            return (BadRequest("Harcama tarihi gelecekte olamaz"), null, null);
        if (inputs.Any(i => i.Amount > 1_000_000m))
            return (BadRequest("Kalem tutarı en fazla 1.000.000 olabilir"), null, null);

        var items = new List<ExpenseItem>();
        var policy = await db.Policies.AsNoTracking().FirstOrDefaultAsync(ct);
        foreach (var item in inputs)
        {
            var amount = item.Amount;
            decimal? rate = null;
            string? origCur = null;
            decimal? km = null;
            // G9: kilometre masrafı km × kiracının km ücreti; yabancı para harcama günündeki TCMB kuruyla TL'ye çevrilir.
            if (item.Category == ExpenseCategory.Mileage)
            {
                if (item.Km is not (> 0 and <= 10000)) return (BadRequest("Kilometre 0–10000 arasında olmalı"), null, null);
                km = item.Km;
                amount = Math.Round(item.Km.Value * (policy?.KmRate ?? new ExpensePolicy().KmRate), 2);
            }
            else if (!string.IsNullOrWhiteSpace(item.OriginalCurrency) && item.OriginalCurrency.ToUpperInvariant() != currency)
            {
                if (currency != "TRY") return (BadRequest("Yabancı para kalemi yalnızca TL beyanda kullanılabilir"), null, null);
                if (item.OriginalAmount is not > 0) return (BadRequest("Yabancı para tutarı gerekli"), null, null);
                origCur = item.OriginalCurrency.Trim().ToUpperInvariant();
                var fxRate = await fx.RateAsync(db, tenantSlug, origCur, item.ExpenseDate, ct);
                if (fxRate is null) return (BadRequest(new { message = $"{origCur} için {item.ExpenseDate:dd.MM.yyyy} kuru bulunamadı; İK elle kur girebilir.", code = "fx_unavailable" }), null, null);
                rate = fxRate.Value.Rate;
                amount = Math.Round(item.OriginalAmount.Value * rate.Value, 2);
            }
            if (amount <= 0) return (BadRequest("Kalem tutari sifirdan buyuk olmali"), null, null);
            var (invoiceError, supplier, invoiceNo, ettn) = NormalizeInvoiceFields(item);
            if (invoiceError is not null) return (BadRequest(new { message = invoiceError }), null, null);
            if (item.VatAmount is < 0 || item.VatAmount > amount) return (BadRequest(new { message = "KDV tutarı 0 ile kalem tutarı arasında olmalı" }), null, null);
            items.Add(new ExpenseItem
            {
                Category = item.Category,
                Amount = amount,
                ExpenseDate = item.ExpenseDate,
                Description = item.Description,
                ReceiptStorageKey = item.ReceiptStorageKey,
                OriginalCurrency = origCur,
                OriginalAmount = origCur is null ? null : item.OriginalAmount,
                FxRate = rate,
                Km = km,
                TravelRequestId = item.TravelRequestId,
                SupplierTaxId = supplier,
                InvoiceNo = invoiceNo,
                Ettn = ettn,
                VatAmount = item.VatAmount,
            });
        }
        return (null, currency, items);
    }

    /// <summary>
    /// Gönderilecek beyandaki ETTN'lerden engellenecek olanlar: kiracıda reddedilmemiş başka bir beyanda
    /// kullanılmış olanlar ve aynı beyanda birden fazla kalemde geçenler (küçük harfe normalize edilir).
    /// </summary>
    [NonAction]
    public static List<string> DuplicateEttns(IEnumerable<string?> claimEttns, IEnumerable<string> usedElsewhere)
    {
        var used = usedElsewhere.Select(e => e.Trim().ToLowerInvariant()).ToHashSet();
        var mine = claimEttns.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!.Trim().ToLowerInvariant()).ToList();
        return mine.Where(e => used.Contains(e)).Concat(mine.GroupBy(e => e).Where(g => g.Count() > 1).Select(g => g.Key))
            .Distinct().OrderBy(e => e).ToList();
    }

    /// <summary>
    /// e-Fatura alanlarını (karekoddan doldurulur ya da elle girilir) doğrular ve normalize eder:
    /// VKN/TCKN 10-11 hane, fatura no en fazla 32 harf/rakam, ETTN geçerli UUID (küçük harf).
    /// </summary>
    [NonAction]
    public static (string? Error, string? Supplier, string? InvoiceNo, string? Ettn) NormalizeInvoiceFields(ExpenseItemInput item)
    {
        string? supplier = string.IsNullOrWhiteSpace(item.SupplierTaxId) ? null : item.SupplierTaxId.Trim();
        if (supplier is not null && !System.Text.RegularExpressions.Regex.IsMatch(supplier, @"^\d{10,11}$"))
            return ("Tedarikçi VKN/TCKN 10 ya da 11 haneli olmalı", null, null, null);
        string? no = string.IsNullOrWhiteSpace(item.InvoiceNo) ? null : item.InvoiceNo.Trim().ToUpperInvariant();
        if (no is not null && !System.Text.RegularExpressions.Regex.IsMatch(no, @"^[A-Z0-9]{1,32}$"))
            return ("Fatura numarası en fazla 32 harf/rakam olmalı", null, null, null);
        string? ettn = null;
        if (!string.IsNullOrWhiteSpace(item.Ettn))
        {
            if (!Guid.TryParse(item.Ettn.Trim(), out var g)) return ("ETTN geçerli değil", null, null, null);
            ettn = g.ToString();
        }
        return (null, supplier, no, ettn);
    }

    /// <summary>
    /// Taslak beyanı düzenler (başlık + kalemlerin tamamı yeniden yazılır). Yalnızca Draft
    /// durumundaki beyan; yalnızca sahibi ya da İK. Doğrulama oluşturmayla aynıdır.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClaimRequest request, CancellationToken ct)
    {
        var claim = await _db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null) return NotFound();
        if (!IsHr)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != claim.EmployeeId) return NotFound();
        }
        if (claim.Status != ClaimStatus.Draft)
            return Conflict(new { message = "Yalnızca taslak durumundaki beyan düzenlenebilir" });

        var (error, currency, items) = await BuildItemsAsync(_db, _fx, _tenant.TenantSlug, request.Title, request.Currency, request.Items, ct);
        if (error is not null) return error;

        claim.Title = request.Title.Trim();
        claim.Currency = currency!;
        ReplaceItems(_db, claim, items!);
        await _db.SaveChangesAsync(ct);
        return Ok(claim);
    }

    /// <summary>
    /// Taslağın kalemlerini yenileriyle değiştirir (kaydetmez). Yeni kalemlerin Id'si
    /// <c>Guid.NewGuid()</c> ile DOLU geldiği için yalnızca gezinme koleksiyonuna eklemek
    /// EF'e "var olan kayıt" dedirtiyor, SaveChanges UPDATE çalıştırıp
    /// DbUpdateConcurrencyException veriyordu (her düzenleme 500). Kalemler açıkça
    /// Added olarak izlenir; eskiler silinir.
    /// </summary>
    [NonAction]
    public static void ReplaceItems(ExpenseDbContext db, ExpenseClaim claim, List<ExpenseItem> items)
    {
        db.Items.RemoveRange(claim.Items);
        claim.Items.Clear();
        foreach (var item in items)
        {
            item.ClaimId = claim.Id;
            item.TenantSlug = claim.TenantSlug;
        }
        // AddRange, ClaimId sayesinde kalemleri claim.Items'a da bağlar (ilişki düzeltmesi);
        // ayrıca elle eklemek yanıtta her kalemi iki kez gösteriyordu.
        db.Items.AddRange(items);
        foreach (var item in items) if (!claim.Items.Contains(item)) claim.Items.Add(item);
        claim.TotalAmount = items.Sum(i => i.Amount);
    }

    /// <summary>Taslak beyanı (kalemleriyle) siler. Yalnızca Draft; yalnızca sahibi ya da İK.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var claim = await _db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null) return NotFound();
        if (!IsHr)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != claim.EmployeeId) return NotFound();
        }
        if (claim.Status != ClaimStatus.Draft)
            return Conflict(new { message = "Yalnızca taslak durumundaki beyan silinebilir" });
        _db.Items.RemoveRange(claim.Items);
        _db.Claims.Remove(claim);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id}/submit")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitClaimRequest request, CancellationToken ct)
    {
        var claim = await _db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id);
        if (claim is null) return NotFound();
        if (claim.Status != ClaimStatus.Draft) return BadRequest("Yalnizca taslak beyan gonderilebilir");
        if (claim.Items.Count == 0) return BadRequest("Beyanda en az bir kalem olmali");

        // GUVENLIK: Sahiplik kontrolu yoktu - herkes baskasinin taslagini gonderebiliyordu.
        if (!IsHr)
        {
            var me = await _approvals.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != claim.EmployeeId) return NotFound();
        }

        // Aynı e-Fatura (ETTN) kiracıda ikinci kez gönderilemez: önceki beyan reddedilmediyse (gönderildi /
        // onaylandı / ödendi) 409. Hangi çalışanın beyanında olduğu söylenmez (KVKK). Aynı beyanda tekrar da engellenir.
        var myEttns = claim.Items.Select(i => i.Ettn).Where(e => !string.IsNullOrEmpty(e)).Select(e => e!).ToList();
        if (myEttns.Count > 0)
        {
            var used = await _db.Items.AsNoTracking()
                .Where(i => i.ClaimId != claim.Id && i.Ettn != null && myEttns.Contains(i.Ettn)
                    && (i.Claim!.Status == ClaimStatus.Submitted || i.Claim.Status == ClaimStatus.Approved || i.Claim.Status == ClaimStatus.Paid))
                .Select(i => i.Ettn!).Distinct().ToListAsync(ct);
            var dup = DuplicateEttns(myEttns, used);
            if (dup.Count > 0)
                return Conflict(new { message = $"Bu e-Fatura başka bir masraf beyanında zaten gönderilmiş (ETTN {dup[0]}). Aynı fatura yalnızca önceki beyan reddedildiyse yeniden gönderilebilir.", code = "duplicate_ettn", ettns = dup });
        }

        // G9: politika denetimi (kalem/aylık limit, fiş zorunluluğu).
        var policy = await _db.Policies.AsNoTracking().FirstOrDefaultAsync(ct);
        var limits = ExpensePolicies.Limits(policy);
        if (limits.Count > 0)
        {
            var monthStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            var mtd = await _db.Items.AsNoTracking()
                .Where(i => i.ClaimId != claim.Id && i.ExpenseDate >= monthStart && i.Claim!.EmployeeId == claim.EmployeeId
                    && (i.Claim.Status == ClaimStatus.Submitted || i.Claim.Status == ClaimStatus.Approved || i.Claim.Status == ClaimStatus.Paid))
                .GroupBy(i => i.Category).Select(g => new { g.Key, Sum = g.Sum(i => i.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
            var violations = ExpensePolicies.Violations(claim.Items, limits, mtd);
            if (violations.Count > 0)
                return BadRequest(new { message = "Masraf politikasına uymayan kalemler var: " + string.Join("; ", violations), code = "policy_violation", violations });
        }

        // Masraf denetimi (olağan dışı tutar / olası mükerrer fiş): yalnızca onaycıya işaret.
        // Gönderimi ENGELLEMEZ; ML yanıt vermezse işaret yazılmaz ve beyan yine gönderilir.
        var anomalyFlags = 0;
        if (_anomaly is not null)
        {
            var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-ExpenseAnomalyClient.HistoryDays);
            var history = await _db.Items.AsNoTracking()
                .Where(i => i.ClaimId != claim.Id && i.ExpenseDate >= since
                    && (i.Claim!.Status == ClaimStatus.Submitted || i.Claim.Status == ClaimStatus.Approved || i.Claim.Status == ClaimStatus.Paid))
                .OrderByDescending(i => i.ExpenseDate).Take(ExpenseAnomalyClient.HistoryLimit)
                .Select(i => new HistoryRow(i.Claim!.EmployeeId, i.Category, i.Amount, i.ExpenseDate, i.SupplierTaxId, i.InvoiceNo, i.Ettn, i.Description))
                .ToListAsync(ct);
            var (checkedOk, n) = await _anomaly.CheckAsync(_tenant.TenantSlug ?? "", claim.EmployeeId, claim.Items, history,
                Request.Headers.Authorization.ToString(), ct);
            if (checkedOk) claim.AnomalyCheckedAt = DateTimeOffset.UtcNow;
            anomalyFlags = n;
        }

        claim.Status = ClaimStatus.Submitted;
        claim.SubmittedAt = DateTimeOffset.UtcNow;

        // Departman basina otomatik bir onay akisi acilir; bu olmadan "Onay kutusu"
        // sayfasi beyani hic gostermiyordu.
        // GUVENLIK: Istemcinin verdigi WorkflowRequestId KULLANILMAZ (alan yalnizca
        // eski istemcilerle uyumluluk icin kabul edilir). Onceden
        // bir yonetici kendi actigi sahte bir akisi (onayci = kendisi) kendi beyanina
        // baglayip onaylayarak KENDI beyanini onaylatabiliyordu (canli dogrulandi).
        // Onay akisini yalnizca sunucu, departman basina yonlendirerek baslatir.
        claim.WorkflowRequestId =
            await _approvals.StartExpenseApprovalAsync(claim.EmployeeId, claim.Title, ct,
                // Akış tanımındaki tutar koşulları (ör. 10.000 TL üstü finans onayı) için.
                // anomalyFlags: onaycı akış ekranında denetim işareti sayısını görür (karar insanda).
                System.Text.Json.JsonSerializer.Serialize(anomalyFlags > 0
                    ? new { expenseClaimId = claim.Id, amount = claim.TotalAmount, currency = claim.Currency, anomalyFlags }
                    : (object)new { expenseClaimId = claim.Id, amount = claim.TotalAmount, currency = claim.Currency }));

        await _db.SaveChangesAsync();
        return Ok(claim);
    }

    [HttpPost("{id}/resolve")]
    [Authorize(Policy = "RequireExpenseManage")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveClaimRequest request, CancellationToken ct)
    {
        var claim = await _db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id);
        if (claim is null) return NotFound();
        if (claim.Status != ClaimStatus.Submitted)
            return BadRequest("Yalnızca onay bekleyen beyan sonuçlandırılabilir");

        // GUVENLIK: bu uc, workflow-service'teki asil onay akisi basarisiz
        // olursa diye birakilan elle-sonuclandirma yolu - ama politika
        // (RequireExpenseManage) tek basina "sen bu beyanin sahibi misin"
        // sorusunu cevaplamiyordu. En azindan en bariz acigi (kendi
        // beyanini kendi onaylama/reddetme) kapatiyoruz.
        var myEmployeeId = await _approvals.FindMyEmployeeIdAsync(ct);
        if (myEmployeeId is null || myEmployeeId.Value == claim.EmployeeId)
            return Forbid();

        // GUVENLIK: Onay akisi olan bir beyan bu uctan sonuclandirilamaz - onceden
        // herhangi bir yonetici, departman basini atlayip tenant'taki her beyani
        // (akis Pending kalirken) buradan onaylayabiliyordu. Bu uc yalnizca akis
        // baslatilamamis (orn. departman basi atanmamis) beyanlar icin.
        if (claim.WorkflowRequestId is not null)
            return Conflict(new { message = "Bu beyan onay akışı üzerinden sonuçlandırılmalı" });

        claim.Status = request.Approved ? ClaimStatus.Approved : ClaimStatus.Rejected;
        if (request.Approved) claim.ApprovedByEmployeeId = myEmployeeId.Value;
        await _db.SaveChangesAsync();
        return Ok(claim);
    }

    [HttpPost("{id}/mark-paid")]
    [Authorize(Policy = "RequireExpenseMarkPaid")]
    public async Task<IActionResult> MarkPaid(Guid id, CancellationToken ct)
    {
        var claim = await _db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (claim is null) return NotFound();
        if (claim.Status != ClaimStatus.Approved)
            return BadRequest("Yalnızca onaylanmış beyan ödenmiş işaretlenebilir");

        // GUVENLIK / gorev ayriligi: beyan sahibi ya da onu onaylayan kisi odemeyi
        // isaretleyemez (onceden hr-admin tek basina tum dongunu kapatabiliyordu).
        // Kimligi olmayan platform hesaplari da bu kontrolu atlayamaz.
        var me = await _approvals.FindMyEmployeeIdAsync(ct);
        if (me is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Ödeme işaretlemek için çalışan kaydına bağlı bir hesap gerekli" });
        if (me.Value == claim.EmployeeId || me.Value == claim.ApprovedByEmployeeId)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Kendi beyanınızı ya da onayladığınız beyanı ödendi işaretleyemezsiniz" });

        claim.Status = ClaimStatus.Paid;
        claim.PaidAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(claim);
    }
}

public record ExpenseItemInput(
    ExpenseCategory Category, decimal Amount, DateOnly ExpenseDate,
    string? Description, string? ReceiptStorageKey,
    string? OriginalCurrency = null, decimal? OriginalAmount = null, decimal? Km = null, Guid? TravelRequestId = null,
    string? SupplierTaxId = null, string? InvoiceNo = null, string? Ettn = null, decimal? VatAmount = null);
public record CreateClaimRequest(
    Guid EmployeeId, string Title, string Currency, List<ExpenseItemInput> Items);
public record UpdateClaimRequest(string Title, string Currency, List<ExpenseItemInput> Items);
public record SubmitClaimRequest(Guid? WorkflowRequestId);
public record ResolveClaimRequest(bool Approved);
