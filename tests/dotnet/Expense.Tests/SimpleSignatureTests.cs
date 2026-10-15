using System.Net;
using System.Text.Json;
using ExpenseService.Models;
using ExpenseService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Expense.Tests;

public class SimpleSignatureTests
{
    [Fact]
    public void Disclaimer_is_the_single_unified_text()
    {
        Assert.Equal("Basit elektronik imza — 5070 sayılı Kanun kapsamında nitelikli (güvenli) elektronik imza değildir.", SimpleSignature.Disclaimer);
        Assert.Equal("HrDocument", SimpleSignature.DocumentType);
    }

    [Theory]
    [InlineData("203.0.113.57", "203.0.113.0")]
    [InlineData("203.0.113.57, 10.0.0.1", "203.0.113.0")]
    [InlineData("::ffff:198.51.100.9", "198.51.100.0")]
    [InlineData("2001:db8:abcd:12:34::1", "2001:db8:abcd::")]
    [InlineData("not-an-ip", null)]
    [InlineData(null, null)]
    public void Ip_is_masked(string? raw, string? expected) => Assert.Equal(expected, SimpleSignature.MaskIp(raw));

    private static Document Doc() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        EmployeeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        FileName = "Sözleşme.pdf", StorageKey = "docs/a.pdf", SizeBytes = 1234, ContentType = "application/pdf",
        Type = DocumentType.Contract, UploadedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000),
    };

    [Fact]
    public void Document_hash_is_stable_and_detects_changes()
    {
        var h = SimpleSignature.DocumentHash(Doc());
        Assert.Equal(64, h.Length);
        Assert.Equal(h, SimpleSignature.DocumentHash(Doc()));
        var renamed = Doc(); renamed.FileName = "Sözleşme-v2.pdf";
        var moved = Doc(); moved.StorageKey = "docs/b.pdf";
        var resized = Doc(); resized.SizeBytes = 1235;
        Assert.NotEqual(h, SimpleSignature.DocumentHash(renamed));
        Assert.NotEqual(h, SimpleSignature.DocumentHash(moved));
        Assert.NotEqual(h, SimpleSignature.DocumentHash(resized));
        // Postgres mikro saniye hassasiyeti ozeti bozmaz (ms'ye yuvarlanir).
        var micro = Doc(); micro.UploadedAt = micro.UploadedAt.AddTicks(7);
        Assert.Equal(h, SimpleSignature.DocumentHash(micro));
    }

    [Fact]
    public void Legacy_evidence_hash_covers_all_fields()
    {
        var e = new SignatureEvidence
        {
            TenantSlug = "demo", SignatureId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), SignerEmployeeId = Guid.NewGuid(),
            SignedAt = DateTimeOffset.UtcNow, DocumentHash = new string('b', 64), IpMasked = "203.0.113.0",
            UserAgentHash = SimpleSignature.HashUserAgent("Mozilla/5.0"), OtpChannel = "InApp+Email", EvidenceHash = "",
        };
        var h = SimpleSignature.EvidenceHash(e);
        e.EvidenceHash = h;
        Assert.Equal(h, SimpleSignature.EvidenceHash(e));
        e.IpMasked = "203.0.114.0";
        Assert.NotEqual(h, SimpleSignature.EvidenceHash(e));
        Assert.Null(SimpleSignature.HashUserAgent("  "));
        Assert.Equal(64, SimpleSignature.HashUserAgent("Mozilla/5.0")!.Length);
    }

    /* ------------------------------------------------------------ governance imza motoruna devir */

    private static GovEvidence Gov(string method = "OTP-InApp+Email") => new(
        Guid.NewGuid(), "HrDocument", Guid.NewGuid(), 2, new string('c', 64), Guid.NewGuid(), DateTimeOffset.UtcNow, method,
        "203.0.113.0/24", "Sözleşme.pdf", SimpleSignature.Disclaimer, new string('d', 64), true);

    [Theory]
    [InlineData("OTP-InApp+Email", "InApp+Email")]
    [InlineData("OTP-Email", "Email")]
    [InlineData("OTP-InApp", "InApp")]
    [InlineData("SimpleElectronicSignature-OTP", "SimpleElectronicSignature-OTP")]
    public void Channel_is_derived_from_method(string method, string channel) => Assert.Equal(channel, SignatureViews.ChannelFromMethod(method));

    [Fact]
    public void Governance_evidence_maps_to_legacy_view_fields()
    {
        var g = Gov();
        var sid = Guid.NewGuid();
        var j = JsonSerializer.SerializeToElement(SignatureViews.Evidence(sid, g), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(g.Id, j.GetProperty("id").GetGuid());
        Assert.Equal(sid, j.GetProperty("signatureId").GetGuid());
        Assert.Equal(g.DocumentSha256, j.GetProperty("documentHash").GetString());
        Assert.Equal("203.0.113.0/24", j.GetProperty("ipMasked").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("userAgentHash").ValueKind);
        Assert.Equal("InApp+Email", j.GetProperty("otpChannel").GetString());
        Assert.Equal("OTP-InApp+Email", j.GetProperty("method").GetString());
        Assert.Equal(g.EvidenceSha256, j.GetProperty("evidenceHash").GetString());
        Assert.True(j.GetProperty("integrityOk").GetBoolean());
        Assert.Equal("governance", j.GetProperty("source").GetString());
    }

    [Fact]
    public void Legacy_evidence_view_still_verifies()
    {
        var e = new SignatureEvidence
        {
            TenantSlug = "demo", SignatureId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), SignerEmployeeId = Guid.NewGuid(),
            SignedAt = DateTimeOffset.UtcNow, DocumentHash = new string('b', 64), OtpChannel = "InApp", EvidenceHash = "",
        };
        e.EvidenceHash = SimpleSignature.EvidenceHash(e);
        var j = JsonSerializer.SerializeToElement(SignatureViews.Legacy(e), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(j.GetProperty("integrityOk").GetBoolean());
        Assert.Equal("legacy", j.GetProperty("source").GetString());
    }

    [Fact]
    public void Error_body_is_passed_through_with_code_and_attempts()
    {
        var r = GovernanceSignatureClient.ParseError<GovOtp>(400, """{"message":"Kod hatalı. Kalan deneme: 2.","code":"otp_invalid","attemptsLeft":2}""");
        Assert.Equal((400, "otp_invalid", 2), (r.Status, r.Code, r.AttemptsLeft));
        Assert.False(r.Ok);
        var cool = GovernanceSignatureClient.ParseError<GovOtp>(429, """{"message":"bekleyin","code":"otp_cooldown"}""");
        Assert.Equal((429, "otp_cooldown", (int?)null), (cool.Status, cool.Code, cool.AttemptsLeft));
        // Govdesiz 404 = ic uc kapali/anahtar yanlis: imza servisi kullanilamaz.
        var closed = GovernanceSignatureClient.ParseError<GovOtp>(404, "");
        Assert.Equal((503, GovernanceSignatureClient.Unavailable), (closed.Status, closed.Code));
    }

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<(string Url, string? Token, string? Lang, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-Internal-Token", out var t) ? t.First() : null,
                request.Headers.TryGetValues("X-HR360-Lang", out var l) ? l.First() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static GovernanceSignatureClient Client(Stub stub, string? lang = null)
    {
        var acc = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (lang is not null) acc.HttpContext.Request.Headers["X-HR360-Lang"] = lang;
        return new GovernanceSignatureClient(new HttpClient(stub), acc, NullLogger<GovernanceSignatureClient>.Instance);
    }

    [Fact]
    public async Task Sign_delegates_to_internal_endpoint_with_token_and_hr_document_type()
    {
        // InternalChatTests ile ayni anahtar (ortam degiskeni surec geneli).
        Environment.SetEnvironmentVariable("INTERNAL_SERVICE_TOKEN", "unit-test-internal-token");
        var g = Gov();
        var stub = new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(g, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var doc = Guid.NewGuid(); var emp = Guid.NewGuid(); var otp = Guid.NewGuid();
        var r = await Client(stub, "en").SignAsync("demo", doc, emp, otp, "123456", new string('a', 64), 2, "203.0.113.9", "u1", "Ayşe", "Sözleşme.pdf", default);
        Assert.True(r.Ok);
        Assert.Equal(g.Id, r.Value!.Id);
        var call = Assert.Single(stub.Calls);
        Assert.EndsWith("/api/internal/signatures/sign", call.Url);
        Assert.Equal("unit-test-internal-token", call.Token);
        Assert.Equal("en", call.Lang);
        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("demo", body.GetProperty("tenantSlug").GetString());
        Assert.Equal("HrDocument", body.GetProperty("documentType").GetString());
        Assert.Equal(doc, body.GetProperty("documentId").GetGuid());
        Assert.Equal(emp, body.GetProperty("employeeId").GetGuid());
        Assert.Equal(otp, body.GetProperty("otpId").GetGuid());
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal(new string('a', 64), body.GetProperty("documentSha256").GetString());
    }

    [Fact]
    public async Task Otp_error_and_unreachable_engine_are_reported()
    {
        var stub = new Stub(HttpStatusCode.TooManyRequests, """{"message":"Yeni kod için lütfen biraz bekleyin (30 sn).","code":"otp_cooldown"}""");
        var r = await Client(stub).RequestOtpAsync("demo", Guid.NewGuid(), Guid.NewGuid(), "Sözleşme.pdf", "InApp+Email", 1, default);
        Assert.Equal((429, "otp_cooldown"), (r.Status, r.Code));
        Assert.Equal("InApp+Email", JsonDocument.Parse(stub.Calls[0].Body).RootElement.GetProperty("channel").GetString());

        var down = new GovernanceSignatureClient(new HttpClient(new Throwing()), new HttpContextAccessor(), NullLogger<GovernanceSignatureClient>.Instance);
        var d = await down.RequestOtpAsync("demo", Guid.NewGuid(), Guid.NewGuid(), "x", "InApp", 1, default);
        Assert.Equal((503, GovernanceSignatureClient.Unavailable), (d.Status, d.Code));
        Assert.Empty(await down.EvidenceAsync("demo", Guid.NewGuid(), null, default));
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task Evidence_lookup_returns_dictionary_by_id()
    {
        var a = Gov(); var b = Gov("OTP-InApp");
        var stub = new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = new[] { a, b } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var map = await Client(stub).EvidenceAsync("demo", null, new[] { a.Id, b.Id }, default);
        Assert.Equal(2, map.Count);
        Assert.Equal("OTP-InApp", map[b.Id].Method);
        Assert.Empty(await Client(stub).EvidenceAsync("demo", null, Array.Empty<Guid>(), default));
        Assert.Single(stub.Calls);
    }
}
