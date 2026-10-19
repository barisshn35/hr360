using HR360.Platform;
using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ExpenseService.Data;
using ExpenseService.Tenancy;
using ExpenseService.Messaging;
using ExpenseService.Services;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
ExpenseService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
builder.AddHr360Logging(); // HR360_LOG_FORMAT=json: tek satır JSON, istek kimliği + şirket (Shared/StructuredLogging.cs)
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
ExpenseService.Observability.Telemetry.AddHrTelemetry(builder.Services, "expense-service");

var connectionString = Environment.GetEnvironmentVariable("EXPENSE_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<ApprovalWorkflowClient>();
builder.Services.AddHttpClient<ExpenseService.Services.GovernanceSignatureClient>();
// Anahtar yenileme: etkin olmayan anahtarla şifreli değerleri yeniden yazar (scripts/crypto-keys.sh).
// Bu servisin sahibi olduğu tüm şifreli sütunlar burada listelenir; yenisini eklerken buraya ve
// scripts/crypto-keys.sh içindeki listeye ekleyin.
ExpenseService.Security.EncColumn[] encryptedColumns =
[
    new("expense_travel_requests", "PassportCipher", ExpenseService.Security.EncKind.Prefixed),
];
builder.Services.AddHostedService(sp => new ExpenseService.Security.KeyRotationJob("EXPENSE_DB_CONNECTION", encryptedColumns,
    sp.GetRequiredService<ILogger<ExpenseService.Security.KeyRotationJob>>()));
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<WorkflowEventConsumer>();
builder.Services.AddHttpClient<ExpenseService.Services.FxService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ExpenseService.Services.ExpenseAnomalyClient>();
builder.Services.AddHostedService<ExpenseService.Controllers.TravelPurgeWorker>();

builder.Services.AddDbContext<ExpenseDbContext>(options =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(HR360.Platform.TenantSessionInterceptor.Instance, new ExpenseService.Auditing.AuditInterceptor("expense-service"),
            new ExpenseService.Observability.BusinessMetricsInterceptor()));

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
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireRole("hr-admin", "tenant-admin", "platform-admin"));
    options.AddPolicy("RequireManagerOrAbove", policy =>
        policy.RequireRole("manager", "hr-admin", "tenant-admin", "platform-admin"));
    options.AddPolicy("RequireAccountingOrAbove", policy =>
        policy.RequireRole("accounting", "hr-admin", "tenant-admin", "platform-admin"));
    // DocumentsController (document:manage), ExpenseClaimsController.Resolve
    // (expense:manage), MarkPaid (expense:markPaid) ve HrCasesController.Assign
    // (case:manage) roles.ts'te ayri izinlere karsilik gelir; her biri kendi
    // ext-* ek izniyle genisletilir. RequireHrAdmin/RequireManagerOrAbove'u
    // dogrudan genisletmek yanlis olur (baska iznin uclarina da acar).
    // Not: HrCases.Assign ayrica kod icinde IK'ya (veya ext-case-manage'e)
    // daraltilir; vaka kapatma (Resolve) atanan kisiye/IK'ya aciktir.
    options.AddPolicy("RequireDocumentManage", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("hr-admin") || ctx.User.IsInRole("tenant-admin") ||
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-document-manage")));
    options.AddPolicy("RequireExpenseManage", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("manager") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-expense-manage")));
    options.AddPolicy("RequireExpenseMarkPaid", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("accounting") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-expense-markPaid")));
    options.AddPolicy("RequireCaseManage", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("manager") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-case-manage")));
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
app.UseHr360LogScope();
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

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "expense-service" }));
app.MapReadiness<ExpenseDbContext>("expense-service");
app.MapControllers();
app.MapMetrics();

app.Run();
