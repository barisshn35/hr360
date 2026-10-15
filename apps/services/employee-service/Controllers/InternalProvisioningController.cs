using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EmployeeService.Data;
using EmployeeService.Messaging;
using EmployeeService.Models;
using EmployeeService.Tenancy;

namespace EmployeeService.Controllers;

/// <summary>
/// Dalga 5d (Y26): dizin sağlama (SCIM 2.0 / LDAP-AD eşitlemesi) için servisler arası uç.
/// YALNIZCA tenant-service çağırır; X-Internal-Token (INTERNAL_SERVICE_TOKEN) ile korunur ve
/// gateway /api/*/internal/ yollarını dışarıya kapatır. Anahtar tanımsızsa uç kapalıdır (404).
///
/// Çalışan kaydını Keycloak hesabına göre oluşturur ya da günceller (upsert). KVKK veri
/// minimizasyonu: yalnızca ad, soyad, iş e-postası, (varsa) unvan ve departman alınır -
/// telefon, adres vb. bu uçtan hiç kabul edilmez. Kota, e-posta tekilliği ve "bir giriş
/// hesabı tek çalışana bağlı" kuralları normal uçlardakiyle aynıdır.
/// </summary>
[ApiController]
[AllowAnonymous]
public class InternalProvisioningController : ControllerBase
{
    private readonly EmployeeDbContext _db;
    private readonly TenantContext _tenant;

    public InternalProvisioningController(EmployeeDbContext db, TenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public record DirectoryUpsertRequest(
        string TenantSlug, string KeycloakUserId, string Email, string FirstName, string LastName,
        string? PositionTitle, Guid? DepartmentId);

    private bool Authorized() =>
        Security.InternalServiceToken.Matches(Request.Headers[Security.InternalServiceToken.Header].FirstOrDefault());

    private static string? Clean(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length > max ? s.Trim()[..max] : s.Trim());

    [HttpPost("/api/internal/employees/directory-upsert")]
    public async Task<IActionResult> Upsert([FromBody] DirectoryUpsertRequest body, CancellationToken ct)
    {
        if (!Authorized()) return NotFound();
        if (string.IsNullOrWhiteSpace(body.TenantSlug) || string.IsNullOrWhiteSpace(body.KeycloakUserId))
            return BadRequest(new { message = "Kiracı ve giriş hesabı zorunlu" });
        var first = Clean(body.FirstName, 100);
        var last = Clean(body.LastName, 100);
        var email = Clean(body.Email, 254)?.ToLowerInvariant();
        if (first is null || last is null) return BadRequest(new { message = "Ad ve soyad zorunlu" });
        if (email is null || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return BadRequest(new { message = "Geçerli bir e-posta adresi gerekli" });
        var title = Clean(body.PositionTitle, 200);

        // Kiracı yalnızca bu güvenilir iç çağrıdan gelir (JWT yok); sorgu filtresi buna göre çalışır.
        _tenant.TenantSlug = body.TenantSlug.Trim();
        _tenant.IsPlatformAdmin = false;
        var slug = _tenant.TenantSlug;

        var employee = await _db.Employees.Include(e => e.Assignments)
            .FirstOrDefaultAsync(e => e.KeycloakUserId == body.KeycloakUserId, ct);
        var created = false;
        var linked = false;
        if (employee is null)
        {
            // Hesap başka bir kiracıdaki çalışana bağlıysa asla devralınmaz.
            if (await _db.Employees.IgnoreQueryFilters().AnyAsync(e => e.KeycloakUserId == body.KeycloakUserId, ct))
                return Conflict(new { message = "Bu giriş hesabı başka bir çalışan kaydına bağlı" });
            employee = await _db.Employees.Include(e => e.Assignments).FirstOrDefaultAsync(e => e.Email.ToLower() == email, ct);
            if (employee is not null)
            {
                if (!string.IsNullOrEmpty(employee.KeycloakUserId))
                    return Conflict(new { message = "Bu e-postadaki çalışan kaydı başka bir giriş hesabına bağlı" });
                employee.KeycloakUserId = body.KeycloakUserId;
                linked = true;
            }
        }

        if (employee is null)
        {
            employee = new Employee
            {
                FirstName = first,
                LastName = last,
                Email = email,
                KeycloakUserId = body.KeycloakUserId,
                HireDate = DateOnly.FromDateTime(DateTime.UtcNow),
            };
            _db.Employees.Add(employee);
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Topic = EmployeeTopics.Events,
                EventType = EmployeeEventTypes.Hired,
                PartitionKey = employee.Id.ToString(),
                Payload = JsonSerializer.Serialize(new EmployeeHiredEvent(slug, employee.Id, employee.FirstName,
                    employee.LastName, employee.Email, employee.HireDate, DateTimeOffset.UtcNow)),
            });
            created = true;
        }
        else
        {
            if (!string.Equals(employee.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                if (await _db.Employees.AnyAsync(e => e.Id != employee.Id && e.Email.ToLower() == email, ct))
                    return Conflict(new { message = "Bu e-posta ile kayıtlı başka bir çalışan var" });
                employee.Email = email;
            }
            employee.FirstName = first;
            employee.LastName = last;
        }

        // Departman/unvan: yalnızca dizin bir departman bildirdiyse (kimliği tenant-service eşler).
        var assigned = false;
        if (body.DepartmentId is { } dep && dep != Guid.Empty)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var current = employee.Assignments.FirstOrDefault(a => a.EffectiveTo == null);
            Assignment? changed = null;
            if (current is null)
            {
                changed = new Assignment { EmployeeId = employee.Id, DepartmentId = dep, PositionTitle = title, EffectiveFrom = today };
                _db.Assignments.Add(changed);
            }
            else if (current.DepartmentId != dep || (title is not null && current.PositionTitle != title))
            {
                if (current.EffectiveFrom >= today)
                {
                    current.DepartmentId = dep;
                    current.PositionTitle = title ?? current.PositionTitle;
                    changed = current;
                }
                else
                {
                    current.EffectiveTo = today.AddDays(-1);
                    changed = new Assignment { EmployeeId = employee.Id, DepartmentId = dep, PositionTitle = title ?? current.PositionTitle, EffectiveFrom = today };
                    _db.Assignments.Add(changed);
                }
            }
            if (changed is not null)
            {
                assigned = true;
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    Topic = EmployeeTopics.Events,
                    EventType = EmployeeEventTypes.Assigned,
                    PartitionKey = employee.Id.ToString(),
                    Payload = JsonSerializer.Serialize(new EmployeeAssignedEvent(slug, employee.Id, changed.Id, changed.DepartmentId,
                        changed.PositionTitle, changed.EffectiveFrom, DateTimeOffset.UtcNow, employee.Email, employee.FirstName, employee.LastName)),
                });
            }
        }

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "Çalışan kaydı aynı anda başka bir istekle değişti; yeniden deneyin" });
        }
        return Ok(new { employeeId = employee.Id, created, linked, assigned });
    }
}
