using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LearningService.Observability;

// ---------------------------------------------------------------------------
// G25: Dagitik izleme (OpenTelemetry) + KVKK maskeleme.
// Bu dosya tum .NET servislerinde AYNIDIR (yalnizca namespace farkli); degisiklik
// yapilacaksa hepsine uygulanmali. Birim testleri: tests/dotnet/Leave.Tests.
//
// - OTEL_EXPORTER_OTLP_ENDPOINT tanimli DEGILSE izleme hic kurulmaz (sifir ek yuk).
// - Tanimliysa: gelen HTTP istekleri (rota sablonuyla), giden HttpClient cagrilari,
//   Npgsql sorgulari (parametre degerleri olmadan) ve Kafka yayin/tuketim adimlari
//   OTLP ile OpenTelemetry Collector'a gider.
// - Kisisel veri disari cikmasin diye PiiMaskingProcessor her span'i disa aktarimdan
//   ONCE temizler (Collector'daki redaction ikinci savunma hattidir).
// ---------------------------------------------------------------------------
public static class Telemetry
{
    /// <summary>Kafka yayin/tuketim span'lerinin kaynagi.</summary>
    public const string MessagingSourceName = "HR360.Messaging";
    public static readonly ActivitySource Messaging = new(MessagingSourceName);

    public static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    /// <summary>
    /// OTEL_EXPORTER_OTLP_ENDPOINT varsa izlemeyi kurar. Ornekleme orani
    /// OTEL_TRACES_SAMPLER_ARG (0..1, varsayilan 1) ile, ust span'in kararina uyarak.
    /// </summary>
    public static IServiceCollection AddHrTelemetry(this IServiceCollection services, string serviceName)
    {
        if (!Enabled) return services;

        var ratio = double.TryParse(Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r)
            ? Math.Clamp(r, 0, 1) : 1.0;

        services.AddOpenTelemetry()
            .ConfigureResource(res => res.AddService(serviceName, serviceNamespace: "hr360"))
            .WithTracing(t => t
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                .AddAspNetCoreInstrumentation(o =>
                {
                    // Saglik/metrik/swagger istekleri gurultu; izlenmez.
                    o.Filter = ctx => !IsNoisePath(ctx.Request.Path.Value);
                    // Istisna mesajlari kisisel veri icerebilir; span olayina yazilmaz.
                    o.RecordException = false;
                })
                .AddHttpClientInstrumentation(o => o.RecordException = false)
                // Npgsql'in yerlesik izleme kaynagi: komut metni parametre DEGERLERI olmadan
                // ($1, @p0 yer tutuculariyla) gelir; kalan literaller islemcide silinir.
                .AddSource("Npgsql")
                .AddSource(MessagingSourceName)
                .AddProcessor(new PiiMaskingProcessor())
                .AddOtlpExporter());
        return services;
    }

    internal static bool IsNoisePath(string? path) =>
        path is not null && (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase));

    // ----------------------------------------------------------------- Kafka

    /// <summary>
    /// Kafka'ya yayin icin producer span'i acar ve W3C traceparent'i mesaj basliklarina
    /// yazar. Izleme kapaliyken null doner ve hicbir baslik eklenmez.
    /// </summary>
    public static Activity? StartProducer(string topic, string? eventType, Action<string, string> setHeader)
    {
        var activity = Messaging.StartActivity($"{topic} publish", ActivityKind.Producer);
        if (activity is null) return null;
        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation.type", "publish");
        activity.SetTag("messaging.destination.name", topic);
        if (!string.IsNullOrEmpty(eventType)) activity.SetTag("hr360.event_type", eventType);
        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current), setHeader, static (set, k, v) => set(k, v));
        return activity;
    }

    /// <summary>Basliklardaki traceparent'i okuyup tuketim span'ini onun cocugu olarak acar.</summary>
    public static Activity? StartConsumer(string topic, string? eventType, Func<string, string?> getHeader)
    {
        if (!Messaging.HasListeners()) return null;
        var parent = Propagators.DefaultTextMapPropagator.Extract(default, getHeader,
            static (get, k) => get(k) is { } v ? new[] { v } : Array.Empty<string>());
        var activity = Messaging.StartActivity($"{topic} process", ActivityKind.Consumer, parent.ActivityContext);
        if (activity is null) return null;
        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation.type", "process");
        activity.SetTag("messaging.destination.name", topic);
        if (!string.IsNullOrEmpty(eventType)) activity.SetTag("hr360.event_type", eventType);
        return activity;
    }
}

