// OTOMATIK KOPYA: kaynak apps/shared-dotnet (scripts/sync-shared.sh). Bu dosyayi duzenlemeyin.
using System.Collections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HR360.Platform;

/// <summary>
/// Yapılandırılmış günlük: <c>HR360_LOG_FORMAT=json</c> (docker-compose varsayılanı) ile her satır tek
/// satırlık JSON olur; istek içinde yazılan satırlara <c>CorrelationId</c> (ağ geçidinin X-Correlation-Id'si,
/// denetim kaydındakiyle aynı) ve varsa <c>Tenant</c> eklenir. Böylece bir hata, Loki'de ya da
/// <c>docker logs</c>'ta isteğin kimliğiyle bulunur. Başka değerde (ör. geliştirme) düz metin kalır.
///
/// KVKK: kapsama yalnızca istek kimliği ve şirket kısa adı girer; kullanıcı, e-posta, IP girmez.
/// </summary>
public static class StructuredLogging
{
    public static bool JsonEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("HR360_LOG_FORMAT"), "json", StringComparison.OrdinalIgnoreCase);

    public static WebApplicationBuilder AddHr360Logging(this WebApplicationBuilder builder)
    {
        if (!JsonEnabled) return builder;
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        return builder;
    }

    /// <summary>İsteğin en başına konur; kapsam isteğin sonuna kadar açık kalır.</summary>
    public static WebApplication UseHr360LogScope(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HR360.Request");
        app.Use(async (ctx, next) =>
        {
            using (logger.BeginScope(new RequestScope(CorrelationId(ctx))))
                await next();
        });
        return app;
    }

    /// <summary>Başlıktaki değer yalnızca güvenli karakterlerle ve 64 karaktere kadar alınır (günlük enjeksiyonu yok).</summary>
    public static string CorrelationId(HttpContext ctx)
    {
        var h = ctx.Request.Headers["X-Correlation-Id"].ToString();
        if (h.Length is > 0 and <= 64 && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return h;
        return ctx.TraceIdentifier;
    }

    /// <summary>
    /// Kiracı, kapsam açıldıktan SONRA (kimlik doğrulama/TenantMiddleware) atanır; bu yüzden değer her
    /// günlük satırı yazılırken okunur (TenantSession.Current, AsyncLocal).
    /// </summary>
    public sealed class RequestScope(string correlationId) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private KeyValuePair<string, object?>[] Items() => TenantSession.Current is { } t
            ? [new("CorrelationId", correlationId), new("Tenant", t)]
            : [new("CorrelationId", correlationId)];

        public int Count => Items().Length;
        public KeyValuePair<string, object?> this[int index] => Items()[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)Items()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => TenantSession.Current is { } t ? $"cid={correlationId} tenant={t}" : $"cid={correlationId}";
    }
}
