using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EmployeeService.Data;
using EmployeeService.Models;
using EmployeeService.Messaging;
using EmployeeService.Services;
using EmployeeService.Infrastructure;
using EmployeeService.Auditing;
using EmployeeService.Tenancy;
using System.Text.Json;

namespace EmployeeService.Controllers;

[ApiController]
[Route("api/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly EmployeeDbContext _db;
    private readonly OrganizationDirectoryClient _organizations;
    private readonly ITenantContext _tenant;

    public EmployeesController(EmployeeDbContext db, OrganizationDirectoryClient organizations, ITenantContext tenant)
    {
        _db = db;
        _organizations = organizations;
        _tenant = tenant;
    }

    /// <remarks>
    /// Sayfalama (G24): <c>page</c> verilmezse eski biçim (düz dizi, en fazla 2000 kayıt,
    /// toplam <c>X-Total-Count</c> başlığında). <c>page</c>/<c>pageSize</c> (en fazla 200) ile
    /// <c>{ items, total, page, pageSize }</c>. Filtreler: <c>q</c> (ad, soyad, e-posta, güncel
    /// pozisyon; <c>qDepartmentIds</c> verilirse güncel departmanı bunlardan biri olanlar da
    /// eşleşir - departman adları organization-service'te olduğundan istemci eşleyip gönderir),
    /// <c>status</c>. Sıralama: <c>sort</c>=name|email|hireDate|status|createdAt, <c>dir</c>=asc|desc.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? email, [FromQuery] int? page, [FromQuery] int? pageSize,
        [FromQuery] string? q, [FromQuery] string? qDepartmentIds, [FromQuery] EmployeeStatus? status,
        [FromQuery] string? sort, [FromQuery] string? dir, CancellationToken ct)
    {
        // manager+ serbestce sorgular (email'siz = tum liste dahil).
        // "employee" rolu ise SADECE kendi kaydini (JWT'deki email'iyle
        // eslesen) sorgulayabilir - boylece "benim iznim/masrafim" gibi
        // ekranlarda EmployeePicker kendi kaydini cozebilir, ama tum
        // calisan listesine erisemez.
        // Tum listeyi gorebilenler: yonetici+ ve calisan listesine ihtiyac duyan ek
        // izinler (bkz. IsManagerOrAbove). Onceden burada ayri, ek izinleri tanimayan
        // bir rol listesi vardi; ek izin verilen kisiler formlarda calisan secemiyordu.
        if (!IsManagerOrAbove)
        {
            // MapInboundClaims (varsayılan true) "email" claim'ini .NET'in
            // kendi URI formatına donusturuyor - hem ham hem donusturulmus
            // ismi deniyoruz, servisin claim mapping ayarindan bagimsiz olsun.
            var callerEmail = User.FindFirst("email")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            if (string.IsNullOrEmpty(callerEmail)
                || string.IsNullOrEmpty(email)
                || !string.Equals(email, callerEmail, StringComparison.OrdinalIgnoreCase))
                return Forbid();
        }

        var query = _db.Employees.Include(e => e.Assignments).AsQueryable();

        // E-posta filtresi: token sahibini calisan kaydiyla eslestirmek icin.
        // Olmadiginda cagiran tum listeyi cekip istemcide filtrelemek
        // zorunda kaliyordu - binlerce calisanli kiracida sorun olurdu.
        if (!string.IsNullOrWhiteSpace(email))
            query = query.Where(e => e.Email.ToLower() == email.ToLower());
        if (status.HasValue) query = query.Where(e => e.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = Paging.FoldLike(q);
            var deps = Paging.Guids(qDepartmentIds);
            query = query.Where(e =>
                EF.Functions.Like(EmployeeDbContext.Fold(e.FirstName + " " + e.LastName), like, "\\")
                || EF.Functions.Like(EmployeeDbContext.Fold(e.Email), like, "\\")
                || e.Assignments.Any(a => a.EffectiveTo == null
                    && (EF.Functions.Like(EmployeeDbContext.Fold(a.PositionTitle ?? ""), like, "\\") || deps.Contains(a.DepartmentId))));
        }

        var desc = Paging.Desc(dir);
        IOrderedQueryable<Employee> ordered = (sort ?? "name").ToLowerInvariant() switch
        {
            "email" => desc ? query.OrderByDescending(e => e.Email) : query.OrderBy(e => e.Email),
            "hiredate" => desc ? query.OrderByDescending(e => e.HireDate) : query.OrderBy(e => e.HireDate),
            "status" => desc ? query.OrderByDescending(e => e.Status) : query.OrderBy(e => e.Status),
            "createdat" => desc ? query.OrderByDescending(e => e.CreatedAt) : query.OrderBy(e => e.CreatedAt),
            _ => desc ? query.OrderByDescending(e => e.FirstName).ThenByDescending(e => e.LastName)
                      : query.OrderBy(e => e.FirstName).ThenBy(e => e.LastName),
        };
        var result = await Paging.ListAsync(this, ordered.ThenBy(e => e.Id), page, pageSize, ct);
        if (_tenant.IsPlatformAdmin)
        {
            // Platform yöneticisi erişimi: kiracı başına tek satır, yalnızca sayı (kimlik yazılmaz).
            var rows = (result as OkObjectResult)?.Value switch
            {
                List<Employee> l => l,
                PagedResult<Employee> pr => pr.Items,
                _ => (IReadOnlyList<Employee>)Array.Empty<Employee>(),
            };
            var perTenant = rows
                .Where(e => PlatformAccessAudit.ShouldLog(true, _tenant.TenantSlug, e.TenantSlug))
                .GroupBy(e => e.TenantSlug)
                .Select(g => (g.Key, (object)new { field = "employeeList", count = g.Count(), page, filtered = !string.IsNullOrWhiteSpace(q) || status.HasValue || !string.IsNullOrWhiteSpace(email) }));
            await PlatformAccessAudit.WriteAsync(_db, HttpContext, "EmployeeList", "list", perTenant, ct);
        }
        return result;
    }

    /// <summary>
    /// Istegi yapan kullanicinin kendi calisan kaydini doner - KeycloakUserId
    /// (JWT'nin sub/nameidentifier claim'i) uzerinden, EMAIL UZERINDEN DEGIL.
    ///
    /// GUVENLIK: GetAll'daki email eslemesi (JWT email == employee.Email)
    /// gecerli bir varsayim degil - bir kullanicinin Keycloak giris e-postasi
    /// (orn. "ad.soyad@sirket.com") ile employee kaydindaki e-postasi
    /// (orn. sirketi kaydederken girilen kisisel "ad.soyad@example.com")
    /// farkli olabilir; bu durumda GetAll?email=<jwt-email> hicbir sonuc
    /// donmuyordu. workflow/leave/expense-service'teki self-approval
    /// kontrolleri bu ucu kullaniyor (bkz. FindMyEmployeeIdAsync) - email
    /// eslemesi sessizce basarisiz oldugunda kontrolun de sessizce
    /// atlanmasina (guvenlik acigina) yol aciyordu.
    ///
    /// Her rol cagirabilir - bu "kendi kaydim" sorgusu, employee:viewAll
    /// gerektirmez.
    /// </summary>
    /// <remarks>
    /// Calisan kaydina bagli olmayan hesaplar (platform yoneticisi, salt kiraci
    /// yoneticisi) icin 404 doner. Arayuz <c>?optional=true</c> ile cagirir ve
    /// 204 alir: "kayit yok" bir hata degil, tarayici konsolunu kirletmesin.
    /// </remarks>
    [HttpGet("me")]
    public async Task<IActionResult> GetMe([FromQuery] bool optional = false)
    {
        var keycloakUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(keycloakUserId)) return optional ? NoContent() : NotFound();

        var me = await _db.Employees.FirstOrDefaultAsync(e => e.KeycloakUserId == keycloakUserId);
        if (me is null) return optional ? NoContent() : NotFound();
        return Ok(me);
    }

    /// <summary>
    /// Calisan dizini: YALNIZCA kimlik ve ad.
    ///
    /// Her rol cagirabilir. Arayuzde ID yerine isim gosterebilmek icin
    /// gerekli - calisan kendi ekibini gorurken "a6e3903d-88d4..." degil
    /// "Ahmet Yilmaz" gormeli. GetAll yonetici yetkisi istedigi icin
    /// calisan rolu oradan isim cozemiyordu.
    ///
    /// Maas, e-posta, ise giris tarihi gibi HICBIR hassas alan donmez.
    /// Bir sirket icinde calisan adlari zaten paylasilan bilgidir;
    /// gizlemek guvenlik saglamaz, yalnizca arayuzu kullanilmaz kilar.
    /// </summary>
    [HttpGet("directory")]
    public async Task<IActionResult> Directory()
    {
        var list = await _db.Employees
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new
            {
                e.Id,
                e.FirstName,
                e.LastName,
                fullName = e.FirstName + " " + e.LastName,
                e.PhotoVersion,
            })
            .ToListAsync();

        return Ok(list);
    }

    /// <summary>
    /// Tam kaydi (atamalar dahil) gorebilenler. Modul yonetim izinleri (ext-*-manage,
    /// orn. ext-timeshift-manage) de dahil: bu kullanicilarin servisleri (vardiya
    /// ekibine uye ekleme gibi) calisanin departman atamasini kendi jetonlariyla okur;
    /// kisitli gorunumde atamalar olmadigi icin gecerli uyeler bile reddediliyordu.
    /// </summary>
    private bool IsManagerOrAbove => User.IsInRole("manager") || User.IsInRole("hr-admin")
        || User.IsInRole("tenant-admin") || User.IsInRole("platform-admin")
        || User.IsInRole("ext-employee-viewAll") || User.IsInRole("ext-employee-create")
        || User.IsInRole("ext-compensation-view")
        // Muhasebe, odenecek masraf beyanlarinin kime ait oldugunu gorebilmeli.
        || User.IsInRole("accounting") || User.IsInRole("ext-expense-markPaid")
        || User.Claims.Any(c => c.Type == System.Security.Claims.ClaimTypes.Role
            && c.Value.StartsWith("ext-", StringComparison.Ordinal)
            && c.Value.EndsWith("-manage", StringComparison.Ordinal));

    private bool IsHr => User.IsInRole("hr-admin") || User.IsInRole("tenant-admin")
        || User.IsInRole("platform-admin") || User.IsInRole("ext-employee-manage");

    private string? CallerSub => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value;

    /// <summary>
    /// GUVENLIK: Onceden herhangi bir calisan, dizinden ogrendigi kimlikle her
    /// meslektasinin TAM kaydini (telefon, ise giris tarihi, KeycloakUserId, atama
    /// gecmisi) okuyabiliyordu (canli dogrulandi). Kaydin sahibi ve yonetici+ tam
    /// kaydi gorur; digerleri yalnizca kurumsal rehber bilgisini (ad, e-posta, durum)
    /// - diger servisler (orn. workflow-service onayciya bildirim icin) kullanicinin
    /// jetonuyla yalnizca bu alanlara ihtiyac duyuyor.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var employee = await _db.Employees.Include(e => e.Assignments).FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null) return NotFound();
        if (PlatformAccessAudit.ShouldLog(_tenant.IsPlatformAdmin, _tenant.TenantSlug, employee.TenantSlug))
            await PlatformAccessAudit.WriteAsync(_db, HttpContext, "Employee", employee.Id.ToString(),
                new[] { (employee.TenantSlug, (object)new { field = "employeeRecord" }) }, HttpContext.RequestAborted);
        if (IsManagerOrAbove || (CallerSub is { } sub && employee.KeycloakUserId == sub))
        {
            // Başkasının tam kaydı: erişim satırı (toplu görüntüleme dedektörü için; 10 dk'da bir).
            if (CallerSub is { } viewer && employee.KeycloakUserId != viewer && !string.IsNullOrEmpty(employee.TenantSlug)
                && Request.Headers.ContainsKey("X-Real-IP")
                && !PlatformAccessAudit.ShouldLog(_tenant.IsPlatformAdmin, _tenant.TenantSlug, employee.TenantSlug)
                && RecordViewLog.ShouldLog(employee.TenantSlug, viewer, employee.Id))
                await PlatformAccessAudit.WriteAsync(_db, HttpContext, "Employee", employee.Id.ToString(),
                    new[] { (employee.TenantSlug, (object)new { field = "employeeRecord" }) }, HttpContext.RequestAborted, action: "Viewed");
            return Ok(employee);
        }
        return Ok(new
        {
            employee.Id, employee.FirstName, employee.LastName, employee.Email, employee.Status, employee.PhotoVersion,
        });
    }

    /// <summary>
    /// Bu calisanin Keycloak kullanicisiyla baglantisini kurar - tenant-service
    /// "davet et" akisinda (yeni Keycloak kullanicisi olusturduktan sonra)
    /// cagirir. Cross-service call, cagiranin KENDI JWT'si iletilerek
    /// yapilir - o yuzden burada da RequireTenantAdmin/HrAdmin yeterli,
    /// service-to-service ayri bir kimlik dogrulamaya gerek yok.
    /// </summary>
    public record LinkKeycloakUserRequest(string KeycloakUserId);

    [HttpPut("{id}/keycloak-link")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> LinkKeycloakUser(Guid id, [FromBody] LinkKeycloakUserRequest request)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null) return NotFound();

        // GUVENLIK: Ayni giris hesabi birden fazla calisan kaydina baglanabiliyordu -
        // /me (FirstOrDefault) o zaman talep sahibi olarak kullanilandan FARKLI bir
        // kayit dondurup kendi talebini onaylama korumalarini bosa cikarabiliyordu.
        if (string.IsNullOrWhiteSpace(request.KeycloakUserId))
            return BadRequest(new { message = "KeycloakUserId zorunlu" });
        var takenBy = await _db.Employees.IgnoreQueryFilters()
            .Where(e => e.KeycloakUserId == request.KeycloakUserId && e.Id != id)
            .Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (takenBy is not null)
            return Conflict(new { message = "Bu giriş hesabı başka bir çalışan kaydına bağlı" });
        if (!string.IsNullOrEmpty(employee.KeycloakUserId) && employee.KeycloakUserId != request.KeycloakUserId)
            return Conflict(new { message = "Bu çalışan zaten başka bir giriş hesabına bağlı" });

        employee.KeycloakUserId = request.KeycloakUserId;
        await _db.SaveChangesAsync();
        return Ok(new { employeeId = id, keycloakUserId = employee.KeycloakUserId });
    }

    /// <summary>Ise giris tarihi makul aralikta olmali: 1950-01-01 ile bugun+1 yil.</summary>
    public static string? ValidateHireDate(DateOnly hireDate)
    {
        if (hireDate < new DateOnly(1950, 1, 1))
            return "İşe giriş tarihi 01.01.1950'den önce olamaz";
        if (hireDate > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1))
            return "İşe giriş tarihi en fazla bir yıl sonrası olabilir";
        return null;
    }

    [HttpPost]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateEmployeeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            return BadRequest(new { message = "Ad ve soyad zorunlu" });
        if (string.IsNullOrWhiteSpace(request.Email)
            || !System.Text.RegularExpressions.Regex.IsMatch(request.Email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return BadRequest(new { message = "Geçerli bir e-posta adresi girin" });
        // Tarih mantik dogrulamasi: 01.01.1800 gibi degerler kabul ediliyordu.
        var hireError = ValidateHireDate(request.HireDate);
        if (hireError is not null) return BadRequest(new { message = hireError });
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        // NOT: Ayni kiracida ayni e-posta tekrar eklenemez (onceden veritabani
        // kisitina carpip 500 donuyordu). Kontrol kiraci filtresiyle yapilir; baska
        // bir kiracidaki ayni adres engel degildir (bkz. kiraci bazli benzersiz indeks).
        if (await _db.Employees.AnyAsync(e => e.Email.ToLower() == normalizedEmail))
            return Conflict(new { message = "Bu e-posta ile kayıtlı bir çalışan zaten var" });

        var employee = new Employee
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = normalizedEmail,
            Phone = request.Phone,
            HireDate = request.HireDate
        };
        _db.Employees.Add(employee);

        // Event, is verisiyle AYNI transaction'da outbox'a yazilir.
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = EmployeeTopics.Events,
            EventType = EmployeeEventTypes.Hired,
            PartitionKey = employee.Id.ToString(),
            Payload = JsonSerializer.Serialize(new EmployeeHiredEvent(
                _db.CurrentTenantSlug ?? "",
                employee.Id, employee.FirstName, employee.LastName,
                employee.Email, employee.HireDate, DateTimeOffset.UtcNow)),
        });

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = employee.Id }, employee);
    }

    /// <summary>
    /// Calisan durumu: Active / OnLeave / Terminated. Ayrilista (Terminated) acik
    /// gorevlendirme ayrilis tarihinde kapatilir; olay outbox'a yazilir (bildirim,
    /// kural motoru ve canli olay akisi dinler). engagement-service offboarding
    /// sureci kapaninca bu ucu kullanicinin kendi jetonuyla cagirir.
    /// </summary>
    public record ChangeStatusRequest(string Status, DateOnly? EffectiveDate);

    [HttpPatch("{id}/status")]
    [Authorize(Policy = "RequireHrAdmin")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeStatusRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<EmployeeStatus>(request.Status, true, out var status))
            return BadRequest(new { message = "Durum Active, OnLeave veya Terminated olmali" });
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return NotFound();
        // Ayrilis tarihi atamalarin bitisi olur; saklama (anonimlestirme) isi bu tarihe
        // bakar. Ise giristen onceki ya da cok ileri bir tarih kabul edilmez.
        if (status == EmployeeStatus.Terminated && request.EffectiveDate is { } eff
            && (eff < employee.HireDate || eff > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1)))
            return BadRequest(new { message = "Ayrılış tarihi işe giriş tarihinden önce ya da bir yıldan ileri olamaz" });
        var old = employee.Status;
        if (old == status) return Ok(employee);
        employee.Status = status;

        if (status == EmployeeStatus.Terminated)
        {
            var end = request.EffectiveDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var open = await _db.Assignments.Where(a => a.EmployeeId == id && a.EffectiveTo == null).ToListAsync(ct);
            foreach (var a in open) a.EffectiveTo = end < a.EffectiveFrom ? a.EffectiveFrom : end;
        }

        _db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = EmployeeTopics.Events,
            EventType = EmployeeEventTypes.StatusChanged,
            PartitionKey = id.ToString(),
            Payload = JsonSerializer.Serialize(new EmployeeStatusChangedEvent(
                _db.CurrentTenantSlug ?? "", id, employee.FirstName, employee.LastName,
                old.ToString(), status.ToString(), request.EffectiveDate, DateTimeOffset.UtcNow)),
        });
        await _db.SaveChangesAsync(ct);
        return Ok(employee);
    }

    /// <summary>
    /// Self-servis: calisan kendi iletisim telefonunu gunceller. Ad, e-posta ve
    /// ise giris tarihi gibi kimlik alanlari yalnizca IK tarafindan degisir.
    /// </summary>
    public record UpdateMyContactRequest(string? Phone);

    [HttpPut("me/contact")]
    public async Task<IActionResult> UpdateMyContact([FromBody] UpdateMyContactRequest request, CancellationToken ct)
    {
        var keycloakUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(keycloakUserId)) return NotFound();
        var me = await _db.Employees.FirstOrDefaultAsync(e => e.KeycloakUserId == keycloakUserId, ct);
        if (me is null) return NotFound();
        var phone = request.Phone?.Trim();
        if (!string.IsNullOrEmpty(phone) && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+?[0-9 ()-]{7,20}$"))
            return BadRequest(new { message = "Telefon numarası geçersiz" });
        me.Phone = string.IsNullOrEmpty(phone) ? null : phone;
        await _db.SaveChangesAsync(ct);
        return Ok(me);
    }

    [HttpPost("{id}/assignments")]
    [Authorize(Policy = "RequireManagerOrAbove")]
    public async Task<IActionResult> CreateAssignment(Guid id, [FromBody] CreateAssignmentRequest request, CancellationToken ct)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (employee is null) return NotFound("Employee not found");

        // Onceki aktif atama (varsa) ele alinir. Uc durum kararlari:
        //   - yeni effectiveFrom, mevcut aktifin effectiveFrom'undan ONCEYSE:
        //     gecersiz istek - iki kayit CAKISIR, veri butunlugunu bozar.
        //   - yeni effectiveFrom AYNI gunse (surukle-birak her zaman "bugun"
        //     gonderir - ayni gun ikinci tasima bu durum): YENI kayit acmak
        //     yerine MEVCUT kaydi GUNCELLE - ayni gun icin iki satir tutmanin
        //     pratik degeri yok, ve "bitis < baslangic" gibi bozuk bir
        //     araliga da yol acardi.
        //   - yeni effectiveFrom SONRAYSA: normal akis (eskiyi kapat, yeni ac).
        var currentActive = await _db.Assignments
            .Where(a => a.EmployeeId == id && a.EffectiveTo == null)
            .FirstOrDefaultAsync();

        // GUVENLIK: Herhangi bir yonetici, herhangi bir calisani herhangi bir
        // departmana tasiyabiliyordu - izin onayi calisanin aktif departmaninin
        // basina gittigi icin bu, onay zincirini istedigi kisiye yonlendirmek
        // demekti. Yonetici yalnizca HENUZ atamasi olmayan (yeni ise alinan)
        // calisani yerlestirebilir; mevcut atamayi degistirmek IK'ya ozel.
        if (currentActive is not null && !IsHr)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Mevcut bir atamayı yalnızca İK değiştirebilir" });
        if (request.DepartmentId == Guid.Empty)
            return BadRequest(new { message = "Departman zorunlu" });

        // Atama baslangici ise giristen once olamaz (01.01.1700 kabul ediliyordu).
        // Istisna: ise girisi ileri tarihli calisan organizasyon semasinda "bugun"
        // ile yerlestirilebilir (surukle-birak bugunu gonderir).
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.EffectiveFrom < employee.HireDate && request.EffectiveFrom < todayUtc)
            return BadRequest(new { message = "Atama başlangıcı işe giriş tarihinden önce olamaz" });
        if (request.EffectiveFrom > todayUtc.AddYears(1))
            return BadRequest(new { message = "Atama başlangıcı en fazla bir yıl sonrası olabilir" });

        if (currentActive is not null && request.EffectiveFrom < currentActive.EffectiveFrom)
            return BadRequest(new
            {
                message = "Başlangıç tarihi, mevcut atamanın başlangıcından önce olamaz",
            });

        Assignment assignment;
        var previousDepartmentId = currentActive?.DepartmentId;

        if (currentActive is not null && request.EffectiveFrom == currentActive.EffectiveFrom)
        {
            currentActive.DepartmentId = request.DepartmentId;
            currentActive.PositionTitle = request.PositionTitle;
            assignment = currentActive;
        }
        else
        {
            if (currentActive is not null)
                currentActive.EffectiveTo = request.EffectiveFrom.AddDays(-1);

            assignment = new Assignment
            {
                EmployeeId = id,
                DepartmentId = request.DepartmentId,
                PositionTitle = request.PositionTitle,
                EffectiveFrom = request.EffectiveFrom
            };
            _db.Assignments.Add(assignment);
        }

        _db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = EmployeeTopics.Events,
            EventType = EmployeeEventTypes.Assigned,
            PartitionKey = id.ToString(),
            Payload = JsonSerializer.Serialize(new EmployeeAssignedEvent(
                _db.CurrentTenantSlug ?? "",
                id, assignment.Id, assignment.DepartmentId,
                assignment.PositionTitle, assignment.EffectiveFrom, DateTimeOffset.UtcNow,
                employee.Email, employee.FirstName, employee.LastName)),
        });

        await _db.SaveChangesAsync();

        // Eski departmanin basi bu calisansa (artik orada degil), bas
        // alanini temizle - best-effort, assignment kaydini engellemez.
        if (previousDepartmentId.HasValue && previousDepartmentId != request.DepartmentId)
            await _organizations.ClearHeadIfMatchesAsync(previousDepartmentId.Value, id, ct);

        return CreatedAtAction(nameof(GetById), new { id }, assignment);
    }
}

public record CreateEmployeeRequest(string FirstName, string LastName, string Email, string? Phone, DateOnly HireDate);
public record CreateAssignmentRequest(Guid DepartmentId, string? PositionTitle, DateOnly EffectiveFrom);
