using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using GovernanceService.Data;
using GovernanceService.Tenancy;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
GovernanceService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
GovernanceService.Observability.Telemetry.AddHrTelemetry(builder.Services, "governance-service");

var connectionString = Environment.GetEnvironmentVariable("GOVERNANCE_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// jsonb sutunlari (gundem, aday listesi, anket sorulari...) tipli C# listeleri olarak
// eslenir; Npgsql 8+ bunun icin veri kaynaginda EnableDynamicJson ister.
var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContext<GovernanceDbContext>(options =>
    options.UseNpgsql(dataSource)
        .AddInterceptors(new GovernanceService.Auditing.AuditInterceptor("governance-service")));
builder.Services.AddSingleton<GovernanceService.Infrastructure.Sql>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.AppCache>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.PeopleDirectory>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Notifier>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.SignatureEngine>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GovernanceService.Infrastructure.EventHub>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Dispatcher>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Chat.SlackApi>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Chat.TeamsApi>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Chat.BotFrameworkAuth>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Chat.ChatService>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Calendar.GoogleCalendar>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Calendar.MicrosoftCalendar>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Calendar.ZoomApi>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Calendar.CalendarService>();
// Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma/kapatma (ACCOUNT_PROVISIONING_ENABLED).
builder.Services.AddSingleton<GovernanceService.Infrastructure.Provisioning.GoogleDirectoryProvisioner>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Provisioning.MicrosoftDirectoryProvisioner>();
builder.Services.AddSingleton<GovernanceService.Infrastructure.Ai.LlmClient>();
builder.Services.AddScoped<GovernanceService.Infrastructure.Ai.AiGateway>();
builder.Services.AddScoped<GovernanceService.Infrastructure.HrAssistant>();
// Anahtar yenileme: etkin olmayan anahtarla şifreli değerleri yeniden yazar (scripts/crypto-keys.sh).
// Bu servisin sahibi olduğu tüm şifreli sütunlar burada listelenir; yenisini eklerken buraya ve
// scripts/crypto-keys.sh içindeki listeye ekleyin.
GovernanceService.Security.EncColumn[] encryptedColumns =
[
    new("governance_chat_apps", "SlackBotTokenEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_apps", "SlackSigningSecretEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_apps", "TeamsAppPasswordEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_apps", "BotTokenEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_apps", "IncomingTokenEnc", GovernanceService.Security.EncKind.Text),
    new("governance_calendar_connections", "AccessTokenEnc", GovernanceService.Security.EncKind.Text),
    new("governance_calendar_connections", "RefreshTokenEnc", GovernanceService.Security.EncKind.Text),
    new("governance_provider_configs", "ClientSecretEnc", GovernanceService.Security.EncKind.Text),
    new("governance_provisioning_configs", "CredentialsEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_context", "TextEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_exit_progress", "AnswersEnc", GovernanceService.Security.EncKind.Text),
    new("governance_chat_pending", "PayloadEnc", GovernanceService.Security.EncKind.Text),
    new("governance_document_requests", "DocumentEnc", GovernanceService.Security.EncKind.Text),
    new("governance_ethics_reports", "ContactEnc", GovernanceService.Security.EncKind.Text),
    new("governance_osh_exams", "NotesEnc", GovernanceService.Security.EncKind.Text),
    new("governance_custom_field_values", "Value", GovernanceService.Security.EncKind.Prefixed),
];
builder.Services.AddHostedService(sp => new GovernanceService.Security.KeyRotationJob("GOVERNANCE_DB_CONNECTION", encryptedColumns,
    sp.GetRequiredService<ILogger<GovernanceService.Security.KeyRotationJob>>()));
builder.Services.AddHostedService<GovernanceService.Infrastructure.EventConsumer>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.Housekeeping>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.WebhookRetryWorker>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.SiemExporter>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.AuditChainGuard>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.Provisioning.ProvisioningScanner>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.Chat.ChatOutboxWorker>();
builder.Services.AddHostedService<GovernanceService.Infrastructure.Chat.ChatDigestWorker>();
builder.Services.AddHostedService<GovernanceService.Controllers.SavedReportWorker>();
// Güvenlik dalgası 2B: toplu görüntüleme (sızdırma) dedektörü ve erişim gözden geçirme hatırlatmaları.
builder.Services.AddHostedService<GovernanceService.Infrastructure.MassViewWorker>();
builder.Services.AddHostedService<GovernanceService.Controllers.AccessReviewWorker>();



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
    // RequireHrAdmin ve RequireManagerOrAbove bu serviste tum uclarda
    // ayni ize karsilik geliyor (ext-governance-manage).
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("hr-admin") || ctx.User.IsInRole("tenant-admin") ||
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-governance-manage")));
    options.AddPolicy("RequireManagerOrAbove", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("manager") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-governance-manage")));
});

builder.Services.AddControllers(o => o.Filters.Add<GovernanceService.Infrastructure.TenantMissingFilter>())
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
// Ayni adli ic ice kayitlar (ör. iki denetleyicide "CreateInput") sema kimliginde
// cakismasin; tam ad kullanilir. Aksi halde OpenAPI tanimi 500 doner.
builder.Services.AddSwaggerGen(c => c.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace('+', '.')));

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

app.UseSwagger();
// Swagger arayuzu yalnizca gelistirmede; JSON belge (/swagger/v1/swagger.json) gateway'in
// /api-docs/specs/*.json ucu ve web'deki API belgeleri sayfasi icin her ortamda acik.
if (!app.Environment.IsProduction()) app.UseSwaggerUI();

app.UseAuthentication();
app.UseTenantContext();
app.UseAuthorization();
app.UseHttpMetrics();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "governance-service" }));
app.MapControllers();
app.MapMetrics();

app.Run();
