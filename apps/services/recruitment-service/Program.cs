using HR360.Platform;
using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RecruitmentService.Data;
using RecruitmentService.Tenancy;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
RecruitmentService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
RecruitmentService.Observability.Telemetry.AddHrTelemetry(builder.Services, "recruitment-service");

var connectionString = Environment.GetEnvironmentVariable("RECRUITMENT_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

builder.Services.AddDbContext<RecruitmentDbContext>(options =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(HR360.Platform.TenantSessionInterceptor.Instance, new RecruitmentService.Auditing.AuditInterceptor("recruitment-service"),
            new RecruitmentService.Observability.BusinessMetricsInterceptor()));

// Dalga 5c: teklif onay akışı (workflow-service, kullanıcının jetonuyla), Kafka karar olayları,
// kariyer sayfası istek sınırı (bellekte) ve aday verisi imha işçisi.
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<RecruitmentService.Services.ApprovalWorkflowClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
// Dalga 11: teklif e-imzası governance tek imza motoruna iç uçlarla devredilir.
builder.Services.AddHttpClient<RecruitmentService.Services.GovernanceSignatureClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<RecruitmentService.Services.PublicRateLimiter>();
// Dalga 11: durum bağlantısı GET sınırı (ayrı sayaç) ve öneri ödülü hak ediş işçisi.
builder.Services.AddSingleton<RecruitmentService.Services.StatusRateLimiter>();
builder.Services.AddHostedService<RecruitmentService.Services.ReferralRewardWorker>();
builder.Services.AddHostedService<RecruitmentService.Messaging.WorkflowEventConsumer>();
builder.Services.AddHostedService<RecruitmentService.Services.RetentionWorker>();

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
    // RequireHrAdmin sadece JobPostingsController'i (Create/Close/Publish),
    // RequireManagerOrAbove sadece ApplicationsController+CandidatesController'i
    // koruyor - roles.ts'te sirasiyla tek bir ize karsilik geliyorlar
    // (recruitment:publish, recruitment:candidates).
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("hr-admin") || ctx.User.IsInRole("tenant-admin") ||
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-recruitment-publish")));
    options.AddPolicy("RequireManagerOrAbove", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("manager") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-recruitment-candidates")));
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
// İç içe aynı adlı kayıtlar (ör. iki denetleyicide DecideInput) çakışmasın diye tam ad.
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

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "recruitment-service" }));
app.MapReadiness<RecruitmentDbContext>("recruitment-service");
app.MapControllers();
app.MapMetrics();

app.Run();
