using System.Text.Json;
using Npgsql;

namespace GovernanceService.Infrastructure;

/// <summary>İmza kanıtı satırı (governance_signatures). Bütünlük, saklı alanlardan yeniden hesaplanır.</summary>
public sealed record SignatureEvidence(Guid Id, string DocumentType, Guid DocumentId, int DocumentVersion, string DocumentSha256, Guid SignerEmployeeId,
    DateTime SignedAt, string Method, string? IpPrefix, string Disclaimer, string EvidenceSha256, string? Title)
{
    public bool IntegrityOk =>
        Signatures.Sha256Hex(Signatures.Canonical(DocumentType, DocumentId, DocumentVersion, DocumentSha256, SignerEmployeeId, SignedAt, Method, IpPrefix, Disclaimer))
        == EvidenceSha256;
}

/// <summary>İmza motoru hatası: HTTP durumu + makine kodu + iki dilde mesaj (+ kalan deneme).</summary>
public sealed record SignatureError(int Status, string Code, string MessageTr, string MessageEn, int? AttemptsLeft = null)
{
    public object Body(bool en) => AttemptsLeft is { } left
        ? new { message = en ? MessageEn : MessageTr, code = Code, attemptsLeft = left }
        : new { message = en ? MessageEn : MessageTr, code = Code };
}

public sealed record OtpIssued(Guid OtpId, string Channel, DateTime ExpiresAt, int MaxAttempts, int SendsLeft);

/// <summary>İmza isteği. <c>EmployeeId</c> imzalayanın kimliğidir; <c>SignerKind</c> = Candidate ise aday kimliğidir.</summary>
public sealed record SignRequest(string Tenant, string DocumentType, Guid DocumentId, Guid EmployeeId, Guid? OtpId, string? Code,
    string DocumentSha256, int Version, string? IpRaw, string? UserId, string? UserName, string? Title, string SignerKind = Signatures.SignerEmployee);

/// <summary>
/// Y28 — TEK imza motoru. Belge türünden bağımsızdır: çağıran (DocumentRequest web uçları ya da
/// expense-service iç uçlar üzerinden HrDocument) belgenin sahipliğini ve içerik özetini belirler;
/// motor kodu üretir/doğrular, kanıtı yazar, denetim kaydı ve <c>document.signed</c> olayını üretir.
/// Kurallar <see cref="Signatures"/>'ta: 6 hane, 10 dk, 5 hatalı deneme, belge başına saatte 5 kod,
/// 30 sn yeniden gönderme beklemesi, kod yalnızca HMAC ile saklanır, yeni kod öncekileri geçersiz
/// kılar, kod imzadan ÖNCE tüketilir (tek kullanımlık), IP /24 (/48) kısaltılır.
/// </summary>
public sealed class SignatureEngine
{
    private readonly Sql _sql;
    private readonly Notifier _notifier;
    private readonly IServiceProvider _sp;
    public SignatureEngine(Sql sql, Notifier notifier, IServiceProvider sp) { _sql = sql; _notifier = notifier; _sp = sp; }

    private const string EvidenceColumns =
        "\"Id\",\"DocumentType\",\"DocumentId\",\"DocumentVersion\",\"DocumentSha256\",\"SignerEmployeeId\",\"SignedAt\",\"Method\",\"IpPrefix\",\"Disclaimer\",\"EvidenceSha256\",\"Title\"";

