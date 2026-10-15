using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WorkflowService.Data;
using WorkflowService.Models;
using WorkflowService.Messaging;
using WorkflowService.Services;
using System.Text.Json;

namespace WorkflowService.Controllers;

[ApiController]
[Route("api/workflows")]
[Authorize]
public class WorkflowsController : ControllerBase
{
    private readonly WorkflowDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly WorkflowRouting _routing;

    private readonly Tenancy.ITenantContext _tenant;

    public WorkflowsController(WorkflowDbContext db, EmployeeDirectoryClient employees, WorkflowRouting routing, Tenancy.ITenantContext tenant)
    {
        _db = db;
        _employees = employees;
        _routing = routing;
        _tenant = tenant;
    }

    /// <summary>
    /// Kendi modülünde açılan talep türleri: izin (leave-service), masraf ve seyahat
    /// (expense-service), fazla mesai (timeshift-service), teklif onayı (recruitment-service).
    /// Bu türler modül kurallarından (bakiye, masraf politikası, gerçek yönetici) geçer ve onaycı
    /// zincirini sunucu kurar. Genel "Yeni talep" ucundan açılırsa bu kurallar atlanıyor, talep
    /// eden onaycısını kendi seçebiliyordu; artık tarayıcıdan gelen istekte reddedilir.
    /// </summary>
    public static readonly HashSet<WorkflowType> ModuleTypes = new()
    {
        WorkflowType.LeaveRequest, WorkflowType.ExpenseClaim, WorkflowType.Travel,
        WorkflowType.Overtime, WorkflowType.OfferApproval,
    };

    /// <summary>
    /// İstek bir servisten mi geldi (tarayıcıdan değil)? Modül servisleri bu uca kullanıcının
    /// jetonuyla doğrudan (konteyner ağından) gelir; tarayıcı istekleri her zaman nginx
    /// gateway'inden geçer ve gateway X-Real-IP / X-Forwarded-For başlıklarını kendisi yazar
    /// (istemci bunları kaldıramaz). Ek olarak geçerli X-Internal-Token da servis sayılır.
    /// </summary>
    private bool FromService
    {
        get
        {
            var given = Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault();
            if (!string.IsNullOrEmpty(given) && Security.InternalServiceToken.Matches(given))
                return true;
            return !Request.Headers.ContainsKey("X-Real-IP") && !Request.Headers.ContainsKey("X-Forwarded-For");
        }
    }

    /// <summary>Platform yöneticisinin (kiracısı olmadan) okuduğu kiracı talepleri denetim kaydına yazılır.</summary>
    private Task AuditPlatformListAsync(IEnumerable<WorkflowRequest> items, string entityType, CancellationToken ct)
    {
        if (!_tenant.IsPlatformAdmin) return Task.CompletedTask;
        var perTenant = items
            .Where(w => Auditing.PlatformAccessAudit.ShouldLog(true, _tenant.TenantSlug, w.TenantSlug))
            .GroupBy(w => w.TenantSlug)
            .Select(g => (g.Key, (object)new { count = g.Count() }));
        return Auditing.PlatformAccessAudit.WriteAsync(_db, HttpContext, entityType, "list", perTenant, ct);
    }

    /// <summary>Tum is akislarini gorebilen/yonetebilen roller.</summary>
    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin");

