using HR360.Platform;
using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TenantService.Data;
using TenantService.Services;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
TenantService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
builder.AddHr360Logging(); // HR360_LOG_FORMAT=json: tek satır JSON, istek kimliği + şirket (Shared/StructuredLogging.cs)
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
TenantService.Observability.Telemetry.AddHrTelemetry(builder.Services, "tenant-service");

var connectionString = Environment.GetEnvironmentVariable("TENANT_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddDbContext<TenantDbContext>(options =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(new TenantService.Auditing.AuditInterceptor("tenant-service")));

builder.Services.AddHttpClient<KeycloakAdminClient>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<EmployeeDirectoryClient>();
builder.Services.AddHttpClient<SampleDataService>();
builder.Services.AddScoped<TenantProvisioningService>();
builder.Services.AddScoped<PlatformSettingsService>();
builder.Services.AddScoped<PasswordLinkService>();
builder.Services.AddHostedService<TenantService.Controllers.RealmMailSyncHostedService>();
builder.Services.AddSingleton<TenantService.Security.SmtpCredentialProtector>();
builder.Services.AddSingleton<TenantService.Services.LogoStorageService>();
builder.Services.AddSingleton<TenantService.Services.ObjectStorageEraser>();
// Anahtar yenileme: etkin olmayan anahtarla şifreli değerleri yeniden yazar (scripts/crypto-keys.sh).
// Bu servisin sahibi olduğu tüm şifreli sütunlar burada listelenir; yenisini eklerken buraya ve
// scripts/crypto-keys.sh içindeki listeye ekleyin.
TenantService.Security.EncColumn[] encryptedColumns =
[
    new("platform_tenants", "SmtpPasswordEncrypted", TenantService.Security.EncKind.Text),
    new("platform_settings", "SmtpPasswordEncrypted", TenantService.Security.EncKind.Text),
    new("tenant_directory_settings", "LdapBindPasswordEncrypted", TenantService.Security.EncKind.Text),
];
builder.Services.AddHostedService(sp => new TenantService.Security.KeyRotationJob("TENANT_DB_CONNECTION", encryptedColumns,
    sp.GetRequiredService<ILogger<TenantService.Security.KeyRotationJob>>()));
// NOT: install.sh'nin urettigi "demo.admin" Keycloak kullanicisinin gercekten
// calisir bir demo tenant'i olmasini saglar - bkz. dosyanin basindaki aciklama.
// Yalnizca demo verisi isteniyorsa (install.sh --demo -> HR360_DEMO=true; degisken yoksa
// eski kurulum kabul edilir ve demo acik kalir). Bkz. ProductSettings.
if (ProductSettings.DemoEnabled)
    builder.Services.AddHostedService<DemoTenantSeederHostedService>();
builder.Services.AddHostedService<KeycloakHardeningHostedService>();
// Guvenlik dalgasi 2A: iki adimli dogrulama politikasi, supheli giris tespiti (Keycloak
// LOGIN/LOGIN_ERROR olaylari), platform yoneticisi sureli erisim izni bildirimleri.
builder.Services.AddScoped<SecurityNotifier>();
builder.Services.AddScoped<MfaPolicyService>();
builder.Services.AddHostedService<MfaPolicyHostedService>();
builder.Services.AddHostedService<LoginWatchHostedService>();
// Dalga 5d (Y26/G28): SCIM 2.0 + LDAP/AD dizin saglama, ozel alan adi dogrulama.
builder.Services.AddScoped<TenantService.Directory.ScimTokenService>();
builder.Services.AddScoped<TenantService.Directory.DirectoryProvisioningService>();
builder.Services.AddScoped<TenantService.Directory.DirectorySyncService>();
builder.Services.AddSingleton<TenantService.Directory.LdapDirectoryReader>();
builder.Services.AddSingleton<TenantService.Domains.ITxtResolver, TenantService.Domains.OverridableTxtResolver>();
builder.Services.AddHostedService<TenantService.Directory.DirectorySyncHostedService>();

var keycloakAuthority = Environment.GetEnvironmentVariable("KEYCLOAK_AUTHORITY")
    ?? "http://keycloak:8080/auth/realms/hr360";

// GUVENLIK (CTO denetimi): Onceden issuer ve istemci dogrulanmiyordu - realm'deki
// HERHANGI bir istemcinin (orn. admin-cli, servis hesaplari) jetonu kabul ediliyordu.
// Keycloak artik sabit genel adresle calistigi icin issuer tek ve bilinir
// (KEYCLOAK_VALID_ISSUERS, virgulle birden fazla); jetonun hangi istemci icin
// verildigi (azp) de izinli listede olmali (KEYCLOAK_ALLOWED_CLIENTS, varsayilan
// hr360-web). KEYCLOAK_VALID_ISSUERS tanimsizsa eski davranis (issuer kontrolu yok).
var jwtValidIssuers = (Environment.GetEnvironmentVariable("KEYCLOAK_VALID_ISSUERS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var jwtAllowedClients = (Environment.GetEnvironmentVariable("KEYCLOAK_ALLOWED_CLIENTS") ?? "hr360-web")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloakAuthority;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = jwtValidIssuers.Length > 0,
            ValidIssuers = jwtValidIssuers,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // Izinli istemci (azp) kontrolu - bkz. jwtAllowedClients.
                var azp = context.Principal?.FindFirst("azp")?.Value;
                if (jwtAllowedClients.Length > 0 && (azp is null || !jwtAllowedClients.Contains(azp)))
                {
                    context.Fail("Bu istemci icin verilmis jetonlar kabul edilmiyor");
                    return Task.CompletedTask;
                }

                var identity = context.Principal?.Identity as ClaimsIdentity;
                if (identity is null) return Task.CompletedTask;

                var realmAccess = identity.FindFirst("realm_access")?.Value;
                if (string.IsNullOrEmpty(realmAccess)) return Task.CompletedTask;

                try
                {
                    using var doc = JsonDocument.Parse(realmAccess);
                    if (doc.RootElement.TryGetProperty("roles", out var roles))
                    {
                        foreach (var role in roles.EnumerateArray())
                        {
                            var name = role.GetString();
                            if (!string.IsNullOrEmpty(name))
                                identity.AddClaim(new Claim(ClaimTypes.Role, name));
                        }
                    }
                }
                catch (JsonException) { }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequirePlatformAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-platform-manage")));
    // GUVENLIK: "RequireTenantAdmin" MyTenantController'da (tenant:manage
    // izni) genisletiliyor - ama TeamMembersController.AssignExtraPermission/
    // RemoveExtraPermission (Ek izin atama/kaldirma) AYNI policy adini
    // kullanirsa, "tenant:manage" ek iznini alan biri baskalarina da izin
    // atayabilir hale gelirdi - bu, "sadece Sirket/Platform Yoneticisi ek
    // izin atayabilir" tasarim kuralini deler. Bu yuzden ek izin uclari,
    // "ext-*" genislemesi OLMAYAN bu ayri policy'yi kullaniyor.
    options.AddPolicy("RequireTenantAdminOnly", policy =>
        policy.RequireRole("tenant-admin", "platform-admin"));
    options.AddPolicy("RequireTenantAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-tenant-manage")));
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireRole("hr-admin", "tenant-admin", "platform-admin"));
    options.AddPolicy("RequireManagerOrAbove", policy =>
        policy.RequireRole("manager", "hr-admin", "tenant-admin", "platform-admin"));
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// GUVENLIK: Anonim sirket kaydi sinirsizdi - her cagri bir Keycloak
// organizasyonu + kullanicisi olusturup e-posta gonderiyor (e-posta bombardimani,
// Keycloak'i doldurma). IP basina 15 dakikada 5 kayit. Istemci IP'si nginx'in
// her istekte uzerine yazdigi X-Real-IP basligindan alinir.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("registration", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Request.Headers["X-Real-IP"].FirstOrDefault()
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
            }));
    // Dalga 5d: SCIM istemcileri (IdP) icin IP basina dakikada 600 istek; anonim marka
    // cozumleme (giris ekrani, kurumsal NAT arkasi) dakikada 300, TXT dogrulama dakikada 20.
    static System.Threading.RateLimiting.RateLimitPartition<string> PerIp(HttpContext httpContext, int limit, TimeSpan window) =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Request.Headers["X-Real-IP"].FirstOrDefault()
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = limit, Window = window, QueueLimit = 0 });
    options.AddPolicy("scim", ctx => PerIp(ctx, 600, TimeSpan.FromMinutes(1)));
    options.AddPolicy("public-resolve", ctx => PerIp(ctx, 300, TimeSpan.FromMinutes(1)));
    options.AddPolicy("domain-verify", ctx => PerIp(ctx, 20, TimeSpan.FromMinutes(1)));
    // Parola linki: durum + parola belirleme IP başına 10 dakikada 60; "parolamı unuttum" 15 dakikada 10.
    options.AddPolicy("password-link", ctx => PerIp(ctx, 60, TimeSpan.FromMinutes(10)));
    options.AddPolicy("password-reset", ctx => PerIp(ctx, 10, TimeSpan.FromMinutes(15)));
    options.OnRejected = async (ctx, ct) =>
    {
        var path = ctx.HttpContext.Request.Path.Value ?? "";
        if (path.Contains("/scim/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.HttpContext.Response.ContentType = "application/scim+json";
            await ctx.HttpContext.Response.WriteAsync(
                "{\"schemas\":[\"urn:ietf:params:scim:api:messages:2.0:Error\"],\"status\":\"429\",\"detail\":\"Çok fazla istek\"}", ct);
            return;
        }
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(path.Contains("/registration", StringComparison.OrdinalIgnoreCase)
            ? "{\"message\":\"Çok fazla kayıt denemesi. Lütfen biraz sonra tekrar deneyin.\"}"
            : "{\"message\":\"Çok fazla istek. Lütfen biraz sonra tekrar deneyin.\"}", ct);
    };
});

