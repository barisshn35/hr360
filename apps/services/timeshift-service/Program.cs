using Prometheus;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TimeShiftService.Data;
using TimeShiftService.Messaging;
using TimeShiftService.Services;
using TimeShiftService.Tenancy;

// Sırlar dosyadan da okunabilir (X_FILE, Docker secrets); X tanımlıysa davranış aynı.
TimeShiftService.Security.SecretEnv.Load();
var builder = WebApplication.CreateBuilder(args);
// G25: OpenTelemetry izleme (yalnizca OTEL_EXPORTER_OTLP_ENDPOINT tanimliysa) + KVKK maskeleme.
TimeShiftService.Observability.Telemetry.AddHrTelemetry(builder.Services, "timeshift-service");

var connectionString = Environment.GetEnvironmentVariable("TIMESHIFT_DB_CONNECTION")
    ?? throw new InvalidOperationException("DB connection string not configured");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddHostedService<LeaveEventConsumer>();
builder.Services.AddHostedService<WorkflowEventConsumer>();
builder.Services.AddHttpClient<ApprovalStarter>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<EmployeeDirectoryClient>();
builder.Services.AddHttpClient<DepartmentDirectoryClient>();

builder.Services.AddDbContext<TimeShiftDbContext>(options =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(new TimeShiftService.Auditing.AuditInterceptor("timeshift-service"),
            new TimeShiftService.Observability.BusinessMetricsInterceptor()));

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
    // Bu servisteki tum uclar roles.ts'te tek bir ize karsilik geliyor
    // (timeshift:manage).
    options.AddPolicy("RequireHrAdmin", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("hr-admin") || ctx.User.IsInRole("tenant-admin") ||
            ctx.User.IsInRole("platform-admin") || ctx.User.IsInRole("ext-timeshift-manage")));
    options.AddPolicy("RequireManagerOrAbove", policy =>
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("manager") || ctx.User.IsInRole("hr-admin") ||
            ctx.User.IsInRole("tenant-admin") || ctx.User.IsInRole("platform-admin") ||
            ctx.User.IsInRole("ext-timeshift-manage")));
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
// Madde 68: giriş-çıkış uçlarında hız sınırı (gateway'deki sınıra ek). Cihaz (terminal/kiosk) anahtarı
// başına dakikada 120, kişi başına (web/QR giriş-çıkış) dakikada 20 istek; aşılırsa 429.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("clock-device", ctx =>
    {
        var key = ctx.Request.Headers["X-Device-Key"].FirstOrDefault();
        var part = string.IsNullOrEmpty(key)
            ? "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "-")
            : "dev:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(part, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });
    o.AddPolicy("clock-user", ctx =>
    {
        var who = ctx.User.FindFirst("sub")?.Value ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "-");
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(who, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });
});
builder.Services.AddEndpointsApiExplorer();
// İç içe aynı adlı kayıtlar (ör. iki denetleyicide DecideInput) çakışmasın diye tam ad.
builder.Services.AddSwaggerGen(c => c.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace('+', '.')));

var app = builder.Build();

app.UseSwagger();
// Swagger arayuzu yalnizca gelistirmede; JSON belge (/swagger/v1/swagger.json) gateway'in
// /api-docs/specs/*.json ucu ve web'deki API belgeleri sayfasi icin her ortamda acik.
if (!app.Environment.IsProduction()) app.UseSwaggerUI();

app.UseAuthentication();
app.UseTenantContext();
app.UseAuthorization();
app.UseRateLimiter();
app.UseHttpMetrics();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "timeshift-service" }));
app.MapControllers();
app.MapMetrics();

app.Run();