    /// <summary>
    /// GUVENLIK: Liste/detay/gecikenler onceden yalnizca [Authorize] ile korunuyordu -
    /// her calisan kiracidaki TUM taleplerin konusunu ve payload'unu gorebiliyordu
    /// (ornegin "5 gunluk Sick talebi" = saglik bilgisi; canli dogrulandi). Artik
    /// IK rolleri disindakiler yalnizca talep sahibi, onaycisi ya da vekili
    /// olduklari akislari gorur. Kimlik cozulemezse bos/403 (fail-closed).
    /// </summary>
    private IQueryable<WorkflowRequest> VisibleTo(IQueryable<WorkflowRequest> q, Guid me) =>
        q.Where(w => w.RequesterEmployeeId == me
            || w.Steps.Any(s => s.ApproverEmployeeId == me || s.DelegatedToEmployeeId == me));

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] WorkflowStatus? status, [FromQuery] Guid? requesterId, CancellationToken ct)
    {
        var query = _db.WorkflowRequests.Include(w => w.Steps).AsQueryable();
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            query = VisibleTo(query, me.Value);
        }
        if (status.HasValue) query = query.Where(w => w.Status == status.Value);
        if (requesterId.HasValue) query = query.Where(w => w.RequesterEmployeeId == requesterId.Value);
        var list = await query.AsNoTracking().OrderByDescending(w => w.CreatedAt).ToListAsync(ct);
        await RedactHiddenFieldsAsync(list, ct);
        await AuditPlatformListAsync(list, "WorkflowRequestList", ct);
        return Ok(list);
    }

    /// <summary>
    /// KVKK: akış tanımında onaycılardan gizlenen alanlar (ör. izin gerekçesi) talep sahibi ve
    /// İK dışındakilere gösterilmez. Liste, ayrıntı ve geciken uçlarının hepsinde uygulanır
    /// (önceden yalnızca ayrıntıda uygulanıyordu; liste ucu gizli alanı döndürüyordu).
    /// Varlıklar izlenmeyen (AsNoTracking) olmalıdır.
    /// </summary>
    private async Task RedactHiddenFieldsAsync(IReadOnlyCollection<WorkflowRequest> items, CancellationToken ct)
    {
        if (IsHr || items.Count == 0 || items.All(w => string.IsNullOrEmpty(w.Payload))) return;
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var types = items.Select(w => w.Type).Distinct().ToList();
        var defs = await _db.Definitions.AsNoTracking().Where(d => types.Contains(d.Type) && d.IsActive)
            .Select(d => new { d.Type, d.HiddenFieldsJson }).ToListAsync(ct);
        var hiddenByType = defs.GroupBy(d => d.Type).ToDictionary(g => g.Key,
            g => JsonSerializer.Deserialize<List<string>>(g.First().HiddenFieldsJson) ?? new List<string>());
        foreach (var wf in items)
        {
            if (wf.Payload is not { Length: > 0 } || me == wf.RequesterEmployeeId) continue;
            if (!hiddenByType.TryGetValue(wf.Type, out var fields) || fields.Count == 0) continue;
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(wf.Payload)?.AsObject();
                if (node is null) continue;
                foreach (var k in node.Select(p => p.Key).ToList())
                    if (fields.Any(f => string.Equals(f, k, StringComparison.OrdinalIgnoreCase))) node.Remove(k);
                wf.Payload = node.ToJsonString();
            }
            catch (JsonException) { }
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var q = _db.WorkflowRequests.Include(w => w.Steps.OrderBy(s => s.Order)).AsQueryable();
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            q = VisibleTo(q, me.Value);
        }
        var wf = await q.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null) return NotFound();
        // KVKK: akış tanımında onaycılardan gizlenen alanlar talep sahibi ve İK dışındakilere gösterilmez.
        await RedactHiddenFieldsAsync(new[] { wf }, ct);
        if (Auditing.PlatformAccessAudit.ShouldLog(_tenant.IsPlatformAdmin, _tenant.TenantSlug, wf.TenantSlug))
            await Auditing.PlatformAccessAudit.WriteAsync(_db, HttpContext, "WorkflowRequest", wf.Id.ToString(),
                new[] { (wf.TenantSlug, (object)new { type = wf.Type.ToString() }) }, ct);
        return Ok(wf);
    }

    public sealed class LeaveRow
    {
        public Guid EmployeeId { get; set; }
        public string Type { get; set; } = "";
        public DateOnly StartDate { get; set; }
        public decimal Days { get; set; }
        public string Status { get; set; } = "";
    }

    public sealed class BalanceRow
    {
        public decimal EntitledDays { get; set; }
        public decimal UsedDays { get; set; }
        public decimal PendingDays { get; set; }
    }

    /// <summary>
    /// İzin talebinin onayında çalışanın o tür ve yıl için güncel bakiyesi: onaycı bilgi olmadan
    /// karar vermesin. Talebi görebilen herkes (talep sahibi, onaycı/vekil, İK) okuyabilir; yalnızca
    /// sayılar döner. Kayıtlar aynı veritabanından kiracı filtresiyle salt okunur (çalışan
    /// tablosundaki desenle aynı); izin kaydı talep sahibine ait değilse boş döner.
    /// </summary>
    [HttpGet("{id:guid}/leave-balance")]
    public async Task<IActionResult> LeaveBalance(Guid id, CancellationToken ct)
    {
        var q = _db.WorkflowRequests.AsQueryable();
        Guid? me = null;
        if (!IsHr)
        {
            me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            q = VisibleTo(q, me.Value);
        }
        var wf = await q.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null || wf.Type != WorkflowType.LeaveRequest) return NotFound();
        var none = new { available = false };
        // KVKK: akış tanımı gün sayısını onaycılardan gizliyorsa bakiye de gösterilmez.
        if (!IsHr && me != wf.RequesterEmployeeId)
        {
            var hidden = await _db.Definitions.AsNoTracking().Where(d => d.Type == WorkflowType.LeaveRequest && d.IsActive)
                .Select(d => d.HiddenFieldsJson).FirstOrDefaultAsync(ct);
            if (hidden is not null && (JsonSerializer.Deserialize<List<string>>(hidden) ?? new()).Any(f => f.Equals("days", StringComparison.OrdinalIgnoreCase)))
                return Ok(none);
        }
        Guid leaveId;
        try
        {
            using var doc = JsonDocument.Parse(wf.Payload ?? "{}");
            if (!doc.RootElement.TryGetProperty("leaveRequestId", out var lid) || !lid.TryGetGuid(out leaveId)) return Ok(none);
        }
        catch (JsonException) { return Ok(none); }
        var leave = await _db.Database.SqlQueryRaw<LeaveRow>(
            "SELECT \"EmployeeId\", \"Type\", \"StartDate\", \"Days\", \"Status\" FROM leave_requests WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}",
            wf.TenantSlug, leaveId).FirstOrDefaultAsync(ct);
        if (leave is null || leave.EmployeeId != wf.RequesterEmployeeId) return Ok(none);
        var bal = await _db.Database.SqlQueryRaw<BalanceRow>(
            "SELECT \"EntitledDays\", \"UsedDays\", \"PendingDays\" FROM leave_balances WHERE \"TenantSlug\" = {0} AND \"EmployeeId\" = {1} AND \"Year\" = {2} AND \"Type\" = {3}",
            wf.TenantSlug, leave.EmployeeId, leave.StartDate.Year, leave.Type).FirstOrDefaultAsync(ct);
        if (Auditing.PlatformAccessAudit.ShouldLog(_tenant.IsPlatformAdmin, _tenant.TenantSlug, wf.TenantSlug))
            await Auditing.PlatformAccessAudit.WriteAsync(_db, HttpContext, "WorkflowLeaveBalance", wf.Id.ToString(),
                new[] { (wf.TenantSlug, (object)new { type = leave.Type }) }, ct);
        return Ok(new
        {
            available = bal is not null,
            leaveType = leave.Type,
            year = leave.StartDate.Year,
            requestDays = leave.Days,
            requestStatus = leave.Status,
            entitledDays = bal?.EntitledDays,
            usedDays = bal?.UsedDays,
            pendingDays = bal?.PendingDays,
            // Bekleyen talepler (bu talep dahil) düşülmüş kalan.
            remainingDays = bal is null ? (decimal?)null : bal.EntitledDays - bal.UsedDays - bal.PendingDays,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkflowRequest request, CancellationToken ct)
    {
        // GUVENLIK: Talep eden ve onaycilar tamamen istemciden geliyordu. Bir
        // yonetici "talep eden = bir meslektas, onayci = kendisi" olan sahte bir
        // akis acip kendi masraf beyanina baglayarak KENDI beyanini onaylayabiliyordu
        // (canli dogrulandi: 49.999 TL). Artik IK rolleri disinda talep eden
        // HER ZAMAN cagiranin kendisidir; onaycilar dogrulanir.
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            if (request.RequesterEmployeeId != me.Value)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Yalnızca kendi adınıza talep oluşturabilirsiniz" });
        }
        var fromService = FromService;
        if (!fromService && ModuleTypes.Contains(request.Type))
            return BadRequest(new { message = "Bu talep türü kendi ekranından açılır", code = "module_type" });
        var (error, wf) = await CreateCoreAsync(request, fromService, ct);
        return error ?? CreatedAtAction(nameof(GetById), new { id = wf!.Id }, wf);
    }

    /// <summary>
    /// Servisler arası (jetonsuz) akış başlatma: sohbet botundan açılan izin talebi gibi.
    /// Web ucuyla aynı doğrulamalar uygulanır; talep eden gövdeden gelir (çağıran servis
    /// kişiyi doğrulamıştır). Gateway /api/*/internal/ yollarını dışarıya kapatır.
    /// </summary>
    [HttpPost("/api/internal/workflows")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateInternal([FromBody] InternalCreateWorkflowRequest request,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault()))
            return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        tenant.TenantSlug = request.TenantSlug;
        tenant.IsPlatformAdmin = false;
        var (error, wf) = await CreateCoreAsync(new CreateWorkflowRequest(request.Type, request.RequesterEmployeeId, request.Subject,
            request.Payload, request.ApproverEmployeeIds ?? new List<Guid>(), request.SlaHours), fromService: true, ct);
        return error ?? Ok(new { wf!.Id });
    }

    /// <param name="fromService">
    /// Servisten (modülden) gelen istek: onaycı listesi boşsa sunucu belirler (bölüm başı → üst
    /// bölüm başı → İK onaycısı). Tarayıcıdan gelen serbest talepte onaycılar kullanıcı seçer ve
    /// her biri giriş hesabı olan, ayrılmamış bir çalışan olmalıdır.
    /// </param>
    private async Task<(IActionResult? Error, WorkflowRequest? Wf)> CreateCoreAsync(CreateWorkflowRequest request, bool fromService, CancellationToken ct)
    {
        var tenant = _db.CurrentTenantSlug ?? "";
        // Kiracının bu tür için etkin akış tanımı varsa onaycı zinciri ondan kurulur (Y22).
        var chain = await _routing.ResolveAsync(tenant, request.Type, request.RequesterEmployeeId, request.Payload, ct);
        var approvers = chain?.Select(c => c.Approver).ToList() ?? request.ApproverEmployeeIds ?? new List<Guid>();
        // Üst onaycısı bulunamayan modül talebi (ör. departman başının kendi izni) önceden hiç
        // açılmıyor, kimsenin kutusuna düşmeden askıda kalıyordu: üst bölüm başına, o da yoksa
        // İK onaycısına gider.
        if (approvers.Count == 0 && fromService
            && await _routing.FallbackApproverAsync(tenant, request.RequesterEmployeeId, ct) is { } fallback)
            approvers = new List<Guid> { fallback };
        if (approvers.Count == 0 || approvers.Count > 10)
            return (BadRequest(new { message = "En az 1, en fazla 10 onaycı gerekli" }), null);
        if (approvers.Contains(Guid.Empty) || approvers.Distinct().Count() != approvers.Count)
            return (BadRequest(new { message = "Onaycı listesi geçersiz ya da tekrar içeriyor" }), null);
        if (approvers.Contains(request.RequesterEmployeeId))
            return (BadRequest(new { message = "Talep eden kendi talebinin onaycısı olamaz" }), null);
        if (request.SlaHours is < 1 or > 24 * 90)
            return (BadRequest(new { message = "SLA 1 saat ile 90 gün arasında olmalı" }), null);
        if (request.Subject is { Length: > 300 })
            return (BadRequest(new { message = "Konu en fazla 300 karakter olabilir" }), null);
        if (!fromService && chain is null)
        {
            var valid = await _routing.ActiveLoginEmployeesAsync(tenant, approvers, ct);
            if (approvers.Any(a => !valid.Contains(a)))
                return (BadRequest(new { message = "Onaycılar HR360 giriş hesabı olan, aktif çalışanlar olmalı" }), null);
        }

        var wf = new WorkflowRequest
        {
            Type = request.Type,
            RequesterEmployeeId = request.RequesterEmployeeId,
            Subject = request.Subject,
            Payload = request.Payload,
            SlaDueAt = request.SlaHours.HasValue
                ? DateTimeOffset.UtcNow.AddHours(request.SlaHours.Value)
                : null
        };

        int order = 1;
        foreach (var approverId in approvers)
        {
            wf.Steps.Add(new ApprovalStep
            {
                Order = order,
                ApproverEmployeeId = approverId,
                SlaHours = chain?[order - 1].SlaHours,
            });
            order++;
        }

        _db.WorkflowRequests.Add(wf);

        // Sirali onay: SADECE ilk adim su an aktif; sonraki adimlarin onaycilari kendi siralari
        // geldiginde (ApplyDecisionAsync) bilgilendirilir. Vekalet ve e-posta karar jetonu burada uygulanir.
        var firstStep = wf.Steps.OrderBy(s => s.Order).First();
        await _routing.AssignAsync(wf, firstStep, tenant, ct);

        await _db.SaveChangesAsync(ct);
        return (null, wf);
    }

    // NOT: Onceden RequireManagerOrAbove politikasi vardi; akis tanimlariyla (Y22) belirli bir kisi
    // ve vekalet (Y23) ile yonetici olmayan biri de onayci/vekil olabiliyor. Asagidaki acik kontrol
    // (onayci ya da vekil; IK idari mudahalesi; kendi talebi asla) yeterli ve daha siki.
    [HttpPost("{id}/steps/{stepId}/decide")]
    public async Task<IActionResult> Decide(Guid id, Guid stepId, [FromBody] DecideRequest request, CancellationToken ct)
    {
        // NOT: Onceden "Delegated"/"Pending" da kabul ediliyordu - "Delegated" adimi
        // karar verilmis sayip onaylamadan birakiyor, akis bir daha asla
        // tamamlanamiyordu. Karar yalnizca Onay ya da Red olabilir.
        if (request.Decision is not (StepDecision.Approved or StepDecision.Rejected))
            return BadRequest(new { message = "Karar yalnızca Approved ya da Rejected olabilir" });

        var wf = await _db.WorkflowRequests.Include(w => w.Steps).FirstOrDefaultAsync(w => w.Id == id);
        if (wf is null) return NotFound("Workflow not found");
        if (wf.Status != WorkflowStatus.Pending) return BadRequest("Workflow is not pending");

        var step = wf.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null) return NotFound("Step not found");
        if (step.Decision != StepDecision.Pending) return BadRequest("Step already decided");

        // GUVENLIK: RequireManagerOrAbove tek basina "bu adima atanan kisi
        // bu mu" sorusunu cevaplamiyor - herhangi bir manager+ rolundeki
        // kullanici, baskasina (hatta kendi actigi talebe) atanmis bir
        // adimi karara baglayabiliyordu. Simdi, karari veren kullanicinin
        // GERCEKTEN step.ApproverEmployeeId'ye esit olmasi gerekiyor;
        // hr-admin/tenant-admin/platform-admin bu kontrolu (Devret
        // ozelligine benzer sekilde, idari mudahale icin) gecebilir -
        // AMA "kendi actigi talebi kendi karara baglayamaz" kurali hicbir
        // rol icin istisna degildir. Bu iki kontrol kasitli olarak ayri:
        // ilk denemede tam bu ayrimi atlamistim - tenant-admin override'i,
        // RequesterEmployeeId kontrolunden ONCE calisip kendi talebini
        // reddedebiliyordu.
        //
        // FAIL-CLOSED: myEmployeeId null donerse (employee-service'e
        // erisilemedi ya da /me kaydi yok) REDDEDILIR, kontrol atlanmaz.
        // Ilk denemede "null ise kontrolu atla" yazmistim - bu, gercek
        // veride JWT email'i ile employee kaydi email'i uyusmadiginda
        // (bkz. FindMyEmployeeIdAsync yorumu, artik /me kullaniyor) sessizce
        // acik kapi birakiyordu; bugun canli ortamda iki kez dogrulandi.
        var myEmployeeId = await _employees.FindMyEmployeeIdAsync(ct);
        if (myEmployeeId is null || myEmployeeId.Value == wf.RequesterEmployeeId)
            return Forbid();

        var isAdminOverride = User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
            || User.IsInRole("platform-admin");
        // Vekalet: adim baskasina devredildiyse vekil de karar verebilir (onceden
        // DelegatedToEmployeeId hic okunmuyordu, devretme ozelligi calismiyordu).
        if (!isAdminOverride && myEmployeeId.Value != step.ApproverEmployeeId
            && myEmployeeId.Value != step.DelegatedToEmployeeId)
            return Forbid();

        var comment = request.Comment;
        // Vekilin verdiği karar onay geçmişinde ayrıca belirtilir (denetim izi).
        if (myEmployeeId.Value == step.DelegatedToEmployeeId && myEmployeeId.Value != step.ApproverEmployeeId)
            comment = string.IsNullOrWhiteSpace(comment) ? (step.EscalatedAt is null ? "(vekâleten)" : "(üst yönetici)") : $"{comment} ({(step.EscalatedAt is null ? "vekâleten" : "üst yönetici")})";
        var error = await ApplyDecisionAsync(wf, step, request.Decision, comment, myEmployeeId.Value, ct);
        if (error is not null) return BadRequest(error);
        await _db.SaveChangesAsync();
        return Ok(wf);
    }

    /// <summary>
    /// Servisler arasi karar ucu: Slack/Teams'teki "Onayla/Reddet" dugmeleri
    /// governance-service uzerinden buraya gelir. Kullanici jetonu yoktur; cagri
    /// INTERNAL_SERVICE_TOKEN ile dogrulanir ve karari veren kisi (sohbet
    /// hesabi e-postayla eslesmis calisan) govdede gelir.
    ///
    /// GUVENLIK: Web ucundaki kurallarin aynisi uygulanir, istisnasiz:
    /// karar veren adimin onaycisi ya da vekili olmali, kendi talebini karara
    /// baglayamaz. IK/yonetici "idari mudahale" istisnasi burada YOKTUR (sohbet
    /// hesabinin rolleri bilinmez). Gateway /api/*/internal/ yollarini disariya
    /// kapatir; anahtar tanimli degilse uc tamamen kapalidir.
    /// </summary>
    [HttpPost("/api/internal/workflows/{id}/steps/{stepId}/decide")]
    [AllowAnonymous]
    public async Task<IActionResult> DecideInternal(Guid id, Guid stepId, [FromBody] InternalDecideRequest request,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault()))
            return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        if (request.Decision is not (StepDecision.Approved or StepDecision.Rejected))
            return BadRequest(new { message = "Karar yalnızca Approved ya da Rejected olabilir" });

        tenant.TenantSlug = request.TenantSlug;
        tenant.IsPlatformAdmin = false;

        var wf = await _db.WorkflowRequests.Include(w => w.Steps).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null) return NotFound(new { message = "Talep bulunamadı", code = "not_found" });
        var step = wf.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null) return NotFound(new { message = "Onay adımı bulunamadı", code = "not_found" });
        if (wf.Status != WorkflowStatus.Pending || step.Decision != StepDecision.Pending)
            return Conflict(new { message = "Bu talep zaten karara bağlanmış.", code = "already_decided", status = wf.Status.ToString() });
        if (request.ActorEmployeeId == wf.RequesterEmployeeId)
            return StatusCode(403, new { message = "Kendi talebinizi onaylayamazsınız.", code = "forbidden" });
        if (request.ActorEmployeeId != step.ApproverEmployeeId && request.ActorEmployeeId != step.DelegatedToEmployeeId)
            return StatusCode(403, new { message = "Bu adımın onaycısı siz değilsiniz.", code = "forbidden" });

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (request.Channel is { Length: > 0 } ch) comment = comment is null ? $"({ch} üzerinden)" : $"{comment} ({ch} üzerinden)";
        var error = await ApplyDecisionAsync(wf, step, request.Decision, comment, request.ActorEmployeeId, ct);
        if (error is not null) return Conflict(new { message = "Önceki onay adımları henüz tamamlanmadı.", code = "previous_pending" });
        await _db.SaveChangesAsync(ct);
        return Ok(new { wf.Id, status = wf.Status.ToString(), wf.Subject, stepOrder = step.Order });
    }

    /// <summary>Yetki kontrolleri yapildiktan sonra karari uygular (kaydetmez). Hata metni ya da null.</summary>
    private async Task<string?> ApplyDecisionAsync(WorkflowRequest wf, ApprovalStep step, StepDecision decision, string? comment,
        Guid actorEmployeeId, CancellationToken ct)
    {
        var request = new DecideRequest(decision, comment);
        var myEmployeeId = (Guid?)actorEmployeeId;
        // Sirali onay: onceki adimlar tamamlanmadan bu adim karar veremez
        var previousPending = wf.Steps.Any(s => s.Order < step.Order && s.Decision == StepDecision.Pending);
        if (previousPending) return "Previous steps are still pending";

        step.Decision = request.Decision;
        step.Comment = request.Comment;
        step.DecidedAt = DateTimeOffset.UtcNow;
        step.ActionTokenHash = null; // e-posta bağlantısı artık geçersiz

        if (request.Decision == StepDecision.Rejected)
        {
            wf.Status = WorkflowStatus.Rejected;
            wf.CompletedAt = DateTimeOffset.UtcNow;
        }
        else if (request.Decision == StepDecision.Approved)
        {
            var allApproved = wf.Steps.All(s => s.Decision == StepDecision.Approved);
            if (allApproved)
            {
                wf.Status = WorkflowStatus.Approved;
                wf.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                // Sira bir sonraki adima gecti - o adimin onaycisina
                // "karar bekleyen bir talebiniz var" bildirimi gonder.
                var nextStep = wf.Steps
                    .Where(s => s.Order > step.Order && s.Decision == StepDecision.Pending)
                    .OrderBy(s => s.Order)
                    .FirstOrDefault();
                if (nextStep is not null)
                    await _routing.AssignAsync(wf, nextStep, _db.CurrentTenantSlug ?? wf.TenantSlug, ct);
            }
        }

        // Akis sonuclandiysa event yayinla: talebi baslatan servisler
        // (leave, expense) bunu dinleyip kendi kaydini kapatir.
        if (wf.Status is WorkflowStatus.Approved or WorkflowStatus.Rejected)
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Topic = WorkflowTopics.Events,
                EventType = wf.Status == WorkflowStatus.Approved
                    ? WorkflowEventTypes.Approved
                    : WorkflowEventTypes.Rejected,
                PartitionKey = wf.Id.ToString(),
                Payload = JsonSerializer.Serialize(new WorkflowDecidedEvent(
                    _db.CurrentTenantSlug ?? "",
                    wf.Id, wf.Type.ToString(), wf.RequesterEmployeeId, wf.Subject,
                    wf.Status == WorkflowStatus.Approved,
                    // Denetim izi: karari GERCEKTEN veren kisi (vekil ya da IK
                    // mudahalesi olabilir) - onceden adimin atanmis onaycisi yaziliyordu.
                    myEmployeeId.Value, step.Comment, DateTimeOffset.UtcNow)),
            });
        }

        return null;
    }

    /// <summary>
    /// Bekleyen akisi iptal eder - talep sahibi (izin talebini iptal ettiginde
    /// leave-service onun jetonuyla cagirir) ya da IK. Onceden iptal ucu yoktu: iptal edilen
    /// iznin akisi onaycinin kutusunda acik kaliyordu.
    /// </summary>
    public record BulkItem(Guid WorkflowId, Guid StepId);
    public record BulkDecideRequest(List<BulkItem> Items, StepDecision Decision, string? Comment);

    /// <summary>
    /// Toplu onay/ret (G10): her kalem tek tek, tekil karar ucuyla AYNI kurallarla işlenir;
    /// yetkisiz ya da sırası gelmemiş kalemler atlanır ve sonuçta bildirilir. En fazla 50 kalem.
    /// </summary>
    [HttpPost("bulk-decide")]
    public async Task<IActionResult> BulkDecide([FromBody] BulkDecideRequest request, CancellationToken ct)
    {
        if (request.Decision is not (StepDecision.Approved or StepDecision.Rejected))
            return BadRequest(new { message = "Karar yalnızca Approved ya da Rejected olabilir" });
        if (request.Items is null || request.Items.Count is 0 or > 50)
            return BadRequest(new { message = "1-50 talep seçin" });
        // Tekil retteki kuralla aynı: ret gerekçesi zorunlu (en az 3 karakter); talep sahibi görür.
        var bulkComment = request.Comment?.Trim();
        if (bulkComment is { Length: > 1000 })
            return BadRequest(new { message = "Gerekçe en fazla 1000 karakter olabilir" });
        if (request.Decision == StepDecision.Rejected && (bulkComment is null || bulkComment.Length < 3))
            return BadRequest(new { message = "Ret gerekçesi zorunlu (en az 3 karakter)" });
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        if (me is null) return Forbid();
        var results = new List<object>();
        var done = 0;
        foreach (var item in request.Items.DistinctBy(i => i.StepId))
        {
            var wf = await _db.WorkflowRequests.Include(w => w.Steps).FirstOrDefaultAsync(w => w.Id == item.WorkflowId, ct);
            var step = wf?.Steps.FirstOrDefault(x => x.Id == item.StepId);
            string? error = null;
            if (wf is null || step is null) error = "Talep bulunamadı";
            else if (wf.Status != WorkflowStatus.Pending || step.Decision != StepDecision.Pending) error = "Zaten karara bağlanmış";
            else if (wf.RequesterEmployeeId == me) error = "Kendi talebinize karar veremezsiniz";
            else if (me != step.ApproverEmployeeId && me != step.DelegatedToEmployeeId) error = "Bu adımın onaycısı siz değilsiniz";
            else
            {
                var comment = string.IsNullOrEmpty(bulkComment) ? "(toplu karar)" : $"{bulkComment} (toplu karar)";
                error = await ApplyDecisionAsync(wf, step, request.Decision, comment, me.Value, ct);
                if (error is not null) error = "Önceki onay adımları henüz tamamlanmadı";
            }
            if (error is null) { await _db.SaveChangesAsync(ct); done++; }
            else _db.ChangeTracker.Clear();
            results.Add(new { item.WorkflowId, item.StepId, ok = error is null, error });
        }
        return Ok(new { done, results });
    }

    /// <summary>
    /// E-postadaki tek kullanımlık bağlantının ön izlemesi (oturum gerekmez). KVKK: kişisel veri
    /// yerine yalnızca talep türü ve tarih döner; ayrıntı için HR360'a giriş gerekir.
    /// </summary>
    [HttpGet("email-action/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> EmailActionPreview(string token, CancellationToken ct)
    {
        var step = await _routing.StepForTokenAsync(token, ct);
        if (step?.WorkflowRequest is not { } wf || step.Decision != StepDecision.Pending || wf.Status != WorkflowStatus.Pending)
            return NotFound(new { message = "Bağlantının süresi dolmuş ya da daha önce kullanılmış", code = "invalid_link" });
        return Ok(new { type = wf.Type.ToString(), createdAt = wf.CreatedAt, stepOrder = step.Order, stepCount = wf.Steps.Count, workflowId = wf.Id });
    }

    public record EmailActionRequest(string Token, StepDecision Decision, string? Comment);

    /// <summary>
    /// E-postadan tek tıkla karar. Bağlantı adıma ve onaycıya özeldir, 72 saat geçerlidir ve
    /// TEK KULLANIMLIKTIR; karar web arayüzüyle aynı kurallarla (sıra, kendi talebi) uygulanır.
    /// E-posta tarayıcılarının bağlantıyı açması karar vermez: karar ayrı bir onay adımıyla
    /// (POST) verilir.
    /// </summary>
    [HttpPost("email-action")]
    [AllowAnonymous]
    public async Task<IActionResult> EmailAction([FromBody] EmailActionRequest request, [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (request.Decision is not (StepDecision.Approved or StepDecision.Rejected))
            return BadRequest(new { message = "Karar yalnızca Approved ya da Rejected olabilir" });
        var found = await _routing.StepForTokenAsync(request.Token, ct);
        if (found?.WorkflowRequest is null)
            return NotFound(new { message = "Bağlantının süresi dolmuş ya da daha önce kullanılmış", code = "invalid_link" });
        tenant.TenantSlug = found.TenantSlug;
        tenant.IsPlatformAdmin = false;
        _db.ChangeTracker.Clear();
        var wf = await _db.WorkflowRequests.Include(w => w.Steps).FirstAsync(w => w.Id == found.WorkflowRequestId, ct);
        var step = wf.Steps.First(x => x.Id == found.Id);
        if (wf.Status != WorkflowStatus.Pending || step.Decision != StepDecision.Pending)
            return Conflict(new { message = "Bu talep zaten karara bağlanmış.", code = "already_decided" });
        var actor = step.DelegatedToEmployeeId ?? step.ApproverEmployeeId;
        if (actor == wf.RequesterEmployeeId) return StatusCode(403, new { message = "Kendi talebinizi onaylayamazsınız." });
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? "(e-posta üzerinden)" : $"{request.Comment.Trim()[..Math.Min(request.Comment.Trim().Length, 500)]} (e-posta üzerinden)";
        var error = await ApplyDecisionAsync(wf, step, request.Decision, comment, actor, ct);
        if (error is not null) return Conflict(new { message = "Önceki onay adımları henüz tamamlanmadı.", code = "previous_pending" });
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = wf.Status.ToString(), decision = request.Decision.ToString() });
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var wf = await _db.WorkflowRequests.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null) return NotFound();
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != wf.RequesterEmployeeId) return NotFound();
        }
        return await CancelCoreAsync(wf, ct) ?? Ok(wf);
    }

    /// <summary>
    /// Servisler arası (jetonsuz) iptal: sohbet botundan iptal edilen izin talebinin akışını
    /// leave-service kapatır. Yalnızca talep sahibi adına (actorEmployeeId = talep eden);
    /// web ucuyla aynı kural (yalnızca bekleyen akış). Gateway /api/*/internal/ yollarını dışarıya kapatır.
    /// </summary>
    [HttpPost("/api/internal/workflows/{id}/cancel")]
    [AllowAnonymous]
    public async Task<IActionResult> CancelInternal(Guid id, [FromBody] InternalCancelWorkflowRequest request,
        [FromServices] Tenancy.TenantContext tenant, CancellationToken ct)
    {
        if (!Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault()))
            return NotFound();
        if (string.IsNullOrWhiteSpace(request.TenantSlug)) return BadRequest(new { message = "Kiracı belirtilmedi" });
        tenant.TenantSlug = request.TenantSlug;
        tenant.IsPlatformAdmin = false;
        var wf = await _db.WorkflowRequests.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null || wf.RequesterEmployeeId != request.ActorEmployeeId) return NotFound(new { message = "Talep bulunamadı" });
        return await CancelCoreAsync(wf, ct) ?? Ok(new { wf.Id, status = wf.Status.ToString() });
    }

    private async Task<IActionResult?> CancelCoreAsync(WorkflowRequest wf, CancellationToken ct)
    {
        if (wf.Status != WorkflowStatus.Pending)
            return BadRequest(new { message = "Yalnızca bekleyen talep iptal edilebilir" });
        wf.Status = WorkflowStatus.Cancelled;
        wf.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return null;
    }

    [HttpPost("{id}/steps/{stepId}/delegate")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Delegate(Guid id, Guid stepId, [FromBody] DelegateRequest request, CancellationToken ct)
    {
        var wf = await _db.WorkflowRequests.Include(w => w.Steps).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wf is null) return NotFound("Workflow not found");
        var step = wf.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null) return NotFound("Step not found");
        if (wf.Status != WorkflowStatus.Pending) return BadRequest("Workflow is not pending");
        if (step.Decision != StepDecision.Pending) return BadRequest("Step already decided");

        // GUVENLIK: Onceden herhangi bir yonetici, kendisine ait olmayan herhangi bir
        // adimi istedigi kisiye (kendisine dahil) devredebiliyordu. Artik yalnizca
        // adimin onaycisi ya da IK devredebilir; talep sahibine devredilemez.
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null || me.Value != step.ApproverEmployeeId) return Forbid();
            if (request.DelegateToEmployeeId == me.Value)
                return BadRequest(new { message = "Adımı kendinize devredemezsiniz" });
        }
        if (request.DelegateToEmployeeId == Guid.Empty || request.DelegateToEmployeeId == wf.RequesterEmployeeId)
            return BadRequest(new { message = "Adım talep sahibine devredilemez" });

        step.DelegatedToEmployeeId = request.DelegateToEmployeeId;
        step.Comment = request.Comment;
        await _db.SaveChangesAsync();
        return Ok(step);
    }

    /// <summary>
    /// Serbest talepte seçilebilecek onaycılar: HR360 giriş hesabı olan, ayrılmamış çalışanlar
    /// (çağıran hariç). Yalnızca kimlik ve ad döner (dizinle aynı veri).
    /// </summary>
    [HttpGet("approver-candidates")]
    public async Task<IActionResult> ApproverCandidates(CancellationToken ct)
    {
        var tenant = _db.CurrentTenantSlug;
        if (string.IsNullOrEmpty(tenant)) return Ok(Array.Empty<object>());
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var rows = await _db.Database.SqlQueryRaw<WorkflowRouting.Emp>(
            "SELECT \"Id\", \"FirstName\", \"LastName\", '' AS \"Email\" FROM employee_employees WHERE \"TenantSlug\" = {0} " +
            "AND \"Status\" <> 'Terminated' AND \"KeycloakUserId\" IS NOT NULL ORDER BY \"FirstName\", \"LastName\"", tenant).ToListAsync(ct);
        return Ok(rows.Where(r => r.Id != me).Select(r => new { r.Id, r.FirstName, r.LastName, fullName = $"{r.FirstName} {r.LastName}" }));
    }

    [HttpGet("overdue")]
    public async Task<IActionResult> GetOverdue(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var q = _db.WorkflowRequests.AsQueryable();
        if (!IsHr)
        {
            var me = await _employees.FindMyEmployeeIdAsync(ct);
            if (me is null) return Forbid();
            q = VisibleTo(q, me.Value);
        }
        var overdue = await q
            .Where(w => w.Status == WorkflowStatus.Pending && w.SlaDueAt != null && w.SlaDueAt < now)
            .Include(w => w.Steps)
            .AsNoTracking()
            .ToListAsync(ct);
        await RedactHiddenFieldsAsync(overdue, ct);
        await AuditPlatformListAsync(overdue, "WorkflowRequestList", ct);
        return Ok(overdue);
    }
}

public record CreateWorkflowRequest(
    WorkflowType Type,
    Guid RequesterEmployeeId,
    string? Subject,
    string? Payload,
    List<Guid> ApproverEmployeeIds,
    int? SlaHours);

public record DecideRequest(StepDecision Decision, string? Comment);
public record InternalCreateWorkflowRequest(string TenantSlug, WorkflowType Type, Guid RequesterEmployeeId, string? Subject, string? Payload,
    List<Guid>? ApproverEmployeeIds, int? SlaHours);
public record InternalDecideRequest(string TenantSlug, Guid ActorEmployeeId, StepDecision Decision, string? Comment, string? Channel);
public record InternalCancelWorkflowRequest(string TenantSlug, Guid ActorEmployeeId);
public record DelegateRequest(Guid DelegateToEmployeeId, string? Comment);
