using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Directory;
using TenantService.Models;

namespace TenantService.Controllers;

/// <summary>
/// Y26: SCIM 2.0 sunucusu (RFC 7643/7644 alt kumesi). Disaridan: /api/tenant/scim/v2.
///
/// Kimlik dogrulama: kiraci yoneticisinin Ayarlar > Guvenlik > SCIM panelinde urettigi jeton
/// (Authorization: Bearer hr360scim_...; veritabaninda yalnizca SHA-256 ozeti). Keycloak JWT'si
/// BU UCLARDA kabul edilmez; jeton hangi kiraciya aitse yalnizca o kiracinin kullanicilari.
///
/// Kapsam: Users (liste + userName/externalId/id/emails.value eq filtresi, startIndex/count,
/// GET, POST, PUT, PATCH, DELETE = hesabi kapatma + oturumlari sonlandirma). Groups, Bulk,
/// sort, etag ve parola degistirme YOK (roller dizinden atanmaz; her hesap "employee").
/// KVKK: yalnizca ScimUserMapper.StoredAttributes saklanir, diger nitelikler yok sayilir.
/// </summary>
[ApiController]
[Route("api/scim/v2")]
[AllowAnonymous]
[EnableRateLimiting("scim")]
public class ScimController : ControllerBase
{
    private static readonly JsonSerializerOptions ScimJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int MaxPageSize = 200;

    private readonly TenantDbContext _db;
    private readonly ScimTokenService _tokens;
    private readonly DirectoryProvisioningService _prov;
    private readonly ILogger<ScimController> _log;

    private Tenant _tenant = null!;
    private DirectorySettings _settings = null!;

    public ScimController(TenantDbContext db, ScimTokenService tokens, DirectoryProvisioningService prov, ILogger<ScimController> log)
    {
        _db = db;
        _tokens = tokens;
        _prov = prov;
        _log = log;
    }

    /* ------------------------------------------------------------ altyapi */

    private static string BaseUrl =>
        (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "").TrimEnd('/') + "/api/tenant/scim/v2";

    private static ContentResult Scim(object body, int status = 200) => new()
    {
        StatusCode = status,
        ContentType = "application/scim+json; charset=utf-8",
        Content = JsonSerializer.Serialize(body, ScimJson),
    };

    private static ContentResult Error(int status, string? scimType, string detail) => Scim(new Dictionary<string, object>
    {
        ["schemas"] = new[] { ScimSchemas.Error },
        ["status"] = status.ToString(),
        ["detail"] = detail,
    }.Also(d => { if (scimType is not null) d["scimType"] = scimType; }), status);

