using System.Diagnostics;
using LeaveService.Observability;
using Xunit;

namespace Leave.Tests;

/// <summary>
/// G25: OpenTelemetry KVKK maskeleme islemcisi. Ayni dosya (Observability/Telemetry.cs)
/// tum servislerde kopya; bu testler leave-service kopyasini dogrular.
/// </summary>
public class TelemetryMaskingTests : IDisposable
{
    private readonly ActivitySource _source = new("HR360.Tests.Masking");
    private readonly ActivityListener _listener;

    public TelemetryMaskingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "HR360.Tests.Masking",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Theory]
    [InlineData("/api/leave-requests/8c7dd608-46e2-4bee-900f-6a175b21d3b2", "/api/leave-requests/{guid}")]
    [InlineData("kime: ayse.yilmaz@ornek.com.tr", "kime: {email}")]
    [InlineData("TCKN 12345678901 kaydi", "TCKN {tckn} kaydi")]
    [InlineData("IBAN TR330006100519786457841326", "IBAN {iban}")]
    [InlineData("IBAN TR33 0006 1005 1978 6457 8413 26", "IBAN {iban}")]
    [InlineData("tel 5321234567", "tel {number}")]
    [InlineData("Authorization: Bearer abcdefghijklmnop.qrs", "Authorization: Bearer {token}")]
    [InlineData("t=eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl", "t={token}")]
    public void Mask_replaces_personal_data(string input, string expected)
    {
        Assert.Equal(expected, PiiMasker.Mask(input));
    }

    [Theory]
    [InlineData("GET api/leave-requests/{id}")]
    [InlineData("hr360_operational")]
    [InlineData("tutar 1250.50 TL, 3 gun")]
    [InlineData("2026-10-03")]
    public void Mask_keeps_harmless_values(string input)
    {
        Assert.Same(input, PiiMasker.Mask(input));
    }

    [Fact]
    public void StripSqlLiterals_removes_string_and_long_numeric_literals()
    {
        var sql = "SELECT * FROM employees WHERE \"Email\" = 'ali@x.com' AND \"NationalId\" = 12345678901 AND \"Id\" = @p0 LIMIT 50";
        var clean = PiiMasker.StripSqlLiterals(sql);
        Assert.DoesNotContain("ali@x.com", clean);
        Assert.DoesNotContain("12345678901", clean);
        Assert.Contains("@p0", clean);
        Assert.Contains("LIMIT 50", clean);
    }

    [Fact]
    public void Processor_drops_forbidden_attributes_and_masks_the_rest()
    {
        using var a = _source.StartActivity("GET /api/employees/8c7dd608-46e2-4bee-900f-6a175b21d3b2", ActivityKind.Server)!;
        a.SetTag("http.route", "api/employees/{id}");
        a.SetTag("url.path", "/api/employees/8c7dd608-46e2-4bee-900f-6a175b21d3b2");
        a.SetTag("url.query", "?email=ayse@ornek.com");
        a.SetTag("http.request.header.authorization", new[] { "Bearer secret-token-value" });
        a.SetTag("http.request.header.x-device-key", new[] { "dev-key" });
        a.SetTag("http.request.header.cookie", new[] { "sid=1" });
        a.SetTag("enduser.id", "ayse");
        a.SetTag("user_agent.original", "Mozilla/5.0");
        a.SetTag("client.address", "10.1.2.3");
        a.SetTag("db.statement", "UPDATE x SET \"Iban\" = 'TR330006100519786457841326' WHERE \"Id\" = $1");
        a.SetTag("db.query.parameter.0", "8c7dd608-46e2-4bee-900f-6a175b21d3b2");
        a.SetTag("db.connection_string", "Host=postgres;Database=hr360_operational;Username=hr360admin");
        a.SetTag("http.response.status_code", 200);
        a.SetStatus(ActivityStatusCode.Error, "kayit yok: mehmet@ornek.com");

        new PiiMaskingProcessor().OnEnd(a);

        Assert.Equal("api/employees/{id}", a.GetTagItem("http.route"));
        Assert.Equal("/api/employees/{guid}", a.GetTagItem("url.path"));
        Assert.Equal("GET /api/employees/{guid}", a.DisplayName);
        Assert.Null(a.GetTagItem("url.query"));
        Assert.Null(a.GetTagItem("http.request.header.authorization"));
        Assert.Null(a.GetTagItem("http.request.header.x-device-key"));
        Assert.Null(a.GetTagItem("http.request.header.cookie"));
        Assert.Null(a.GetTagItem("enduser.id"));
        Assert.Null(a.GetTagItem("user_agent.original"));
        Assert.Null(a.GetTagItem("client.address"));
        Assert.Null(a.GetTagItem("db.query.parameter.0"));
        Assert.Null(a.GetTagItem("db.connection_string"));
        Assert.Equal("UPDATE x SET \"Iban\" = '?' WHERE \"Id\" = $1", a.GetTagItem("db.statement"));
        Assert.Equal(200, a.GetTagItem("http.response.status_code"));
        Assert.Equal("kayit yok: {email}", a.StatusDescription);
        Assert.True(a.Recorded);
    }

    [Fact]
    public void Processor_skips_parentless_npgsql_spans()
    {
        using var npgsql = new ActivitySource("Npgsql");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var root = npgsql.StartActivity("hr360_operational", ActivityKind.Client)!;
            new PiiMaskingProcessor().OnEnd(root);
            Assert.False(root.Recorded);
        }
        finally { Activity.Current = previous; }

        using var parent = _source.StartActivity("POST api/leave-requests", ActivityKind.Server)!;
        using var child = npgsql.StartActivity("hr360_operational", ActivityKind.Client)!;
        new PiiMaskingProcessor().OnEnd(child);
        Assert.True(child.Recorded);
    }

    [Fact]
    public void Kafka_context_round_trips_through_headers_when_tracing_is_on()
    {
        // Izleme kapaliyken (dinleyici yok) hicbir baslik yazilmaz.
        var headers = new Dictionary<string, string>();
        using (var none = Telemetry.StartProducer("hr360.leave.events", "leave.approved", (k, v) => headers[k] = v))
        {
            Assert.Null(none);
            Assert.Empty(headers);
        }

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.MessagingSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(new OpenTelemetry.Context.Propagation.TraceContextPropagator());

        using var producer = Telemetry.StartProducer("hr360.leave.events", "leave.approved", (k, v) => headers[k] = v)!;
        Assert.True(headers.ContainsKey("traceparent"));
        producer.Stop();

        using var consumer = Telemetry.StartConsumer("hr360.leave.events", "leave.approved",
            k => headers.TryGetValue(k, out var v) ? v : null)!;
        Assert.Equal(producer.TraceId, consumer.TraceId);
        Assert.Equal(producer.SpanId, consumer.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, consumer.Kind);
    }
}
