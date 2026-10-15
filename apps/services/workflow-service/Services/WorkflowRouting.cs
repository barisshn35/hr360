using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkflowService.Data;
using WorkflowService.Messaging;
using WorkflowService.Models;

namespace WorkflowService.Services;

/// <summary>
/// Onay yönlendirme: akış tanımına göre onaycı zinciri, vekâlet, adım ataması (bildirim olayı
/// ve e-posta karar jetonu) ve süre aşımında üst yöneticiye iletme. Çalışan/departman bilgisi
/// aynı veritabanından kiracı filtresiyle okunur (arka plan işinde de çalışır).
/// </summary>
public class WorkflowRouting
{
    private readonly WorkflowDbContext _db;
    public WorkflowRouting(WorkflowDbContext db) => _db = db;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public sealed class Emp
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public Task<Emp?> EmployeeAsync(string tenant, Guid id, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<Emp>(
            "SELECT \"Id\", \"FirstName\", \"LastName\", \"Email\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = {1}",
            tenant, id).FirstOrDefaultAsync(ct);

    /// <summary>Kişinin aktif departmanının başı; parent=true ise üst departmanın başı.</summary>
    public async Task<Guid?> HeadOfAsync(string tenant, Guid employeeId, bool parent, CancellationToken ct)
    {
        var sql = parent
            ? """
              SELECT pd."HeadEmployeeId" AS "Value" FROM employee_assignments a
              JOIN organization_departments d ON d."Id" = a."DepartmentId"
              JOIN organization_departments pd ON pd."Id" = d."ParentDepartmentId"
              WHERE a."TenantSlug" = {0} AND a."EmployeeId" = {1} AND a."EffectiveTo" IS NULL
              ORDER BY a."EffectiveFrom" DESC LIMIT 1
              """
            : """
              SELECT d."HeadEmployeeId" AS "Value" FROM employee_assignments a
              JOIN organization_departments d ON d."Id" = a."DepartmentId"
              WHERE a."TenantSlug" = {0} AND a."EmployeeId" = {1} AND a."EffectiveTo" IS NULL
              ORDER BY a."EffectiveFrom" DESC LIMIT 1
              """;
        var head = await _db.Database.SqlQueryRaw<Guid?>(sql, tenant, employeeId).FirstOrDefaultAsync(ct);
        // Kişi kendi departmanının başıysa onu onaylayacak kişi üst departmanın başıdır.
        if (!parent && head == employeeId) return await HeadOfAsync(tenant, employeeId, parent: true, ct);
        return head;
    }

    /// <summary>
    /// Giriş hesabı olan ve işten ayrılmamış çalışanlar (verilen kimlikler arasından). Onaycı,
    /// kararı ancak HR360'a girerek verebilir: hesabı olmayan ya da ayrılmış kişiye giden adım
    /// sonsuza dek askıda kalırdı.
    /// </summary>
    public async Task<HashSet<Guid>> ActiveLoginEmployeesAsync(string tenant, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var arr = ids.Distinct().ToArray();
        var rows = await _db.Database.SqlQueryRaw<Guid>(
            "SELECT \"Id\" AS \"Value\" FROM employee_employees WHERE \"TenantSlug\" = {0} AND \"Id\" = ANY({1}) " +
            "AND \"Status\" <> 'Terminated' AND \"KeycloakUserId\" IS NOT NULL",
            tenant, arr).ToListAsync(ct);
        return rows.ToHashSet();
    }

    /// <summary>Kiracının İK onaycısı (onay ayarları); tanımlı değilse null.</summary>
    public Task<Guid?> HrApproverAsync(string tenant, CancellationToken ct) =>
        _db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantSlug == tenant)
            .Select(s => s.HrApproverEmployeeId).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Modül talebinde onaycı belirlenemediğinde (departman başı yok ya da talep eden departman
    /// başı ve üst departman yok) kim onaylar: bölüm başı → üst bölüm başı → İK onaycısı.
    /// Kimse yoksa null (talep açılamaz; çağıran servis kendi kaydını elle sonuçlandırmaya bırakır).
    /// </summary>
    public async Task<Guid?> FallbackApproverAsync(string tenant, Guid requester, CancellationToken ct)
    {
        var head = await HeadOfAsync(tenant, requester, parent: false, ct);
        if (head is { } h && h != Guid.Empty && h != requester && (await ActiveLoginEmployeesAsync(tenant, new[] { h }, ct)).Contains(h))
            return h;
        var hr = await HrApproverAsync(tenant, ct);
        if (hr is { } x && x != Guid.Empty && x != requester && (await ActiveLoginEmployeesAsync(tenant, new[] { x }, ct)).Contains(x))
            return x;
        return null;
    }

    public static decimal? PayloadNumber(string? payload, ConditionField field)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var names = field switch
            {
                ConditionField.Days => new[] { "days" },
                ConditionField.Amount => new[] { "amount", "totalAmount", "total" },
                _ => new[] { "hours" },
            };
            foreach (var p in doc.RootElement.EnumerateObject())
                if (names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.Number)
                    return p.Value.GetDecimal();
        }
        catch (JsonException) { }
        return null;
    }

    public static bool ConditionHolds(DefinitionStep s, string? payload)
    {
        if (s.ConditionField is null || s.ConditionValue is null || string.IsNullOrEmpty(s.ConditionOp)) return true;
        var v = PayloadNumber(payload, s.ConditionField.Value);
        if (v is null) return false; // koşul alanı yoksa koşullu adım atlanır
        return s.ConditionOp switch
        {
            ">" => v > s.ConditionValue, ">=" => v >= s.ConditionValue,
            "<" => v < s.ConditionValue, "<=" => v <= s.ConditionValue, _ => true,
        };
    }

    /// <summary>
    /// Etkin tanım varsa onaycı zinciri: koşulu sağlanan adımlar sırayla, talep eden ve art arda
    /// tekrarlanan onaycılar atlanır. Tanım yoksa ya da kimse bulunamazsa null (çağıranın listesi kullanılır).
    /// </summary>
    public async Task<List<(Guid Approver, int? SlaHours)>?> ResolveAsync(string tenant, WorkflowType type, Guid requester, string? payload, CancellationToken ct)
    {
        var def = await _db.Definitions.AsNoTracking().Where(d => d.Type == type && d.IsActive).OrderByDescending(d => d.UpdatedAt).FirstOrDefaultAsync(ct);
        if (def is null) return null;
        var steps = JsonSerializer.Deserialize<List<DefinitionStep>>(def.StepsJson, Json) ?? new();
        var chain = new List<(Guid, int?)>();
        foreach (var s in steps.Where(s => ConditionHolds(s, payload)))
        {
            Guid? who = s.Approver switch
            {
                ApproverKind.DepartmentHead => await HeadOfAsync(tenant, requester, parent: false, ct),
                ApproverKind.ParentDepartmentHead => await HeadOfAsync(tenant, requester, parent: true, ct),
                _ => s.EmployeeId,
            };
            if (who is null || who == Guid.Empty || who == requester) continue;
            if (chain.Count > 0 && chain[^1].Item1 == who) continue;
            chain.Add((who.Value, s.SlaHours));
        }
        return chain.Count == 0 ? null : chain;
    }

    /// <summary>Kişinin bugün geçerli vekâleti (varsa).</summary>
    public Task<Delegation?> ActiveDelegationAsync(Guid fromEmployee, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        return _db.Delegations.Where(d => d.FromEmployeeId == fromEmployee && d.RevokedAt == null && d.StartDate <= today && d.EndDate >= today)
            .OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync(ct);
    }

    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>
    /// Sıra bu adıma geldi: vekâlet uygulanır, SLA kurulur, e-posta karar jetonu üretilir ve
    /// bildirim olayı (workflow.submitted) etkin onaycıya yayınlanır. Kaydetmez.
    /// </summary>
    public async Task AssignAsync(WorkflowRequest wf, ApprovalStep step, string tenant, CancellationToken ct, Guid? escalateTo = null)
    {
        if (escalateTo is null && step.DelegatedToEmployeeId is null && await ActiveDelegationAsync(step.ApproverEmployeeId, ct) is { } del
            && del.ToEmployeeId != wf.RequesterEmployeeId)
        {
            step.DelegatedToEmployeeId = del.ToEmployeeId;
            step.DelegationId = del.Id;
        }
        if (escalateTo is not null)
        {
            step.DelegatedToEmployeeId = escalateTo;
            step.DelegationId = null;
            step.EscalatedAt = DateTimeOffset.UtcNow;
        }
        if (step.SlaHours is > 0) wf.SlaDueAt = DateTimeOffset.UtcNow.AddHours(step.SlaHours.Value);

        var target = step.DelegatedToEmployeeId ?? step.ApproverEmployeeId;
        var approver = await EmployeeAsync(tenant, target, ct);
        if (approver is null) return;
        var requester = await EmployeeAsync(tenant, wf.RequesterEmployeeId, ct);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        step.ActionTokenHash = Sha(token);
        step.ActionTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(72);
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = WorkflowTopics.Events,
            EventType = WorkflowEventTypes.Submitted,
            PartitionKey = wf.Id.ToString(),
            Payload = JsonSerializer.Serialize(new WorkflowSubmittedEvent(
                tenant, wf.Id, wf.Type.ToString(), wf.RequesterEmployeeId,
                requester is null ? null : $"{requester.FirstName} {requester.LastName}",
                wf.Subject, target, approver.Email, approver.FirstName, wf.SlaDueAt, DateTimeOffset.UtcNow,
                ActionToken: $"{step.Id:N}.{token}",
                OnBehalfOfEmployeeId: step.DelegatedToEmployeeId is null ? null : step.ApproverEmployeeId,
                Escalated: escalateTo is not null)),
        });
    }

    /// <summary>Jeton "adımKimliği.gizli" biçiminde; adımı ve geçerliliği döner.</summary>
    public async Task<ApprovalStep?> StepForTokenAsync(string token, CancellationToken ct)
    {
        var parts = (token ?? "").Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var stepId)) return null;
        var step = await _db.ApprovalSteps.IgnoreQueryFilters().Include(s => s.WorkflowRequest).ThenInclude(w => w!.Steps)
            .FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (step?.ActionTokenHash is null || step.ActionTokenExpiresAt < DateTimeOffset.UtcNow) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(step.ActionTokenHash), Encoding.ASCII.GetBytes(Sha(parts[1]))) ? step : null;
    }
}
