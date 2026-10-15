using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TenantService.Data;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Tenant icindeki kullanicilarin rol yonetimi: listeleme, davet etme
/// (Keycloak hesabi olusturma), rol atama/kaldirma.
///
/// GUVENLIK:
///   - Cross-tenant koruma: employee-service zaten JWT'deki organization
///     claim'ine gore kendi tenant filtresini uyguluyor - EmployeeDirectoryClient
///     bu JWT'yi ILETIYOR, yani buradan cekilen liste otomatik olarak
///     SADECE cagiranin kendi tenant'ina ait. ANCAK rol/izin uclari hedefi
///     govdeden KeycloakUserId olarak aliyor - bu uclar icin hedefin
///     cagiranin Keycloak organizasyonunun uyesi oldugu ayrica dogrulanir
///     (bkz. EnsureTargetInMyTenantAsync).
///   - Atanabilir roller: SADECE employee/manager/accounting/hr-admin/tenant-admin.
///     platform-admin PLATFORM seviyesinde, bu uctan
///     ASLA atanamaz/kaldirilamaz (Keycloak admin konsolundan elle yapilir).
///   - Son yonetici kilidi: bir tenant'taki SON tenant-admin'in rolu
///     kaldirilamaz - tenant yoneticisiz kalmasin.
/// </summary>
[ApiController]
[Route("api/my-tenant/members")]
[Authorize]
public class TeamMembersController : ControllerBase
{
    private static readonly HashSet<string> AssignableRoles =
        new() { "employee", "manager", "accounting", "hr-admin", "tenant-admin" };

    /// <summary>
    /// roles.ts'teki Permission union'iyla BIREBIR ayni olmali - orasi
    /// frontend'in hangi izinleri "var" sayacagini, burasi hangilerinin
    /// bu uctan gecerli sayilacagini belirliyor. Ikisi ayrisirsa,
    /// kullanici arayuzde gormedigi bir izni (gecerli oldugu icin)
    /// atayabilir ya da tam tersi bir izni gecerli sanip deneyip
    /// BadRequest alir.
    /// </summary>
    private static readonly HashSet<string> ExtraAssignablePermissions = new()
    {
        "organization:view", "organization:manage",
        "employee:viewAll", "employee:manage", "employee:create",
        "workflow:view", "workflow:create", "workflow:decide",
        "leave:view", "leave:create", "leave:manageBalance",
        "recruitment:view", "recruitment:candidates", "recruitment:publish",
        "onboarding:view", "onboarding:manage", "asset:manage",
        "timeshift:view", "timeshift:clock", "timeshift:manage",
        "performance:view", "performance:manage",
        "team:manage",
        "learning:view", "learning:enroll", "learning:manage",
        "compensation:view",
        "expense:view", "expense:create", "expense:manage", "expense:markPaid",
        "document:manage",
        "case:view", "case:create", "case:manage",
        "notification:view", "notification:manage",
        // İSG: işyeri hekimi sağlık notlarını okur/yazar; İSG uzmanı kaza ve eğitim kayıtlarını yönetir.
        "osh:physician", "osh:specialist",
        "platform:manage",
        "tenant:manage",
    };

    private readonly TenantDbContext _db;
    private readonly EmployeeDirectoryClient _employees;
    private readonly KeycloakAdminClient _keycloak;

    private readonly ILogger<TeamMembersController> _logger;

    public TeamMembersController(
        TenantDbContext db, EmployeeDirectoryClient employees, KeycloakAdminClient keycloak, ILogger<TeamMembersController> logger)
    {
        _logger = logger;
        _db = db;
        _employees = employees;
        _keycloak = keycloak;
    }

    /// <summary>Uye satiri: calisan kaydi olan ya da olmayan (yalnizca Keycloak hesabi) kullanici.</summary>
    public sealed record MemberView(Guid? EmployeeId, string? KeycloakUserId, string FirstName, string LastName, string Email,
        bool HasLoginAccess, bool HasEmployeeRecord, List<string> AllRoles);

