using HR360.Platform;
using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using CompensationService.Data;
using CompensationService.Tenancy;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
CompensationService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
CompensationService.Observability.Telemetry.AddHrTelemetry(builder.Services, "compensation-service");

var connectionString = Environment.GetEnvironmentVariable("COMPENSATION_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// Anahtar yenileme: etkin olmayan anahtarla şifreli değerleri yeniden yazar (scripts/crypto-keys.sh).
// Bu servisin sahibi olduğu tüm şifreli sütunlar burada listelenir; yenisini eklerken buraya ve
// scripts/crypto-keys.sh içindeki listeye ekleyin.
CompensationService.Security.EncColumn[] encryptedColumns =
[
    new("compensation_payroll_exports", "Cipher", CompensationService.Security.EncKind.Bytes),
    // Bordro dalgası 8: e-bordro teslim kopyası (kanonik JSON).
    new("compensation_payslip_deliveries", "SealedCopy", CompensationService.Security.EncKind.Bytes),
];
builder.Services.AddHostedService(sp => new CompensationService.Security.KeyRotationJob("COMPENSATION_DB_CONNECTION", encryptedColumns,
    sp.GetRequiredService<ILogger<CompensationService.Security.KeyRotationJob>>()));
builder.Services.AddHostedService<CompensationService.Controllers.ExportPurgeWorker>();
// ML dalgası 2: bordro denetimi ve ücret adaleti analizi (ml-inference).
builder.Services.AddHttpClient();
builder.Services.AddScoped<CompensationService.Payroll.PayrollAnomalyClient>();
builder.Services.AddScoped<CompensationService.Payroll.PayEquityClient>();
builder.Services.AddDbContext<CompensationDbContext>(options =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(HR360.Platform.TenantSessionInterceptor.Instance, new CompensationService.Auditing.AuditInterceptor("compensation-service"),
            new CompensationService.Observability.BusinessMetricsInterceptor()));

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
    // Bu serviste RequireHrAdmin'in koruduğu tüm uçlar (GetBands,
    // GetRecords, CreateBand, CreateRecord, Simulate) roles.ts'te TEK bir
    // izne ("compensation:view") karşılık geliyor - o yüzden "ext-*" ek
    // izin rolünü doğrudan bu policy'ye eklemek doğru. Başka bir serviste
    // aynı policy birden fazla izni kapsıyorsa (örn. expense-service'teki
    // RequireHrAdmin hem document:manage hem başka işler için kullanılıyor
    // olabilir), tek bir policy'yi genişletmek YANLIŞ olur - o durumda
    // uçların kendi policy attribute'ları izne göre ayrılmalı.
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("hr-admin") || ctx.User.IsInRole("tenant-admin") ||
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-compensation-view")));
    // Yazma uclari (ucret bandi ve ucret kaydi olusturma) yalnizca IK'ya acik.
    // "compensation:view" ek izni ADINDAN da anlasilacagi gibi yalnizca okuma
    // verir; onceden RequireHrAdmin'e dahil oldugu icin bu kisiler ucret
    // kaydi da yazabiliyordu.
    options.AddPolicy("RequireCompensationWrite", policy =>
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

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "compensation-service" }));
app.MapReadiness<CompensationDbContext>("compensation-service");
app.MapControllers();
app.MapMetrics();

app.Run();