    private static SignatureEvidence Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetGuid(5),
        r.GetFieldValue<DateTime>(6), r.GetString(7), r.Str(8), r.GetString(9), r.GetString(10), r.Str(11));

    public static SignatureError AlreadySigned() => new(409, "already_signed", "Belge zaten imzalanmış.", "The document is already signed.");

    /* ------------------------------------------------------------------ kanıt sorguları */

    /// <summary>Belgenin (tür + kimlik) en son kanıtı.</summary>
    public static async Task<SignatureEvidence?> LatestAsync(Sql sql, string tenant, string documentType, Guid documentId, CancellationToken ct) =>
        (await sql.QueryAsync($"""
            SELECT {EvidenceColumns} FROM governance_signatures
            WHERE "TenantSlug" = $1 AND "DocumentType" = $2 AND "DocumentId" = $3 ORDER BY "SignedAt" DESC LIMIT 1
            """, Map, ct, tenant, documentType, documentId)).FirstOrDefault();

    /// <summary>Belgenin tüm kanıtları ve/veya kimlikle istenen kanıtlar (aynı tür içinde).</summary>
    public static Task<List<SignatureEvidence>> ListAsync(Sql sql, string tenant, string documentType, Guid? documentId, Guid[]? ids, CancellationToken ct) =>
        sql.QueryAsync($"""
            SELECT {EvidenceColumns} FROM governance_signatures
            WHERE "TenantSlug" = $1 AND "DocumentType" = $2
              AND ($3::uuid IS NULL OR "DocumentId" = $3::uuid)
              AND ($4::uuid[] IS NULL OR "Id" = ANY($4::uuid[]))
            ORDER BY "SignedAt" DESC LIMIT 500
            """, Map, ct, tenant, documentType, (object?)documentId ?? DBNull.Value, (object?)ids ?? DBNull.Value);

    /// <summary>Çalışanın imzaladığı tüm belgeler (her tür). Eski kayıtlarda başlık belge talebinden tamamlanır.
    /// Aday imzaları (SignerKind = Candidate) hariç tutulur.</summary>
    public static Task<List<SignatureEvidence>> MineAsync(Sql sql, string tenant, Guid employeeId, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT s."Id",s."DocumentType",s."DocumentId",s."DocumentVersion",s."DocumentSha256",s."SignerEmployeeId",s."SignedAt",s."Method",
                   s."IpPrefix",s."Disclaimer",s."EvidenceSha256",COALESCE(s."Title", d."TemplateName")
            FROM governance_signatures s
            LEFT JOIN governance_document_requests d ON s."DocumentType" = 'DocumentRequest' AND d."Id" = s."DocumentId" AND d."TenantSlug" = s."TenantSlug"
            WHERE s."TenantSlug" = $1 AND s."SignerEmployeeId" = $2 AND s."SignerKind" = 'Employee' ORDER BY s."SignedAt" DESC LIMIT 300
            """, Map, ct, tenant, employeeId);

    private async Task<bool> SignedAsync(string tenant, string documentType, Guid documentId, int version, CancellationToken ct) =>
        await _sql.ScalarAsync("""
            SELECT EXISTS (SELECT 1 FROM governance_signatures WHERE "TenantSlug" = $1 AND "DocumentType" = $2 AND "DocumentId" = $3 AND "DocumentVersion" = $4)
            """, ct, tenant, documentType, documentId, version) is true;

    /* ------------------------------------------------------------------ kod iste */

    /// <param name="externalEmail">Dış imzalayan (aday) için kodun gideceği e-posta; verilirse kanal her zaman Email'dir
    /// ve bildirim çalışan kaydına bağlanmaz. Kimlik (<paramref name="employeeId"/>) bu durumda aday kimliğidir.</param>
    public async Task<(OtpIssued? Otp, SignatureError? Error)> RequestOtpAsync(string tenant, string documentType, Guid documentId, Guid employeeId,
        string title, string? channel, int version, CancellationToken ct, string? externalEmail = null, bool externalEn = false)
    {
        if (await SignedAsync(tenant, documentType, documentId, version, ct)) return (null, AlreadySigned());
        var ch = externalEmail is null ? Signatures.NormalizeChannel(channel) : "Email";
        var stats = (await _sql.QueryAsync("""
            SELECT count(*) FILTER (WHERE "CreatedAt" > now() - interval '1 hour')::int, max("CreatedAt")
            FROM governance_signature_otps WHERE "TenantSlug" = $1 AND "DocumentType" = $2 AND "DocumentId" = $3
            """, r => (Recent: r.GetInt32(0), Last: r.Ts(1)), ct, tenant, documentType, documentId)).First();
        switch (Signatures.RateLimit(stats.Recent, stats.Last, DateTime.UtcNow))
        {
            case "otp_rate_limited":
                return (null, new(429, "otp_rate_limited", $"Bu belge için saatte en fazla {Signatures.MaxCodesPerHour} kod istenebilir.",
                    $"At most {Signatures.MaxCodesPerHour} codes per hour can be requested for this document."));
            case "otp_cooldown":
                return (null, new(429, "otp_cooldown", $"Yeni kod için lütfen biraz bekleyin ({(int)Signatures.ResendCooldown.TotalSeconds} sn).",
                    $"Please wait a moment before requesting a new code ({(int)Signatures.ResendCooldown.TotalSeconds} s)."));
        }
        if (ch != "InApp" && externalEmail is null)
        {
            var email = await _sql.ScalarAsync("SELECT \"Email\" FROM employee_employees WHERE \"TenantSlug\" = $1 AND \"Id\" = $2", ct, tenant, employeeId) as string;
            if (string.IsNullOrWhiteSpace(email))
            {
                if (ch == "Email")
                    return (null, new(400, "no_email", "Kayıtlı e-posta adresiniz yok; uygulama içi kodu kullanın.", "You have no email address on record; use the in-app code."));
                ch = "InApp";
            }
        }
        // Önceki kullanılmamış kodlar geçersiz olur.
        await _sql.ExecuteAsync("""
            UPDATE governance_signature_otps SET "ConsumedAt" = now()
            WHERE "TenantSlug" = $1 AND "DocumentType" = $2 AND "DocumentId" = $3 AND "ConsumedAt" IS NULL
            """, ct, tenant, documentType, documentId);
        var otpId = Guid.NewGuid();
        var code = Signatures.NewCode();
        var expires = DateTime.UtcNow.AddMinutes(Signatures.ValidMinutes);
        await _sql.ExecuteAsync("""
            INSERT INTO governance_signature_otps ("Id","TenantSlug","DocumentType","DocumentId","EmployeeId","CodeHash","Channel","Attempts","ExpiresAt","CreatedAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,0,$8,now())
            """, ct, otpId, tenant, documentType, documentId, employeeId, Signatures.Hash(otpId, code), ch, expires);
        // Kod yalnızca bildirimle gider; yanıtta DÖNMEZ. Belge içeriği bildirime yazılmaz (yalnızca başlık).
        var bodyTr = $"\"{title}\" belgesini imzalamak için tek kullanımlık kodunuz: {code}. Kod {Signatures.ValidMinutes} dakika geçerlidir; kimseyle paylaşmayın, İK dahil kimse sizden bu kodu istemez. {Signatures.DisclaimerTr}";
        var bodyEn = $"Your one-time code to sign \"{title}\": {code}. The code is valid for {Signatures.ValidMinutes} minutes; do not share it — nobody, including HR, will ask you for it. {Signatures.DisclaimerEn}";
        if (externalEmail is not null)
            await _notifier.ExternalEmailAsync(tenant, externalEmail, externalEn ? "Document signing code" : "Belge imza kodu", externalEn ? bodyEn : bodyTr,
                externalEn ? "en" : "tr", "signature.otp", ct);
        else foreach (var c in ch == "InApp+Email" ? new[] { "InApp", "Email" } : new[] { ch })
            await _notifier.LocalizedAsync(tenant, employeeId, "Belge imza kodu", "Document signing code", bodyTr, bodyEn, "signature.otp", ct, c);
        return (new OtpIssued(otpId, ch, expires, Signatures.MaxAttempts, Math.Max(0, Signatures.MaxCodesPerHour - stats.Recent - 1)), null);
    }

    /* ------------------------------------------------------------------ imzala */

    public async Task<(SignatureEvidence? Evidence, SignatureError? Error)> SignAsync(SignRequest q, CancellationToken ct)
    {
        if (await SignedAsync(q.Tenant, q.DocumentType, q.DocumentId, q.Version, ct)) return (null, AlreadySigned());
        // Kod kimliği verilmezse belgenin bu çalışana gönderilmiş en son kodu kullanılır.
        var otp = (await _sql.QueryAsync("""
            SELECT "Id","CodeHash","Channel","Attempts","ExpiresAt","ConsumedAt" FROM governance_signature_otps
            WHERE "TenantSlug" = $1 AND "DocumentType" = $2 AND "DocumentId" = $3 AND "EmployeeId" = $4 AND ($5::uuid IS NULL OR "Id" = $5::uuid)
            ORDER BY "CreatedAt" DESC LIMIT 1
            """, x => (Id: x.GetGuid(0), Hash: x.GetString(1), Channel: x.GetString(2), Attempts: x.GetInt32(3), Expires: x.GetFieldValue<DateTime>(4), Consumed: x.Ts(5)),
            ct, q.Tenant, q.DocumentType, q.DocumentId, q.EmployeeId, (object?)q.OtpId ?? DBNull.Value)).FirstOrDefault();
        if (otp.Hash is null) return (null, new(404, "otp_not_found", "Kod bulunamadı; önce kod isteyin.", "Code not found; request a code first."));
        switch (Signatures.State(otp.Expires, otp.Attempts, otp.Consumed, DateTime.UtcNow))
        {
            case "consumed": return (null, new(410, "otp_used", "Bu kod artık geçerli değil; yeni kod isteyin.", "This code is no longer valid; request a new one."));
            case "expired": return (null, new(410, "otp_expired", $"Kodun süresi doldu ({Signatures.ValidMinutes} dk); yeni kod isteyin.", $"The code has expired ({Signatures.ValidMinutes} min); request a new one."));
            case "locked": return (null, Locked());
        }
        if (!Signatures.Matches(otp.Id, q.Code, otp.Hash))
        {
            var attempts = await _sql.ScalarAsync("UPDATE governance_signature_otps SET \"Attempts\" = \"Attempts\" + 1 WHERE \"Id\" = $1 RETURNING \"Attempts\"", ct, otp.Id);
            var left = Math.Max(0, Signatures.MaxAttempts - Convert.ToInt32(attempts ?? otp.Attempts + 1));
            return (null, left == 0 ? Locked() with { AttemptsLeft = 0 }
                : new(400, "otp_invalid", $"Kod hatalı. Kalan deneme: {left}.", $"Wrong code. Attempts left: {left}.", left));
        }
        // Tek kullanımlık: kodu önce tüket (eşzamanlı ikinci istek imza üretemez).
        var consumed = await _sql.ExecuteAsync("UPDATE governance_signature_otps SET \"ConsumedAt\" = now() WHERE \"Id\" = $1 AND \"ConsumedAt\" IS NULL", ct, otp.Id);
        if (consumed == 0) return (null, new(410, "otp_used", "Bu kod artık geçerli değil; yeni kod isteyin.", "This code is no longer valid; request a new one."));

        var signedAt = DateTime.UtcNow;
        signedAt = signedAt.AddTicks(-(signedAt.Ticks % TimeSpan.TicksPerMillisecond));
        var method = Signatures.MethodFor(otp.Channel);
        var ip = Signatures.TruncateIp(q.IpRaw);
        var disclaimer = Signatures.DisclaimerTr;
        var evidenceHash = Signatures.Sha256Hex(Signatures.Canonical(q.DocumentType, q.DocumentId, q.Version, q.DocumentSha256, q.EmployeeId, signedAt, method, ip, disclaimer));
        var sigId = Guid.NewGuid();
        var title = string.IsNullOrWhiteSpace(q.Title) ? null : q.Title.Trim()[..Math.Min(q.Title.Trim().Length, 300)];
        try
        {
            await _sql.ExecuteAsync("""
                INSERT INTO governance_signatures ("Id","TenantSlug","DocumentType","DocumentId","DocumentVersion","DocumentSha256","SignerEmployeeId","SignedAt","Method","IpPrefix","Disclaimer","EvidenceSha256","Title","SignerKind")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
                """, ct, sigId, q.Tenant, q.DocumentType, q.DocumentId, q.Version, q.DocumentSha256, q.EmployeeId, signedAt, method, ip, disclaimer, evidenceHash, title,
                q.SignerKind);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (null, AlreadySigned());
        }
        await _sql.ExecuteAsync("""
            INSERT INTO audit_log ("TenantSlug","Service","EntityType","EntityId","Action","Changes","UserId","UserName","IpAddress","OccurredAt")
            VALUES ($1,'governance-service',$2,$3,'Signed',$4::jsonb,$5,$6,$7,now())
            """, ct, q.Tenant, q.DocumentType, q.DocumentId.ToString(),
            JsonSerializer.Serialize(new { documentType = q.DocumentType, method, documentSha256 = q.DocumentSha256, evidence = evidenceHash, signerKind = q.SignerKind }),
            q.UserId, q.UserName, ip);
        // Olayda kişisel veri yok: kimlikler + belge türü. Başlık yalnızca şablon adıysa (DocumentRequest) eklenir;
        // özlük dokümanının dosya adı kişisel veri içerebilir.
        InternalEvents.Raise(_sp, q.Tenant, "document.signed", new
        {
            TenantSlug = q.Tenant, DocumentId = q.DocumentId, DocumentType = q.DocumentType,
            TemplateName = q.DocumentType == Signatures.DocumentRequest ? title : null,
            // Aday imzasında çalışan kimliği yoktur (olay tüketicileri çalışan araması yapmasın).
            EmployeeId = q.SignerKind == Signatures.SignerEmployee ? q.EmployeeId : (Guid?)null, SignerKind = q.SignerKind, SignedAt = signedAt, Method = method, DocumentSha256 = q.DocumentSha256,
        });
        return (new SignatureEvidence(sigId, q.DocumentType, q.DocumentId, q.Version, q.DocumentSha256, q.EmployeeId, signedAt, method, ip, disclaimer, evidenceHash, title), null);
    }

    private static SignatureError Locked() =>
        new(429, "otp_locked", "Çok fazla hatalı deneme; yeni kod isteyin.", "Too many wrong attempts; request a new code.");

    /* ------------------------------------------------------------------ görünüm */

    /// <summary>Kanıtın API görünümü (web ve iç uçlar aynı alanları döner).</summary>
    public static object View(SignatureEvidence e, bool en) => new
    {
        e.Id, e.DocumentType, e.DocumentId, e.DocumentVersion, e.DocumentSha256, e.SignerEmployeeId, e.SignedAt, e.Method, e.IpPrefix,
        e.Title, disclaimer = en ? Signatures.DisclaimerEn : e.Disclaimer, e.EvidenceSha256, e.IntegrityOk, kind = "simple-electronic-signature",
    };
}