var app = builder.Build();
app.UseHr360LogScope();
// Zamanlanmış arka plan işleri çok kopyada tek kopyada çalışır (HR360.Platform.LeaderGate).
HR360.Platform.LeaderGate.Configure(connectionString, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LeaderGate"));

app.UseSwagger();
// Swagger arayuzu yalnizca gelistirmede; JSON belge (/swagger/v1/swagger.json) gateway'in
// /api-docs/specs/*.json ucu ve web'deki API belgeleri sayfasi icin her ortamda acik.
if (!app.Environment.IsProduction()) app.UseSwaggerUI();

app.UseRateLimiter();
app.UseAuthentication();
// GUVENLIK: Askiya alinan kiracinin kullanicilarinin elindeki (henuz suresi dolmamis)
// jetonlar bu serviste de reddedilir - diger servislerdeki TenantStatusGate ile ayni kural.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true && !ctx.User.IsInRole("platform-admin"))
    {
        var slug = TenantService.Security.OrganizationClaimParser.ParseSlug(ctx.User.FindFirst("organization")?.Value);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var db = ctx.RequestServices.GetRequiredService<TenantService.Data.TenantDbContext>();
            var row = await db.Tenants.Where(t => t.Slug == slug)
                .Select(t => new { t.Status, t.IpAllowlist })
                .FirstOrDefaultAsync(ctx.RequestAborted);
            var status = row?.Status;
            // G22: kiracı IP kısıtı (gateway'den gelen isteklerde).
            var realIp = ctx.Request.Headers["X-Real-IP"].FirstOrDefault();
            var allow = TenantService.Tenancy.TenantStatusGate.ParseList(row?.IpAllowlist);
            if (allow.Count > 0 && realIp is not null && !TenantService.Tenancy.TenantStatusGate.Allowed(allow, realIp))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { message = "Bu ağdan erişim şirketiniz tarafından kısıtlanmış.", code = "ip_not_allowed" });
                return;
            }
            if (status == TenantService.Models.TenantStatus.Suspended)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    message = "Şirket hesabı askıya alınmış. Lütfen yöneticinizle iletişime geçin.",
                    code = "tenant_suspended",
                });
                return;
            }
        }
    }
    await next();
});
app.UseAuthorization();
app.UseHttpMetrics();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "tenant-service" }));
app.MapReadiness<TenantDbContext>("tenant-service");
app.MapControllers();
app.MapMetrics();

app.Run();