/// <summary>
/// KVKK: span'ler disa aktarilmadan once kisisel veriden arindirilir.
/// - Yasakli ozellikler silinir (sorgu dizesi, istek/yanit basliklari, kullanici kimligi,
///   istemci IP'si, user-agent, SQL parametreleri, baglanti dizesi, mesaj govdeleri).
/// - SQL metnindeki literaller ('...' ve uzun sayilar) '?' olur.
/// - Kalan tum metin degerlerinde GUID, e-posta, IBAN, TCKN benzeri 11 haneli sayi,
///   uzun sayi dizileri ve JWT/Bearer jetonlari yer tutucuyla degistirilir.
/// - Ust span'i olmayan (arka plan dongusu) Npgsql sorgulari disa aktarilmaz (gurultu).
/// </summary>
public sealed class PiiMaskingProcessor : BaseProcessor<Activity>
{
    private static readonly string[] DroppedPrefixes =
    {
        "http.request.header.", "http.response.header.", "enduser.", "db.query.parameter.",
        "db.statement.parameter", "messaging.message.body", "messaging.kafka.message.key",
        "http.request.body", "http.response.body", "rpc.request.metadata.", "rpc.response.metadata.",
    };

    private static readonly HashSet<string> DroppedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "url.query", "http.query", "user_agent.original", "http.user_agent", "client.address",
        "client.port", "client.socket.address", "net.sock.peer.addr", "http.client_ip",
        "messaging.message.id", "messaging.kafka.message.key", "user.id", "user.email", "user.name",
        // Npgsql baglanti dizesi (sifresiz olsa da altyapi bilgisi; gereksiz).
        "db.connection_string",
    };

    private static readonly HashSet<string> SqlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "db.statement", "db.query.text",
    };

    public override void OnEnd(Activity activity)
    {
        // Arka plan donguleri (outbox taramasi vb.) her birkac saniyede bir kok Npgsql
        // span'i uretir; bir istek/olay baglami olmayan sorgular aktarilmaz.
        if (activity.Source.Name == "Npgsql" && activity.Parent is null && activity.ParentSpanId == default)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }
        Sanitize(activity);
    }

    public static void Sanitize(Activity activity)
    {
        var maskedName = PiiMasker.Mask(activity.DisplayName);
        if (!ReferenceEquals(maskedName, activity.DisplayName) && maskedName != activity.DisplayName)
            activity.DisplayName = maskedName;

        foreach (var tag in activity.TagObjects.ToList())
        {
            var key = tag.Key;
            if (ShouldDrop(key))
            {
                activity.SetTag(key, null);
                continue;
            }
            switch (tag.Value)
            {
                case string s:
                    var clean = SqlKeys.Contains(key) ? PiiMasker.Mask(PiiMasker.StripSqlLiterals(s)) : PiiMasker.Mask(s);
                    if (clean != s) activity.SetTag(key, clean);
                    break;
                case string[] arr:
                    activity.SetTag(key, arr.Select(PiiMasker.Mask).ToArray());
                    break;
            }
        }

        if (activity.StatusDescription is { Length: > 0 } desc)
        {
            var clean = PiiMasker.Mask(desc);
            if (clean != desc) activity.SetStatus(activity.Status, clean);
        }
    }

    internal static bool ShouldDrop(string key)
    {
        if (DroppedKeys.Contains(key)) return true;
        foreach (var p in DroppedPrefixes)
            if (key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>Metin icindeki kisisel veri kaliplarini yer tutucuyla degistirir.</summary>
public static class PiiMasker
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", Opts, Timeout);
    private static readonly Regex Bearer = new(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", Opts, Timeout);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", Opts, Timeout);
    private static readonly Regex Guid = new(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", Opts, Timeout);
    // IBAN: 2 harf ulke kodu + 2 kontrol hanesi + 11..30 alfanumerik (bosluklu yazim dahil).
    private static readonly Regex Iban = new(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b", Opts, Timeout);
    // TCKN benzeri: tam 11 haneli sayi (telefon numaralari da bu kalibi yakalar).
    private static readonly Regex Tckn = new(@"(?<![\d.])\d{11}(?![\d])", Opts, Timeout);
    // Diger uzun sayi dizileri (hesap/kart/telefon): 10+ hane.
    private static readonly Regex LongNumber = new(@"(?<![\d.])\d{10,}(?![\d])", Opts, Timeout);
    private static readonly Regex SqlString = new(@"'(?:[^']|'')*'", Opts, Timeout);
    private static readonly Regex SqlNumber = new(@"(?<![\w$@.])\d{4,}(?![\w.])", Opts, Timeout);

    /// <summary>Degismediyse ayni ornegi dondurur.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        try
        {
            var s = value;
            s = Jwt.Replace(s, "{token}");
            s = Bearer.Replace(s, "$1 {token}");
            s = Email.Replace(s, "{email}");
            s = Guid.Replace(s, "{guid}");
            s = Iban.Replace(s, "{iban}");
            s = Tckn.Replace(s, "{tckn}");
            s = LongNumber.Replace(s, "{number}");
            return s == value ? value : s;
        }
        catch (RegexMatchTimeoutException)
        {
            return "{redacted}";
        }
    }

    /// <summary>SQL metnindeki metin literallerini ve 4+ haneli sayilari '?' yapar.</summary>
    public static string StripSqlLiterals(string sql)
    {
        try
        {
            var s = SqlString.Replace(sql, "'?'");
            return SqlNumber.Replace(s, "?");
        }
        catch (RegexMatchTimeoutException)
        {
            return "{redacted}";
        }
    }
}
