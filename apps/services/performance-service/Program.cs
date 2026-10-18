using HR360.Platform;
using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PerformanceService.Data;
using PerformanceService.Tenancy;
using PerformanceService.Services;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
PerformanceService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
PerformanceService.Observability.Telemetry.AddHrTelemetry(builder.Services, "performance-service");

var connectionString = Environment.GetEnvironmentVariable("PERFORMANCE_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

builder.Services.AddDbContext<PerformanceDbContext>(options =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(HR360.Platform.TenantSessionInterceptor.Instance, new PerformanceService.Auditing.AuditInterceptor("performance-service")));

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
        // KRITIK: .NET varsayilan olarak JWT'nin standart claim adlarini
        // (email, sub, given_name...) eski XML sema URI'lerine (ClaimTypes.*)
        // otomatik donusturur. Bu donusum aktifken User.FindFirst("email")
        // HICBIR ZAMAN eslesmiyor - JWT'de "email" claim'i acikca VARKEN
        // bile /me uclari (AnalyticsController.Me, GoalsController.GetAll,
        // CycleAnalyticsController.MyCycles) "Token'da e-posta bilgisi yok"
        // diye 400 donuyordu. "organization" ve "realm_access" gibi
        // Keycloak'a ozel claim'ler bu haritada olmadigi icin etkilenmiyordu,
        // sorun sessizce yalniz email okuyan yerlerde ortaya cikiyordu.
        options.MapInboundClaims = false;
        options.Authority = keycloakAuthority;
        options.RequireHttpsMetadata = false; // ic ag, VPN arkasinda
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            // Token app-01 veya app-02'deki Keycloak'tan alinabilir; iki instance
            // farkli issuer URL'i dondugu icin issuer dogrulamasi kapali.
            ValidateIssuer = jwtValidIssuers.Length > 0,
            ValidIssuers = jwtValidIssuers,
        };
        // Keycloak realm_access.roles claim'ini ASP.NET Core'un ClaimTypes.Role'une
        // esler. Bu olmadan policy/role tabanli yetkilendirme calismaz.
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
    // Bu servisteki tum uclar roles.ts'te tek bir ize karsilik geliyor
    // (performance:manage - başka performans izni yok, RequireHrAdmin
    // olan uclar da manager seviyesindeki bu izne dahil).
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("hr-admin") || ctx.User.IsInRole("tenant-admin") ||
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-performance-manage")));
    options.AddPolicy("RequireManagerOrAbove", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("manager") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-performance-manage")));
});

builder.Services.AddScoped<ScoreCalculator>();
builder.Services.AddScoped<SnapshotService>();
// G12 (Dalga 5c): departman başkanı/çalışan okuyucu + denetim yazıcı.
builder.Services.AddScoped<PerfPeople>();
builder.Services.AddScoped<NineBoxGrid>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<DirectoryClient>();
builder.Services.AddScoped<RecommendationEngine>();
builder.Services.AddHttpClient<PerformanceMlClient>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
// Ayni adli ic ice kayitlar (ör. Feedback360 ve Calibration "CreateInput") sema kimliginde
// cakismasin; tam ad kullanilir. Aksi halde OpenAPI tanimi 500 doner.
builder.Services.AddSwaggerGen(c => c.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace('+', '.')));

var app = builder.Build();
// Zamanlanmış arka plan işleri çok kopyada tek kopyada çalışır (HR360.Platform.LeaderGate).
HR360.Platform.LeaderGate.Configure(connectionString, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LeaderGate"));

app.UseSwagger();
// Swagger arayuzu yalnizca gelistirmede; JSON belge (/swagger/v1/swagger.json) gateway'in
// /api-docs/specs/*.json ucu ve web'deki API belgeleri sayfasi icin her ortamda acik.
if (!app.Environment.IsProduction()) app.UseSwaggerUI();

app.UseAuthentication();
app.UseTenantContext();
app.UseAuthorization();
app.UseHttpMetrics();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "performance-service" }));
app.MapReadiness<PerformanceDbContext>("performance-service");
app.MapControllers();
app.MapMetrics();

app.Run();