    /// <summary>Jetonu dogrular, kiraciyi ve ayarlari yukler; hata varsa SCIM hata yaniti doner.</summary>
    private async Task<IActionResult?> AuthAsync(CancellationToken ct)
    {
        var token = await _tokens.ValidateAsync(Request.Headers.Authorization.ToString(), ct);
        if (token is null)
        {
            Response.Headers.WWWAuthenticate = "Bearer realm=\"hr360-scim\"";
            return Error(401, null, "Geçersiz ya da iptal edilmiş SCIM jetonu");
        }
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == token.TenantSlug, ct);
        if (tenant?.KeycloakOrgId is null) return Error(401, null, "Jetonun şirketi bulunamadı");
        if (tenant.Status is TenantStatus.Suspended or TenantStatus.Cancelled)
            return Error(403, null, "Şirket hesabı askıya alınmış");
        _tenant = tenant;
        _settings = await _prov.GetSettingsAsync(tenant.Slug, ct);
        // Denetim kaydi (audit_log) SCIM degisikliklerini jetonun adiyla iliskilendirir.
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "scim:" + token.Id),
            new Claim("name", "SCIM: " + token.Name),
            new Claim("organization", JsonSerializer.Serialize(new[] { tenant.Slug })),
        }, "Scim"));
        return null;
    }

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action, CancellationToken ct)
    {
        if (await AuthAsync(ct) is { } denied) return denied;
        try { return await action(); }
        catch (ScimException ex) { return Error(ex.Status, ex.ScimType, ex.Message); }
        catch (JsonException) { return Error(400, "invalidSyntax", "Gövde geçerli JSON değil"); }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("SCIM islemi kimlik sunucusunda tamamlanamadi: {Message}", ex.Message);
            return Error(502, null, "Kimlik sunucusundaki işlem tamamlanamadı; daha sonra yeniden deneyin");
        }
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is > 256 * 1024) throw ScimException.Invalid("Gövde çok büyük");
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private IQueryable<DirectoryUser> ScimUsers =>
        _db.DirectoryUsers.Where(u => u.TenantSlug == _tenant.Slug && u.Source == DirectorySources.Scim);

    private async Task<DirectoryUser> FindUserAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) throw new ScimException(404, null, "Kullanıcı bulunamadı");
        return await ScimUsers.FirstOrDefaultAsync(u => u.Id == gid, ct) ?? throw new ScimException(404, null, "Kullanıcı bulunamadı");
    }

    private static Dictionary<string, object?> UserResource(DirectoryUser u)
    {
        var schemas = new List<string> { ScimSchemas.User };
        var r = new Dictionary<string, object?>
        {
            ["id"] = u.Id.ToString(),
            ["userName"] = u.UserName,
            ["name"] = new { givenName = u.GivenName, familyName = u.FamilyName, formatted = $"{u.GivenName} {u.FamilyName}".Trim() },
            ["displayName"] = $"{u.GivenName} {u.FamilyName}".Trim(),
            ["emails"] = new[] { new { value = u.Email, type = "work", primary = true } },
            ["active"] = u.Active,
            ["meta"] = new
            {
                resourceType = "User",
                created = u.CreatedAt.UtcDateTime.ToString("o"),
                lastModified = u.UpdatedAt.UtcDateTime.ToString("o"),
                location = $"{BaseUrl}/Users/{u.Id}",
            },
        };
        if (u.ExternalId is not null) r["externalId"] = u.ExternalId;
        if (u.Title is not null) r["title"] = u.Title;
        if (u.Department is not null)
        {
            schemas.Add(ScimSchemas.EnterpriseUser);
            r[ScimSchemas.EnterpriseUser] = new { department = u.Department };
        }
        r["schemas"] = schemas;
        return r;
    }

    private static object ListResponse<T>(int total, int startIndex, IReadOnlyCollection<T> items) => new Dictionary<string, object>
    {
        ["schemas"] = new[] { ScimSchemas.ListResponse },
        ["totalResults"] = total,
        ["startIndex"] = startIndex,
        ["itemsPerPage"] = items.Count,
        ["Resources"] = items,
    };

    private static (int Start, int Count) Paging(int? startIndex, int? count) =>
        (Math.Max(1, startIndex ?? 1), Math.Clamp(count ?? 100, 0, MaxPageSize));

    /* ------------------------------------------------------------ kesif uclari */

    [HttpGet("ServiceProviderConfig")]
    public Task<IActionResult> ServiceProviderConfig(CancellationToken ct) => Run(() => Task.FromResult<IActionResult>(Scim(new Dictionary<string, object>
    {
        ["schemas"] = new[] { ScimSchemas.ServiceProviderConfig },
        ["patch"] = new { supported = true },
        ["bulk"] = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
        ["filter"] = new { supported = true, maxResults = MaxPageSize },
        ["changePassword"] = new { supported = false },
        ["sort"] = new { supported = false },
        ["etag"] = new { supported = false },
        ["authenticationSchemes"] = new[]
        {
            new { type = "oauthbearertoken", name = "OAuth Bearer Token", description = "HR360 Ayarlar › Güvenlik › SCIM panelinde üretilen kiracı jetonu", primary = true },
        },
        ["documentationUri"] = (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN") ?? "").TrimEnd('/') + "/panel/guvenlik",
        // KVKK veri minimizasyonu (standart dışı, bilgilendirme amaçlı uzantı).
        ["urn:hr360:params:scim:dataMinimisation"] = new
        {
            storedAttributes = ScimUserMapper.StoredAttributes,
            ignoredAttributes = "Diğer tüm nitelikler (phoneNumbers, addresses, photos, manager, employeeNumber, ...) kabul edilir ama saklanmaz.",
            notice = ScimUserMapper.MinimisationNoticeTr,
        },
        ["meta"] = new { resourceType = "ServiceProviderConfig", location = $"{BaseUrl}/ServiceProviderConfig" },
    })), ct);

    private static object UserResourceType() => new
    {
        schemas = new[] { ScimSchemas.ResourceType }, id = "User", name = "User", endpoint = "/Users",
        description = "HR360 kullanıcı hesabı (yalnızca saklanan nitelikler)", schema = ScimSchemas.User,
        schemaExtensions = new[] { new { schema = ScimSchemas.EnterpriseUser, required = false } },
        meta = new { resourceType = "ResourceType", location = $"{BaseUrl}/ResourceTypes/User" },
    };

    [HttpGet("ResourceTypes")]
    public Task<IActionResult> ResourceTypes(CancellationToken ct) =>
        Run(() => Task.FromResult<IActionResult>(Scim(ListResponse(1, 1, new[] { UserResourceType() }))), ct);

    [HttpGet("ResourceTypes/{name}")]
    public Task<IActionResult> ResourceType(string name, CancellationToken ct) => Run(() => Task.FromResult<IActionResult>(
        name == "User" ? Scim(UserResourceType()) : Error(404, null, "Kaynak türü bulunamadı")), ct);

    private static object Attr(string name, string type = "string", bool required = false, string mutability = "readWrite",
        bool caseExact = false, string uniqueness = "none", bool multiValued = false, object[]? sub = null) => new Dictionary<string, object>
    {
        ["name"] = name, ["type"] = type, ["multiValued"] = multiValued, ["required"] = required, ["caseExact"] = caseExact,
        ["mutability"] = mutability, ["returned"] = "default", ["uniqueness"] = uniqueness,
    }.Also(d => { if (sub is not null) d["subAttributes"] = sub; });

    private static object SchemaOf(string id) => id switch
    {
        ScimSchemas.User => SchemaDoc(id, "User", "HR360 kullanıcısı. " + ScimUserMapper.MinimisationNoticeTr, new[]
        {
            Attr("userName", required: true, uniqueness: "server"),
            Attr("externalId", caseExact: true),
            Attr("name", "complex", sub: new[] { Attr("givenName", required: true), Attr("familyName", required: true), Attr("formatted", mutability: "readOnly") }),
            Attr("displayName", mutability: "readOnly"),
            Attr("emails", "complex", required: true, multiValued: true, sub: new[] { Attr("value"), Attr("type"), Attr("primary", "boolean") }),
            Attr("title"),
            Attr("active", "boolean"),
        }),
        ScimSchemas.EnterpriseUser => SchemaDoc(id, "EnterpriseUser", "Kurumsal uzantı (yalnızca departman saklanır)", new[] { Attr("department") }),
        _ => throw new ScimException(404, null, "Şema bulunamadı"),
    };

    private static object SchemaDoc(string id, string name, string description, object[] attrs) => new
    {
        schemas = new[] { ScimSchemas.Schema }, id, name, description, attributes = attrs,
        meta = new { resourceType = "Schema", location = $"{BaseUrl}/Schemas/{id}" },
    };

    [HttpGet("Schemas")]
    public Task<IActionResult> Schemas(CancellationToken ct) => Run(() =>
    {
        var all = new[] { ScimSchemas.User, ScimSchemas.EnterpriseUser }.Select(SchemaOf).ToList();
        return Task.FromResult<IActionResult>(Scim(ListResponse(all.Count, 1, all)));
    }, ct);

    [HttpGet("Schemas/{id}")]
    public Task<IActionResult> Schema(string id, CancellationToken ct) => Run(() => Task.FromResult<IActionResult>(Scim(SchemaOf(id))), ct);

    /* ------------------------------------------------------------ Users */

    private static readonly string[] UserFilterAttrs = { "userName", "externalId", "id", "emails.value" };

    [HttpGet("Users")]
    public Task<IActionResult> ListUsers([FromQuery] string? filter, [FromQuery] int? startIndex, [FromQuery] int? count, CancellationToken ct) => Run(async () =>
    {
        var cond = ScimFilter.Parse(filter, UserFilterAttrs);
        var q = ScimUsers;
        if (cond is not null)
        {
            var v = cond.Value;
            var lower = v.ToLower();
            q = cond.Attribute switch
            {
                "userName" => q.Where(u => u.UserName.ToLower() == lower),
                "emails.value" => q.Where(u => u.Email.ToLower() == lower),
                "externalId" => q.Where(u => u.ExternalId == v),
                _ => Guid.TryParse(v, out var gid) ? q.Where(u => u.Id == gid) : q.Where(_ => false),
            };
        }
        var (start, size) = Paging(startIndex, count);
        var total = await q.CountAsync(ct);
        var page = size == 0 ? new List<DirectoryUser>()
            : await q.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Skip(start - 1).Take(size).ToListAsync(ct);
        return Scim(ListResponse(total, start, page.Select(UserResource).ToList()));
    }, ct);

    [HttpGet("Users/{id}")]
    public Task<IActionResult> GetUser(string id, CancellationToken ct) => Run(async () => Scim(UserResource(await FindUserAsync(id, ct))), ct);

    [HttpPost("Users")]
    public Task<IActionResult> CreateUser(CancellationToken ct) => Run(async () =>
    {
        var draft = ScimUserMapper.FromResource(await ReadBodyAsync(Request, ct));
        ScimUserMapper.Validate(draft);
        var du = await _prov.CreateAsync(_tenant, _settings, DirectorySources.Scim, draft, ct);
        Response.Headers.Location = $"{BaseUrl}/Users/{du.Id}";
        return Scim(UserResource(du), 201);
    }, ct);

    [HttpPut("Users/{id}")]
    public Task<IActionResult> ReplaceUser(string id, CancellationToken ct) => Run(async () =>
    {
        var du = await FindUserAsync(id, ct);
        var draft = ScimUserMapper.FromResource(await ReadBodyAsync(Request, ct));
        ScimUserMapper.Validate(draft);
        await _prov.UpdateAsync(_tenant, du, draft, ct);
        return Scim(UserResource(du));
    }, ct);

    [HttpPatch("Users/{id}")]
    public Task<IActionResult> PatchUser(string id, CancellationToken ct) => Run(async () =>
    {
        var du = await FindUserAsync(id, ct);
        var ops = ScimPatch.ParseOperations(await ReadBodyAsync(Request, ct));
        var draft = ScimPatch.ApplyToUser(DirectoryProvisioningService.ToDraft(du), ops);
        ScimUserMapper.Validate(draft);
        await _prov.UpdateAsync(_tenant, du, draft, ct);
        return Scim(UserResource(du));
    }, ct);

    /// <summary>
    /// SILME = hesabi kapatma. Calisan verisi ve dizin kaydi silinmez (saklama politikasi);
    /// kaynak active=false olarak kalir.
    /// </summary>
    [HttpDelete("Users/{id}")]
    public Task<IActionResult> DeleteUser(string id, CancellationToken ct) => Run(async () =>
    {
        var du = await FindUserAsync(id, ct);
        if (du.Active) await _prov.DeactivateAsync(_tenant, du, ct);
        return NoContent();
    }, ct);
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