    /// <summary>
    /// Calisan kaydi olmayan organizasyon uyeleri (saf; birim testli): ornegin yalnizca yonetim icin acilmis
    /// demo.admin hesabi. Calisan kaydina bagli hesaplar ve kimligi bos uyeler cikarilir.
    /// </summary>
    public static List<KeycloakAdminClient.OrgMember> AccountsWithoutEmployee(
        IEnumerable<string?> linkedKeycloakIds, IEnumerable<KeycloakAdminClient.OrgMember> orgMembers)
    {
        var linked = linkedKeycloakIds.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal);
        return orgMembers.Where(m => !string.IsNullOrEmpty(m.Id) && !linked.Contains(m.Id))
            .DistinctBy(m => m.Id).ToList();
    }

    /// <summary>
    /// Tenant'taki tum calisanlar + calisan kaydi olmayan organizasyon uyeleri (rol sahibi hesaplar).
    /// Erisim gozden gecirme, gorevler ayriligi uyarisi ve son yonetici kilidi bu listeye dayanir;
    /// calisan kaydi olmayan bir yonetici hesabi gorunmez kalmamali.
    /// </summary>
    private async Task<List<MemberView>> BuildMembersAsync(CancellationToken ct)
    {
        var employees = await _employees.GetAllAsync(ct);
        var result = new List<MemberView>();
        foreach (var e in employees)
        {
            List<string> roles = new();
            if (!string.IsNullOrEmpty(e.KeycloakUserId))
                roles = await _keycloak.GetUserRealmRolesAsync(e.KeycloakUserId, ct);
            result.Add(new MemberView(e.Id, e.KeycloakUserId, e.FirstName, e.LastName, e.Email,
                !string.IsNullOrEmpty(e.KeycloakUserId), true, roles));
        }

        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        var orgId = string.IsNullOrWhiteSpace(slug) ? null
            : await _db.Tenants.AsNoTracking().Where(t => t.Slug == slug).Select(t => t.KeycloakOrgId).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(orgId)) return result;
        List<KeycloakAdminClient.OrgMember> orgMembers;
        try { orgMembers = await _keycloak.ListOrganizationMembersAsync(orgId, ct); }
        catch (InvalidOperationException) { return result; } // Keycloak okunamazsa eski davranis (yalnizca calisanlar)
        foreach (var m in AccountsWithoutEmployee(employees.Select(e => e.KeycloakUserId), orgMembers))
        {
            var roles = await _keycloak.GetUserRealmRolesAsync(m.Id, ct);
            if (roles.Contains("platform-admin")) continue; // platform hesabi kiracinin uyesi sayilmaz
            result.Add(new MemberView(null, m.Id, m.FirstName ?? m.Username ?? "", m.LastName ?? "", m.Email ?? "",
                m.Enabled, false, roles));
        }
        return result;
    }

    /// <summary>Tenant'taki tum calisanlar ve calisan kaydi olmayan hesaplar + Keycloak rolleri.</summary>
    [HttpGet]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var members = await BuildMembersAsync(ct);
        return Ok(members.Select(m => new
        {
            employeeId = m.EmployeeId,
            keycloakUserId = m.KeycloakUserId,
            firstName = m.FirstName,
            lastName = m.LastName,
            email = m.Email,
            hasLoginAccess = m.HasLoginAccess,
            hasEmployeeRecord = m.HasEmployeeRecord,
            roles = m.AllRoles.Where(r => AssignableRoles.Contains(r)),
            // "ext-compensation-view" -> "compensation:view". Sadece
            // ExtraAssignablePermissions'ta hala gecerli olanlari
            // gosteriyoruz - roles.ts'ten kaldirilmis eski bir izin
            // Keycloak'ta rol olarak kalmis olsa bile arayuzde
            // tanimasiz bir sey gorunmemeli.
            extraPermissions = m.AllRoles
                .Where(r => r.StartsWith("ext-"))
                .Select(r => r["ext-".Length..].Replace('-', ':'))
                .Where(p => ExtraAssignablePermissions.Contains(p)),
        }));
    }

    /// <summary>
    /// Calisana giris erisimi verir: Keycloak'ta kullanici yoksa olusturur,
    /// tenant organizasyonuna ekler, employee-service'teki kaydi Keycloak
    /// user ID'siyle iliskilendirir, parola belirleme e-postasi gonderir.
    /// Zaten hesabi varsa (baska bir yoldan olusturulmus) sadece iliskiyi kurar.
    /// </summary>
    [HttpPost("{employeeId:guid}/invite")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Invite(Guid employeeId, [FromServices] PasswordLinkService links, CancellationToken ct)
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug))
            return NotFound(new { message = "Kullanıcı bir şirkete bağlı değil" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null || tenant.KeycloakOrgId is null)
            return NotFound(new { message = "Tenant kaydı veya Keycloak organizasyonu bulunamadı" });
        if (tenant.Status == TenantService.Models.TenantStatus.Suspended)
            return Conflict(new { message = "Şirket askıya alınmış; yeni kullanıcı davet edilemez" });

        var employee = await _employees.GetByIdAsync(employeeId, ct);
        if (employee is null)
            return NotFound(new { message = "Çalışan bulunamadı" });

        if (!string.IsNullOrEmpty(employee.KeycloakUserId))
            return Ok(new { message = "Bu çalışanın zaten giriş erişimi var", keycloakUserId = employee.KeycloakUserId });

        var existingUserId = await _keycloak.FindUserByEmailAsync(employee.Email, ct);
        string userId;
        if (existingUserId is not null)
        {
            // GUVENLIK: Onceden bu e-postaya sahip HERHANGI bir Keycloak hesabi
            // (baska bir kiracinin kullanicisi, platform hesabi) sorgusuz bu
            // sirkete baglaniyor, kimligi yanitta donduruluyor ve o kisiye parola
            // sifirlama e-postasi gidiyordu. Artik yalnizca ZATEN bu sirketin
            // uyesi olan hesap baglanabilir; digerleri icin temiz bir 409.
            if (!await _keycloak.IsOrganizationMemberAsync(tenant.KeycloakOrgId, existingUserId, ct))
                return Conflict(new
                {
                    message = "Bu e-posta adresi başka bir hesapta kullanılıyor. " +
                              "Çalışan için farklı bir e-posta adresi kullanın.",
                });
            userId = existingUserId;
        }
        else
        {
            userId = await _keycloak.CreateUserAsync(
                employee.Email, employee.FirstName, employee.LastName, tenant.Id, ct);
        }

        if (existingUserId is null)
            await _keycloak.AddOrganizationMemberAsync(tenant.KeycloakOrgId, userId, ct);
        await _keycloak.AssignRealmRoleAsync(userId, "employee", ct);
        await _employees.LinkKeycloakUserAsync(employeeId, userId, ct);

        // Parola: süreli davet linki yöneticiye bir kez gösterilir (e-posta açıksa ayrıca e-postayla gider);
        // şirket eski yöntemi seçtiyse Keycloak e-postası, gönderilemezse yine link.
        PasswordLinkService.Issued? issued = null;
        try
        {
            issued = await links.IssueAsync(new PasswordLinkService.IssueRequest(
                tenant, userId, employee.Email, $"{employee.FirstName} {employee.LastName}".Trim(),
                TenantService.Models.PasswordInvitePurposes.Setup, LinkDisplay.Show, PasswordLinkService.ActorOf(User)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hesap ve bağlantı geçerli kalır; link "Davetler" listesinden yeniden üretilebilir.
            _logger.LogWarning("Davet linki üretilemedi: {Message}", ex.Message);
        }

        return Ok(new
        {
            message = issued is null ? "Giriş erişimi açıldı; davet linki üretilemedi, Davetler listesinden yeniden üretin" : "Giriş erişimi açıldı",
            keycloakUserId = userId,
            passwordLink = issued is null ? null : PasswordLinkView.From(issued),
        });
    }

    public record RoleActionRequest(string KeycloakUserId, string Role);

    [HttpPost("roles")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> AssignRole([FromBody] RoleActionRequest request, CancellationToken ct)
    {
        if (await EnsureTargetInMyTenantAsync(request.KeycloakUserId, ct) is { } denied) return denied;

        if (!AssignableRoles.Contains(request.Role))
            return BadRequest(new { message = $"'{request.Role}' bu uctan atanamaz" });

        // Yetki yukseltme koruması: "tenant-admin" rolunu SADECE bir
        // tenant-admin verebilir - hr-admin kendinden daha yuksek bir
        // rolu baskasina VEREMEZ.
        if (request.Role == "tenant-admin" && !User.IsInRole("tenant-admin") && !User.IsInRole("platform-admin"))
            return Forbid();

        await _keycloak.AssignRealmRoleAsync(request.KeycloakUserId, request.Role, ct);
        return Ok(new { message = "Rol atandı" });
    }

    [HttpDelete("roles")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> RemoveRole([FromBody] RoleActionRequest request, CancellationToken ct)
    {
        if (await EnsureTargetInMyTenantAsync(request.KeycloakUserId, ct) is { } denied) return denied;

        if (!AssignableRoles.Contains(request.Role))
            return BadRequest(new { message = $"'{request.Role}' bu uctan kaldırılamaz" });

        if (request.Role == "tenant-admin")
        {
            if (!User.IsInRole("tenant-admin") && !User.IsInRole("platform-admin"))
                return Forbid();

            var adminCount = await CountTenantAdminsAsync(ct);
            if (adminCount <= 1)
                return Conflict(new
                {
                    message = "Bu, tenant'taki son şirket yöneticisi. Rol kaldırılamaz - " +
                              "önce başka birine şirket yöneticisi rolü verin.",
                });
        }

        await _keycloak.RemoveRealmRoleAsync(request.KeycloakUserId, request.Role, ct);
        return Ok(new { message = "Rol kaldırıldı" });
    }

    public record PermissionActionRequest(string KeycloakUserId, string Permission);

    /// <summary>
    /// Tek bir izni (roles.ts'teki Permission kodlarindan biri, orn.
    /// "compensation:view"), kullanicinin rolunden BAGIMSIZ olarak dogrudan
    /// atar - "employee" rolundeki biri, hr-admin'in TUM 9 iznini degil,
    /// SADECE "compensation:view"i alabilir.
    ///
    /// GUVENLIK: standart rol atamadan (RequireHrAdmin) daha kisitli -
    /// SADECE tenant-admin/platform-admin cagirabilir. Bu, bilincli bir
    /// tasarim karari: "serbest, rolun disinda tek tek izin verme" gucu,
    /// yalnizca en ust iki role ait olmali - hr-admin standart 5 rolu
    /// atayabilir ama bu ek/serbest mekanizmayi kullanamaz.
    ///
    /// Not: Bu ucun izin verdigi Keycloak rolu (ext-&lt;permission&gt;)
    /// sadece TENANT-SERVICE tarafinda gecerlilik kontrolunden geciyor;
    /// ilgili backend servisinin bu ek rolu ayrica tanimasi (yetkilendirme
    /// policy'sine dahil etmesi) gerekir. Hangi izinlerin uctan uca
    /// desteklendigi web istemcisindeki PERMISSIONS_WITH_BACKEND_SUPPORT
    /// listesinde tutulur; Roller sayfasi yalnizca bunlari sunar.
    /// </summary>
    [HttpPost("permissions")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> AssignExtraPermission(
        [FromBody] PermissionActionRequest request, CancellationToken ct)
    {
        if (await EnsureTargetInMyTenantAsync(request.KeycloakUserId, ct) is { } denied) return denied;

        if (!ExtraAssignablePermissions.Contains(request.Permission))
            return BadRequest(new { message = $"'{request.Permission}' geçerli bir izin değil" });

        // GUVENLIK/yetki yukseltme korumasi: "platform:manage" en ust
        // seviye izin - RequireTenantAdminOnly bir tenant-admin'in de
        // gecmesine izin veriyor, ama bir tenant-admin, PLATFORM-admin
        // OLMAYAN biri olarak, baskasina platform:manage veremez. AssignRole'
        // daki "tenant-admin rolunu sadece tenant-admin verebilir" korumasiyla
        // ayni mantik - burada bir ust seviye: platform:manage'i sadece
        // GERCEK platform-admin verebilir.
        if (request.Permission == "platform:manage" && !User.IsInRole("platform-admin"))
            return Forbid();

        var roleName = $"ext-{request.Permission.Replace(':', '-')}";
        await _keycloak.EnsureRealmRoleExistsAsync(roleName, ct);
        await _keycloak.AssignRealmRoleAsync(request.KeycloakUserId, roleName, ct);
        return Ok(new { message = "İzin atandı" });
    }

    [HttpDelete("permissions")]
    [Authorize(Policy = "RequireTenantAdminOnly")]
    public async Task<IActionResult> RemoveExtraPermission(
        [FromBody] PermissionActionRequest request, CancellationToken ct)
    {
        if (await EnsureTargetInMyTenantAsync(request.KeycloakUserId, ct) is { } denied) return denied;

        if (!ExtraAssignablePermissions.Contains(request.Permission))
            return BadRequest(new { message = $"'{request.Permission}' geçerli bir izin değil" });

        var roleName = $"ext-{request.Permission.Replace(':', '-')}";
        await _keycloak.RemoveRealmRoleAsync(request.KeycloakUserId, roleName, ct);
        return Ok(new { message = "İzin kaldırıldı" });
    }

    /// <summary>
    /// GUVENLIK: Rol/izin degisikliginin hedefi CAGIRANIN kiracisina ait olmali.
    /// Onceki halinde KeycloakUserId dogrudan istek govdesinden aliniyor ve hic
    /// dogrulanmiyordu; realm rolleri tum kiracilarda ortak oldugu icin, anonim
    /// kayitla kendi sirketini acan biri baska bir sirketteki bir kullaniciya
    /// (ornegin kendi calisan hesabina) tenant-admin verebiliyor ya da o sirketin
    /// yoneticilerinin rollerini silebiliyordu (canli dogrulandi: "kotu" kiracisinin
    /// yoneticisi "demo"daki Mehmet'e hr-admin verdi / manager rolunu sildi).
    /// Hedef, cagiranin kiracisinin Keycloak organizasyonunun uyesi olmali.
    /// Platform-admin (platform operatoru) bu kontrolden muaftir.
    /// null donerse islem serbest; aksi halde donen sonuc aynen dondurulmeli.
    /// </summary>
    private async Task<IActionResult?> EnsureTargetInMyTenantAsync(string? keycloakUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keycloakUserId))
            return BadRequest(new { message = "keycloakUserId zorunlu" });
        if (User.IsInRole("platform-admin")) return null;

        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(User.FindFirst("organization")?.Value);
        if (string.IsNullOrWhiteSpace(slug)) return Forbid();
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant?.KeycloakOrgId is null) return Forbid();

        // Varligini sizdirmamak icin "baska kiracida" ile "hic yok" ayni cevap.
        return await _keycloak.IsOrganizationMemberAsync(tenant.KeycloakOrgId, keycloakUserId, ct)
            ? null
            : NotFound(new { message = "Kullanıcı bu şirkette bulunamadı" });
    }

    /// <summary>
    /// KRITIK: N+1 sorgu - her calisan icin Keycloak'a ayri istek atar.
    /// Tenant'lar genelde kucuk oldugu ve bu SIK cagrilan bir uc olmadigi
    /// icin (sadece rol kaldirma oncesi guvenlik kontrolu) kabul edilebilir.
    /// Cok buyuk tenant'larda (yuzlerce kullanici) performans sorunu
    /// yaratirsa, Keycloak'in "get role members" ucuna gecilebilir.
    /// </summary>
    private async Task<int> CountTenantAdminsAsync(CancellationToken ct) =>
        (await BuildMembersAsync(ct)).Count(m => m.HasLoginAccess && m.AllRoles.Contains("tenant-admin"));
}
