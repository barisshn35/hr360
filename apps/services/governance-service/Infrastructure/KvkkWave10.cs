using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace GovernanceService.Infrastructure;

/* ======================================================================
 * Dalga 10 — KVKK:
 *   53) VERBİS envanteri (bakımı yapılan, kiracıya özel kayıt; dışa aktarım)
 *   54) İlgili kişi başvurusu: 20./27. gün hatırlatması, süre aşımı uyarısı,
 *       erişim başvurusunda otomatik veri paketi (ZIP)
 *   55) Metin yeni sürümünde yeniden onay kampanyası
 * ==================================================================== */

/// <summary>
/// Bir çalışanın tüm modüllerdeki kişisel verisi (KVKK m.11 dökümü). Dışa aktarım ucu ve veri paketi ortak kullanır.
/// Yalnızca OKUMA sorguları; her sorgu kiracıya ($1) ve kişiye ($2 çalışan kimliği, $3 kullanıcı kimliği,
/// $4 normalleştirilmiş e-posta) göre süzülür. Bölüm listesi <see cref="Sections"/>'ta; her bölüm Türkçe/İngilizce
/// başlıkla ve kategori bilgisiyle pakete "_icerik" altında yazılır.
/// </summary>
public static class PersonalDataExport
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const int MaxRowsPerSection = 5000;
    public const int NotificationDays = 90;

    /// <summary>
    /// Pakete giren bölüm. <paramref name="Query"/> LIMIT içermez (eklenir). <paramref name="Compensation"/>: ücret verisi
    /// (yalnızca kişinin kendisi / İK). <paramref name="Special"/>: özel nitelikli kişisel veri (KVKK m.6).
    /// </summary>
    public sealed record Section(string Key, string Tr, string En, string Category, string Query,
        bool Compensation = false, bool Special = false, string? NoteTr = null, string? NoteEn = null);

    /// <summary>Kişi hakkında 360 sonucu (kişiye gösterildiği biçimde, toplulaştırılmış) — tablo sorgusu değil, ayrı hesaplanır.</summary>
    public const string Feedback360Key = "degerlendirme360";
    /// <summary>Erişim kayıtları: pakette erisim-kayitlari.json; dışa aktarım ucunda bu anahtarla.</summary>
    public const string AccessLogKey = "erisimKayitlari";

    public static readonly Section[] Sections =
    {
        // --- Kimlik ve iş ilişkisi
        new("calisan", "Çalışan kaydı", "Employee record", "Kimlik / İletişim",
            """SELECT * FROM employee_employees WHERE "TenantSlug" = $1 AND "Id" = $2"""),
        new("gorevlendirmeler", "Görevlendirmeler", "Assignments", "Özlük",
            """SELECT * FROM employee_assignments WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "EffectiveFrom" """),
        new("profil", "Profil", "Profile", "Kimlik / İletişim / Finans",
            """SELECT * FROM engagement_profiles WHERE "TenantSlug" = $1 AND "EmployeeId" = $2"""),
        new("ozelAlanlar", "Özel alan değerleri", "Custom field values", "Özlük",
            """
            SELECT f."Key", f."Label", f."Type", f."IsSpecialCategory", f."Purpose", f."LegalBasis", f."RetentionMonths", v."Value", v."UpdatedAt"
            FROM governance_custom_field_values v JOIN governance_custom_fields f ON f."Id" = v."FieldId" AND f."TenantSlug" = v."TenantSlug"
            WHERE v."TenantSlug" = $1 AND v."EmployeeId" = $2 ORDER BY f."SortOrder", f."Label"
            """),
        new("ozlukBelgeleri", "Özlük belgeleri (yalnızca bilgi)", "Personnel documents (metadata only)", "Özlük",
            """SELECT "Type","FileName","SizeBytes","ContentType","UploadedAt","SignedAt" FROM expense_documents WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "UploadedAt" """,
            NoteTr: "Belgelerin kendisi pakete eklenmez; Profilim › Belgelerim ekranından indirilebilir.",
            NoteEn: "The files themselves are not included; download them under My profile › My documents."),
        // --- İzin ve zaman
        new("izinTalepleri", "İzin talepleri", "Leave requests", "Özlük",
            """SELECT * FROM leave_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "StartDate" """),
        new("izinBakiyeleri", "İzin bakiyeleri", "Leave balances", "Özlük",
            """SELECT * FROM leave_balances WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "Year" """),
        new("puantaj", "Puantaj", "Time entries", "Özlük",
            """SELECT * FROM timeshift_time_entries WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "Date" """),
        new("kartBasmalar", "Giriş-çıkış kayıtları", "Clock punches", "Özlük / Konum",
            """
            SELECT p."At", p."Kind", p."Method", p."OnSite", p."DistanceBucket", s."Name" AS "Site",
                   p."RawLatitude", p."RawLongitude", p."RawExpiresAt"
            FROM timeshift_clock_punches p LEFT JOIN timeshift_clock_sites s ON s."Id" = p."SiteId" AND s."TenantSlug" = p."TenantSlug"
            WHERE p."TenantSlug" = $1 AND p."EmployeeId" = $2 ORDER BY p."At"
            """,
            NoteTr: "Ham konum yalnızca saklama süresi dolmamışsa kayıtlıdır; aksi hâlde yalnızca mesafe aralığı tutulur.",
            NoteEn: "Raw coordinates are present only while their retention period lasts; otherwise only a distance band is kept."),
        new("fazlaMesaiTalepleri", "Fazla mesai talepleri", "Overtime requests", "Özlük",
            """SELECT * FROM timeshift_overtime_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "Date" """),
        // --- Masraf, seyahat, belge talepleri
        new("masraflar", "Masraf talepleri", "Expense claims", "Finans",
            """SELECT * FROM expense_claims WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" """),
        new("masrafKalemleri", "Masraf kalemleri", "Expense items", "Finans",
            """
            SELECT i."ClaimId", i."Category", i."Amount", i."ExpenseDate", i."Description", i."OriginalCurrency", i."OriginalAmount", i."FxRate", i."Km",
                   i."SupplierTaxId", i."InvoiceNo", i."VatAmount", i."ReceiptStorageKey" IS NOT NULL AS "HasReceipt"
            FROM expense_items i JOIN expense_claims c ON c."Id" = i."ClaimId" AND c."TenantSlug" = i."TenantSlug"
            WHERE i."TenantSlug" = $1 AND c."EmployeeId" = $2 ORDER BY i."ExpenseDate"
            """),
        new("seyahatTalepleri", "Seyahat talepleri", "Travel requests", "Finans / Seyahat",
            """
            SELECT "Id","Destination","Abroad","StartDate","EndDate","Purpose","Transport","NeedsAccommodation","PerDiemDays","PerDiemRate","PerDiemCurrency",
                   "PerDiemTotal","AdvanceRequested","PassportCipher" IS NOT NULL AS "PassportOnFile","PassportPurgedAt","Status","CreatedAt"
            FROM expense_travel_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "StartDate"
            """,
            NoteTr: "Pasaport numarası şifreli saklanır; pakete yalnızca kayıtlı olup olmadığı yazılır.",
            NoteEn: "The passport number is stored encrypted; the package only states whether one is on file."),
        new("belgeTalepleri", "Belge talepleri", "Document requests", "Özlük",
            """
            SELECT "TemplateName","Purpose","Status","DecisionNote","DecidedBy","VerificationCode","CreatedAt","IssuedAt"
            FROM governance_document_requests WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt"
            """),
        new("ikTalepleri", "İK talepleri", "HR cases", "Özlük",
            """SELECT "Subject","Description","Category","Priority","Status","Resolution","CreatedAt","ResolvedAt" FROM expense_hr_cases WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" """),
        // --- Ücret ve bordro (yalnızca kişinin kendisi / İK)
        new("ucretGecmisi", "Ücret geçmişi", "Compensation history", "Finans",
            """SELECT * FROM compensation_records WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "EffectiveFrom" """, Compensation: true),
        new("bordrolar", "Bordrolar", "Payslips", "Finans",
            """SELECT * FROM compensation_payslips WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "Year", "Month" """, Compensation: true),
        new("avanslar", "Avans ve borçlar", "Advances and loans", "Finans",
            """SELECT * FROM compensation_advances WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" """, Compensation: true),
        new("yanHaklar", "Yan hak seçimleri", "Benefit elections", "Finans",
            """SELECT * FROM compensation_benefit_elections WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "UpdatedAt" """, Compensation: true),
        new("sgkBilgileri", "SGK bilgileri", "Social security details", "Özlük",
            """SELECT * FROM compensation_employee_sgk WHERE "TenantSlug" = $1 AND "EmployeeId" = $2""", Compensation: true),
        // --- Performans
        new("performansHedefleri", "Hedefler ve OKR", "Goals and OKRs", "Performans",
            """
            SELECT g.*, o."Title" AS "ParentObjectiveTitle"
            FROM performance_goals g LEFT JOIN performance_objectives o ON o."Id" = g."ParentObjectiveId" AND o."TenantSlug" = g."TenantSlug"
            WHERE g."TenantSlug" = $1 AND g."EmployeeId" = $2 ORDER BY g."CreatedAt"
            """),
        new("performansDegerlendirmeleri", "Performans değerlendirmeleri", "Performance reviews", "Performans",
            """SELECT * FROM performance_reviews WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" """),
        new("geriBildirimler", "Hakkınızdaki geri bildirimler", "Feedback about you", "Performans",
            """
            SELECT "CycleId","Reason","ReasonDetail","Sentiment","Body","VisibleToEmployee","CreatedAt","ReadAt"
            FROM performance_feedback WHERE "TenantSlug" = $1 AND "ToEmployeeId" = $2 ORDER BY "CreatedAt"
            """,
            NoteTr: "Geri bildirimi verenin kimliği üçüncü kişinin verisi olduğundan pakete yazılmaz.",
            NoteEn: "The author's identity is third-party data and is not included."),
        new("birebirGorusmeler", "Birebir görüşmeler", "One-on-one meetings", "Performans",
            """
            SELECT "ManagerName","ScheduledAt","Status","Agenda","SharedNotes","ActionItems","Mood","CreatedAt"
            FROM engagement_one_on_ones WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "ScheduledAt"
            """,
            NoteTr: "Yöneticinin kişisel çalışma notları pakete eklenmez; ortak notlar eklenir.",
            NoteEn: "The manager's private working notes are not included; shared notes are."),
        // --- Eğitim
        new("egitimKayitlari", "Eğitim kayıtları", "Course enrollments", "Eğitim",
            """
            SELECT e."CourseId", c."Title" AS "Course", e."Status", e."Score", e."EnrolledAt", e."CompletedAt", e."DueOn"
            FROM learning_enrollments e LEFT JOIN learning_courses c ON c."Id" = e."CourseId" AND c."TenantSlug" = e."TenantSlug"
            WHERE e."TenantSlug" = $1 AND e."EmployeeId" = $2 ORDER BY e."EnrolledAt"
            """),
        new("sertifikalar", "Sertifikalar", "Certifications", "Eğitim",
            """SELECT * FROM learning_certifications WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "IssuedOn" """),
        // --- İşe alım kaynağı
        new("iseAlimKaydi", "İşe alım kaydı (aday başvurusu)", "Recruitment record (candidate application)", "Kimlik / İletişim / Mesleki deneyim",
            """
            SELECT c."FirstName", c."LastName", c."Email", c."Phone", c."Source", c."Skills", c."CreatedAt" AS "CandidateCreatedAt",
                   c."TalentPoolConsent", c."TalentPoolConsentAt", c."ResumeStorageKey" IS NOT NULL AS "HasResume",
                   j."Title" AS "JobTitle", a."Status", a."Channel", a."AppliedAt", a."StatusChangedAt", a."CoverNote", a."PrivacyNoticeVersion"
            FROM recruitment_candidates c
            JOIN recruitment_applications a ON a."CandidateId" = c."Id" AND a."TenantSlug" = c."TenantSlug"
            LEFT JOIN recruitment_job_postings j ON j."Id" = a."JobPostingId" AND j."TenantSlug" = a."TenantSlug"
            WHERE c."TenantSlug" = $1 AND $4 <> '' AND c."NormalizedEmail" = $4 AND c."AnonymizedAt" IS NULL
              AND EXISTS (SELECT 1 FROM recruitment_applications h WHERE h."TenantSlug" = c."TenantSlug" AND h."CandidateId" = c."Id" AND h."Status" = 'Hired')
            ORDER BY a."AppliedAt"
            """,
            NoteTr: "İşe alındığınız aday kaydı e-posta adresinizle eşleştirilir. Mülakat değerlendirmeleri ve İK iç notları eklenmez.",
            NoteEn: "Matched to the candidate record you were hired from by e-mail. Interview scorecards and internal HR notes are not included."),
        // --- İşe başlama / ayrılma / zimmet
        new("onboarding", "İşe başlama planı", "Onboarding plan", "Özlük",
            """SELECT * FROM onboarding_plans WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" """),
        new("onboardingGorevleri", "İşe başlama görevleri", "Onboarding tasks", "Özlük",
            """
            SELECT t."Title", t."Category", t."DueDate", t."Status", t."CompletedAt", t."OwnerRole"
            FROM onboarding_tasks t JOIN onboarding_plans p ON p."Id" = t."PlanId" AND p."TenantSlug" = t."TenantSlug"
            WHERE t."TenantSlug" = $1 AND p."EmployeeId" = $2 ORDER BY t."Order"
            """),
        new("istenAyrilis", "İşten ayrılış süreci", "Offboarding", "Özlük",
            """
            SELECT "LastWorkingDay","Reason","Status","Checklist","ExitInterview","RehireEligible","AssetChecks","AccountStatus","AccountDisabledAt",
                   "RetentionMonths","PlannedAnonymizationOn","CreatedAt","CompletedAt"
            FROM engagement_offboarding_cases WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt"
            """),
        new("zimmetler", "Zimmetler", "Assigned assets", "Özlük",
            """
            SELECT a."AssetTag", a."Type", a."Model", a."SerialNumber", z."AssignedOn", z."ExpectedReturnOn", z."ReturnedOn", z."ConditionOnReturn", z."Notes"
            FROM onboarding_asset_assignments z LEFT JOIN onboarding_assets a ON a."Id" = z."AssetId" AND a."TenantSlug" = z."TenantSlug"
            WHERE z."TenantSlug" = $1 AND z."EmployeeId" = $2 ORDER BY z."AssignedOn"
            """),
        // --- Sağlık (özel nitelikli)
        new("saglikMuayeneleri", "İşyeri hekimi muayeneleri", "Occupational health examinations", "Sağlık (özel nitelikli)",
            """
            SELECT "ExamType","ExamDate","NextDueDate","Result","NotesEnc" IS NOT NULL AS "HasPhysicianNotes","RecordedBy","CreatedAt","UpdatedAt"
            FROM governance_osh_exams WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "ExamDate"
            """, Special: true,
            NoteTr: "Sağlık verisi özel nitelikli kişisel veridir (KVKK m.6); İSG mevzuatı (6331 s. Kanun) gereği işlenir. İşyeri hekiminin şifreli notları pakete açılmaz; hekimden talep edebilirsiniz.",
            NoteEn: "Health data is a special category of personal data (KVKK art. 6), processed under occupational safety law (Law 6331). The physician's encrypted notes are not opened in the package; you may request them from the physician."),
        // --- Etkileşim, sohbet, bildirim
        new("aldigiTakdirler", "Aldığınız takdirler", "Recognition received", "Etkileşim",
            """SELECT "FromName","Badge","Message","CreatedAt" FROM engagement_kudos WHERE "TenantSlug" = $1 AND "ToEmployeeId" = $2 ORDER BY "CreatedAt" """),
        new("sohbetHesaplari", "Bağlı sohbet hesapları", "Linked chat accounts", "İletişim",
            """
            SELECT "Platform","Email","DisplayName","LinkedAt","VerifiedAt","LastSeenAt"
            FROM governance_chat_identities WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "LinkedAt"
            """),
        new("asistanBaglami", "Asistan konuşma bağlamı", "Assistant conversation context", "İletişim",
            """SELECT "Role","TextEnc","CreatedAt" FROM governance_chat_context WHERE "TenantSlug" = $1 AND "EmployeeId" = $2 ORDER BY "CreatedAt" """,
            NoteTr: "Asistan, yanıt kalitesi için son mesajlarınızı kısa süreli ve şifreli tutar.",
            NoteEn: "The assistant keeps your recent messages briefly and encrypted to keep answers in context."),
        new("asistanKullanimi", "Yapay zekâ kullanım kayıtları", "AI usage records", "İşlem güvenliği",
            """
            SELECT "Task","Provider","Model","InputTokens","OutputTokens","Success","At"
            FROM governance_ai_usage WHERE "TenantSlug" = $1 AND $3 <> '' AND "UserId" = $3 ORDER BY "At" DESC
            """,
            NoteTr: "Yalnızca kullanım bilgisi (görev, model, süre); istem metni saklanmaz.",
            NoteEn: "Usage metadata only (task, model, tokens); prompt text is not stored."),
        new("bildirimler", "Bildirimler (son 90 gün)", "Notifications (last 90 days)", "İletişim",
            """
            SELECT "Channel","TemplateCode","Subject","Body","Status","Language","CreatedAt","SentAt","ReadAt"
            FROM notification_messages WHERE "TenantSlug" = $1 AND "RecipientEmployeeId" = $2 AND "CreatedAt" >= now() - interval '90 days'
            ORDER BY "CreatedAt" DESC
            """),
        // --- KVKK
        new("onaylar", "Aydınlatma ve rıza geçmişi", "Notice and consent history", "Hukuki işlem",
            """
            SELECT "ConsentType","Version","Granted","RecordedAt" FROM governance_consents
            WHERE "TenantSlug" = $1 AND ("EmployeeId" = $2 OR ($3 <> '' AND "UserId" = $3)) ORDER BY "RecordedAt"
            """),
        new("kvkkBasvurulari", "KVKK başvurularınız", "Your data subject requests", "Hukuki işlem",
            """
            SELECT "Kind","Details","Status","Response","Channel","CreatedAt","DueAt","CompletedAt"
            FROM governance_data_requests WHERE "TenantSlug" = $1 AND ("EmployeeId" = $2 OR ($3 <> '' AND "UserId" = $3)) ORDER BY "CreatedAt"
            """),
    };

    /// <summary>Pakette tablo sorgusu dışında üretilen bölümler (başlık ve kategori bilgisiyle).</summary>
    public static readonly Section[] DerivedSections =
    {
        new(Feedback360Key, "360 derece geri bildirim sonuçları", "360-degree feedback results", "Performans", "",
            NoteTr: "Size gösterildiği biçimde toplulaştırılmıştır: yalnızca kapanmış ve sizinle paylaşılmış talepler; 5'ten az yanıt varsa sonuç yoktur. Değerlendiren kimliği saklanmaz.",
            NoteEn: "Aggregated exactly as shown to you: only closed requests shared with you; no results below 5 responses. Reviewer identities are not stored."),
        new(AccessLogKey, "Verinize erişim kayıtları (son 2 yıl)", "Access log about you (last 2 years)", "İşlem güvenliği", "",
            NoteTr: "Hassas verilerinizin kim tarafından açıldığı, dışa aktarıldığı ya da otomatik analizde kullanıldığı.",
            NoteEn: "Who opened, exported or used your sensitive data in automated analysis."),
    };

    /// <summary>Dökümde açılmaz: kimlik doğrulama/şifreleme ayrıntıları (kişisel veri değil, güvenlik verisi).</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "TenantSlug", "SelfServiceTokenHash", "PinHash", "CardHash", "LinkCodeHash", "OtpHash", "QrSecret", "DeviceKeyHash",
        "PassportCipher", "DocumentEnc", "NotesEnc",
    };

    /// <summary>Gizli sütun mu (birim testi için).</summary>
    public static bool IsHidden(string column) => Hidden.Contains(column);

    /// <summary>Normalleştirilmiş e-posta (recruitment-service DuplicateDetector.NormalizeEmail ile aynı).</summary>
    public static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var e = email.Trim().ToLowerInvariant();
        var at = e.LastIndexOf('@');
        if (at <= 0) return e;
        var local = e[..at];
        var plus = local.IndexOf('+');
        if (plus > 0) local = local[..plus];
        return local + e[at..];
    }

    public static async Task<List<Dictionary<string, object?>>> RowsAsync(Sql sql, string query, CancellationToken ct, params object?[] args)
    {
        try
        {
            return await sql.QueryAsync(query.TrimEnd() + " LIMIT " + MaxRowsPerSection, r =>
            {
                var d = new Dictionary<string, object?>();
                for (var i = 0; i < r.FieldCount; i++)
                {
                    var name = r.GetName(i);
                    if (Hidden.Contains(name)) continue;
                    d[name] = r.IsDBNull(i) ? null : r.GetValue(i) switch { DateOnly x => x.ToString("yyyy-MM-dd"), var v => v };
                }
                return d;
            }, ct, args);
        }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42703")
        {
            return new(); // modülün tablosu/sütunu bu kurulumda yok
        }
    }

    /// <summary>engagement-service'in şifrelediği TCKN/IBAN'ı döküm için açar.</summary>
    public static Dictionary<string, object?> OpenPii(Dictionary<string, object?> row)
    {
        foreach (var k in new[] { "Iban", "NationalId" })
            if (row.TryGetValue(k, out var v) && v is string sv && Security.KeyRing.IsSealed(sv))
                row[k] = SecretBox.Unprotect(sv);
        return row;
    }

    /// <summary>Bölüme özgü dönüşüm: şifreli alanlar (kişinin kendi verisi) açılır; açılamayan değer yazılmaz.</summary>
    public static Dictionary<string, object?> Transform(string section, Dictionary<string, object?> row)
    {
        switch (section)
        {
            case "profil":
                return OpenPii(row);
            case "ozelAlanlar":
                if (row.TryGetValue("Value", out var v) && v is string sv) row["Value"] = SafeOpen(() => CustomFields.Open(sv));
                return row;
            case "asistanBaglami":
                row["Text"] = row.TryGetValue("TextEnc", out var t) && t is string st ? SafeOpen(() => SecretBox.Unprotect(st)) : null;
                row.Remove("TextEnc");
                return row;
            default:
                return row;
        }
    }

    private static string? SafeOpen(Func<string?> open)
    {
        try { return open(); }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Kişiye gösterilen 360 sonucunun aynısı (performance-service F360Rules.Aggregate ile aynı kural).</summary>
    public sealed record F360Competency(string Key, string Label);

    public const int F360MinGroup = 5;

    public static object Aggregate360(string title, DateTime? closedAt, IReadOnlyList<F360Competency> competencies,
        IReadOnlyList<(IReadOnlyDictionary<string, int> Ratings, string? Comment)> responses, int minResponses)
    {
        var threshold = Math.Max(F360MinGroup, minResponses);
        if (responses.Count < threshold)
            return new { title, closedAt, hidden = true, responses = responses.Count, threshold, competencies = Array.Empty<object>(), comments = Array.Empty<string>() };
        var comps = competencies.Select(c =>
        {
            var vals = responses.Select(r => r.Ratings.TryGetValue(c.Key, out var x) ? x : 0).Where(x => x is >= 1 and <= 5).ToList();
            if (vals.Count < F360MinGroup) return (object)new { c.Key, c.Label, ratings = vals.Count, hidden = true };
            return new { c.Key, c.Label, ratings = vals.Count, average = Math.Round((decimal)vals.Average(), 2), hidden = false };
        }).ToList();
        var comments = responses.Select(r => r.Comment?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        return new { title, closedAt, hidden = false, responses = responses.Count, threshold, competencies = comps, comments };
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static async Task<List<object>> Feedback360Async(Sql sql, string tenant, Guid employeeId, CancellationToken ct)
    {
        var result = new List<object>();
        try
        {
            var reqs = await sql.QueryAsync("""
                SELECT "Id","Title","CompetenciesJson"::text,"MinResponses","ClosedAt" FROM performance_f360_requests
                WHERE "TenantSlug" = $1 AND "SubjectEmployeeId" = $2 AND "ReleasedToSubject" AND "Status" = 'Closed'
                ORDER BY "ClosedAt" LIMIT 200
                """, r => (Id: r.GetGuid(0), Title: r.GetString(1), Comp: r.GetString(2), Min: r.GetInt32(3), Closed: r.Ts(4)), ct, tenant, employeeId);
            foreach (var q in reqs)
            {
                var comps = JsonSerializer.Deserialize<List<F360Competency>>(q.Comp, WebJson) ?? new();
                var resp = await sql.QueryAsync("""
                    SELECT "RatingsJson"::text, "Comment" FROM performance_f360_responses WHERE "TenantSlug" = $1 AND "RequestId" = $2
                    """, r => ((IReadOnlyDictionary<string, int>)(JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(0), WebJson) ?? new()), r.Str(1)),
                    ct, tenant, q.Id);
                result.Add(Aggregate360(q.Title, q.Closed, comps, resp, q.Min));
            }
        }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42703") { }
        return result;
    }

    /// <summary>"_icerik": her bölümün başlığı (TR/EN), kategorisi, kayıt sayısı ve notu.</summary>
    public static List<object> Contents(Dictionary<string, object?> bundle) =>
        Sections.Concat(DerivedSections).Where(s => bundle.ContainsKey(s.Key)).Select(s => (object)new
        {
            bolum = s.Key, baslik = s.Tr, title = s.En, kategori = s.Category, ozelNitelikli = s.Special,
            kayit = bundle[s.Key] is System.Collections.ICollection c ? c.Count : 0,
            not = s.NoteTr, note = s.NoteEn,
        }).ToList();

    public static async Task<Dictionary<string, object?>> BuildAsync(Sql sql, string tenant, Person person, bool includeCompensation, CancellationToken ct)
    {
        var bundle = new Dictionary<string, object?>
        {
            ["hazirlanma"] = DateTime.UtcNow,
            ["aciklama"] = "KVKK m.11 kapsamında, HR360'ta sizinle ilişkili tutulan kişisel verilerin dökümüdür. Bölüm başlıkları ve açıklamaları \"_icerik\" altındadır.",
            ["description"] = "Export of the personal data HR360 holds about you (KVKK art. 11). Section titles and notes are listed under \"_icerik\".",
        };
        var userId = person.UserId ?? "";
        var email = NormalizeEmail(person.Email);
        foreach (var s in Sections)
        {
            if (s.Compensation && !includeCompensation) continue;
            var rows = await RowsAsync(sql, s.Query, ct, tenant, person.Id, userId, email);
            bundle[s.Key] = rows.Select(r => Transform(s.Key, r)).ToList();
        }
        bundle[Feedback360Key] = await Feedback360Async(sql, tenant, person.Id, ct);
        bundle["_icerik"] = Contents(bundle);
        return bundle;
    }

    /// <summary>Kişinin hassas verisine erişim kayıtları (açma, dışa aktarma, otomatik analiz) — şeffaflık için pakete eklenir.</summary>
    public static Task<List<object>> AccessLogAsync(Sql sql, string tenant, Guid employeeId, CancellationToken ct) =>
        sql.QueryAsync<object>("""
            SELECT "OccurredAt","Service","EntityType","Action","UserName" FROM audit_log
            WHERE "TenantSlug" = $1 AND "EntityId" = $2
              AND "Action" IN ('Revealed','SensitiveViewed','Exported','AutomatedAnalysis','PlatformAccess')
              AND "OccurredAt" >= now() - interval '2 years'
            ORDER BY "OccurredAt" DESC LIMIT 2000
            """, r => new { at = r.GetFieldValue<DateTime>(0), service = r.GetString(1), entity = r.GetString(2), action = r.GetString(3), by = r.Str(4) ?? "—" },
            ct, tenant, employeeId.ToString());
}

/* ------------------------------------------------------------------ başvuru durum makinesi */

/// <summary>
/// İlgili kişi başvurusunun durum geçişleri. Açık: Received, InProgress. Kapalı (son): Completed, Rejected, Withdrawn.
/// Kapalı başvuru yeniden açılamaz/değiştirilemez (yanıt bildirimi tekrar gönderilmez); yeni talep için yeni başvuru yapılır.
/// </summary>
public static class DataRequestStates
{
    public static readonly string[] All = { "Received", "InProgress", "Completed", "Rejected", "Withdrawn" };

    public static bool IsOpen(string status) => status is "Received" or "InProgress";

    public static bool IsTerminal(string status) => status is "Completed" or "Rejected" or "Withdrawn";

    /// <summary>
    /// İzinli geçişler: Received → Received/InProgress/Completed/Rejected; InProgress → InProgress/Completed/Rejected
    /// (aynı açık durumda kalmak ara not kaydı içindir). Withdrawn yalnızca başvurucu tarafından, açık başvurudan.
    /// </summary>
    public static bool CanTransition(string from, string to, bool byApplicant = false) => (from, to) switch
    {
        ("Received" or "InProgress", "Withdrawn") => byApplicant,
        (_, _) when byApplicant => false,
        ("Received", "Received" or "InProgress" or "Completed" or "Rejected") => true,
        ("InProgress", "InProgress" or "Completed" or "Rejected") => true,
        _ => false,
    };

    /// <summary>Yanıt bildirimi yalnızca açık durumdan kapalı (Completed/Rejected) duruma geçişte gönderilir.</summary>
    public static bool NotifiesApplicant(string from, string to) => IsOpen(from) && to is "Completed" or "Rejected";

    /// <summary>
    /// Koşullu geçiş: kayıt hâlâ <paramref name="from"/> durumundaysa günceller (eşzamanlı iki işlemden yalnızca biri kazanır).
    /// Dönen değer güncellenen satır sayısıdır (0 = durum bu arada değişti).
    /// </summary>
    public static Task<int> TryMoveAsync(Sql sql, string tenant, Guid id, string from, string to, string? response, CancellationToken ct) =>
        sql.ExecuteAsync("""
            UPDATE governance_data_requests
            SET "Status" = $4, "Response" = COALESCE($5, "Response"),
                "CompletedAt" = CASE WHEN $4 IN ('Completed','Rejected','Withdrawn') THEN now() ELSE NULL END
            WHERE "TenantSlug" = $1 AND "Id" = $2 AND "Status" = $3
            """, ct, tenant, id, from, to, response);
}

/* ------------------------------------------------------------------ 54) başvuru süre takibi */

public static class DataRequestReminders
{
    public const int LegalDays = 30;

    /// <summary>
    /// Açık başvurunun bu turda gönderilecek uyarısı: "overdue" (süre geçti), "d27", "d20" ya da null.
    /// Her uyarı bir kez gönderilir; daha ileri bir aşamaya geçilmişse geridekiler atlanır.
    /// </summary>
    public static string? Due(DateTime createdAt, DateTime dueAt, DateTime now, bool sent20, bool sent27, bool sentOverdue)
    {
        if (now > dueAt) return sentOverdue ? null : "overdue";
        var days = (now - createdAt).TotalDays;
        if (days >= 27) return sent27 ? null : "d27";
        if (days >= 20) return sent20 ? null : "d20";
        return null;
    }

    /// <summary>Başvurunun kaçıncı gününde olduğu (1–30; süre geçince 30'dan büyük).</summary>
    public static int DayOf(DateTime createdAt, DateTime now) => Math.Max(1, (int)Math.Floor((now - createdAt).TotalDays) + 1);

    private static readonly Dictionary<string, string> KindTr = new()
    {
        ["Access"] = "bilgi/erişim", ["Rectification"] = "düzeltme", ["Erasure"] = "silme", ["Objection"] = "itiraz",
    };

    public static async Task<int> RunAsync(Sql sql, CancellationToken ct)
    {
        List<(Guid Id, string Tenant, string Kind, DateTime Created, DateTime Due, bool S20, bool S27, bool SO)> open;
        try
        {
            open = await sql.QueryAsync("""
                SELECT "Id","TenantSlug","Kind","CreatedAt","DueAt","Reminder20At" IS NOT NULL,"Reminder27At" IS NOT NULL,"OverdueAlertAt" IS NOT NULL
                FROM governance_data_requests WHERE "Status" IN ('Received','InProgress')
                """, r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTime>(3), r.GetFieldValue<DateTime>(4),
                    r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7)), ct);
        }
        catch (PostgresException e) when (e.SqlState == "42703") { return 0; } // göç uygulanmadı
        var sent = 0;
        var now = DateTime.UtcNow;
        foreach (var r in open)
        {
            var stage = Due(r.Created, r.Due, now, r.S20, r.S27, r.SO);
            if (stage is null) continue;
            var column = stage switch { "overdue" => "OverdueAlertAt", "d27" => "Reminder27At", _ => "Reminder20At" };
            // Önce işaretlenir (çift gönderimi önler); alıcı yoksa da işaret kalır, uyum ekranı süreyi gösterir.
            var claimed = await sql.ExecuteAsync($"UPDATE governance_data_requests SET \"{column}\" = now() WHERE \"Id\" = $1 AND \"{column}\" IS NULL", ct, r.Id);
            if (claimed == 0) continue;
            var due = TrTime.ToTr(r.Due).ToString("dd.MM.yyyy");
            var kind = KindTr.GetValueOrDefault(r.Kind, r.Kind);
            var (subTr, subEn, bodyTr, bodyEn) = stage switch
            {
                "overdue" => ("KVKK başvurusunun yasal süresi geçti", "A data subject request is overdue",
                    $"Bir {kind} başvurusu 30 günlük yasal süreyi (KVKK m.13) aştı; son gün {due} idi. Gecikmeden yanıtlayın: KVKK › Başvurular.",
                    $"A {r.Kind} request exceeded the 30-day legal deadline (KVKK art. 13); the last day was {due}. Respond without delay: KVKK › Requests."),
                "d27" => ("KVKK başvurusu: son 3 gün", "Data subject request: 3 days left",
                    $"Bir {kind} başvurusu 27. gününde; en geç {due} tarihinde yanıtlanmalı (KVKK m.13). KVKK › Başvurular.",
                    $"A {r.Kind} request is on day 27; it must be answered by {due} (KVKK art. 13). KVKK › Requests."),
                _ => ("KVKK başvurusu: 10 gün kaldı", "Data subject request: 10 days left",
                    $"Bir {kind} başvurusu 20. gününde; en geç {due} tarihinde yanıtlanmalı (KVKK m.13). KVKK › Başvurular.",
                    $"A {r.Kind} request is on day 20; it must be answered by {due} (KVKK art. 13). KVKK › Requests."),
            };
            var settings = await SecuritySettingsStore.LoadAsync(sql, r.Tenant, ct);
            var to = await SecuritySettingsStore.RecipientsAsync(sql, r.Tenant, settings, ct);
            await BulkNotifyLocalized.InAppAsync(sql, r.Tenant, to, subTr, subEn, bodyTr, bodyEn, "privacy.request." + stage, ct);
            await ComplianceAudit.WriteAsync(sql, r.Tenant, "DataRequest", r.Id.ToString(), stage == "overdue" ? "OverdueAlert" : "DeadlineReminder",
                new { stage, recipients = to.Count, dueAt = r.Due }, "system", "Sistem (KVKK süre takibi)", ct);
            sent++;
        }
        return sent;
    }
}

/* ------------------------------------------------------------------ 54) erişim başvurusu veri paketi */

public static class DataRequestPackages
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int KeepDays = 60;

    public sealed record Package(Guid Id, string FileName, string Sha256, int SizeBytes, Dictionary<string, int> Sections);

    /// <summary>ZIP içeriği: kisisel-veri.json, erisim-kayitlari.json, basvuru.txt, BENIOKU.txt, manifest.json (SHA-256).</summary>
    public static byte[] BuildZip(Dictionary<string, object?> bundle, List<object> accessLog, string requestSummary, DateTime now)
    {
        var files = new List<(string Name, byte[] Data)>
        {
            ("kisisel-veri.json", JsonSerializer.SerializeToUtf8Bytes(bundle, PersonalDataExport.Json)),
            ("erisim-kayitlari.json", JsonSerializer.SerializeToUtf8Bytes(accessLog, PersonalDataExport.Json)),
            ("basvuru.txt", Encoding.UTF8.GetBytes(requestSummary)),
            ("BENIOKU.txt", Encoding.UTF8.GetBytes(Readme)),
        };
        var manifest = files.Select(f => new { file = f.Name, bytes = f.Data.Length, sha256 = Convert.ToHexString(SHA256.HashData(f.Data)).ToLowerInvariant() }).ToList();
        files.Add(("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new { createdAt = now, files = manifest }, PersonalDataExport.Json)));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(now, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    public const string Readme = """
        HR360 — KİŞİSEL VERİ PAKETİ (KVKK m.11)

        Bu paket, bilgi/erişim başvurunuz üzerine HR360'ta sizinle ilişkili tutulan kişisel verilerden hazırlanmıştır.

        kisisel-veri.json      Modül başına bölümler: çalışan kaydı, görevlendirmeler, profil, özel alanlar, izin talep ve
                               bakiyeleri, puantaj, giriş-çıkış kayıtları, fazla mesai, masraf ve seyahat talepleri, belge
                               talepleri, ücret geçmişi ve bordrolar, performans (hedef/OKR, değerlendirme, geri bildirim,
                               toplulaştırılmış 360 sonuçları), eğitim ve sertifikalar, işe alım kaydı, işe başlama/ayrılış,
                               zimmetler, işyeri hekimi muayeneleri, sohbet/asistan kayıtları, son 90 günün bildirimleri,
                               rıza geçmişi ve KVKK başvurularınız. Her bölümün başlığı ve açıklaması "_icerik" altındadır.
                               Sağlık verisi özel nitelikli kişisel veridir (KVKK m.6); "_icerik" içinde ayrıca işaretlidir.
        erisim-kayitlari.json  Hassas verilerinize son 2 yılda kimin, ne zaman eriştiği (açma, dışa aktarma, otomatik analiz).
        basvuru.txt            Başvurunuzun özeti ve yasal süre.
        manifest.json          Dosyaların SHA-256 özetleri (bütünlük kontrolü).

        Verilerin işlenme amaçları, hukuki sebepleri, aktarıldığı alıcı grupları ve saklama süreleri şirketin aydınlatma
        metninde ve kişisel veri envanterinde yer alır. Düzeltme, silme ya da itiraz için HR360 › Profilim › Gizlilik
        ekranından yeni başvuru yapabilirsiniz. Başvurunuza verilen yanıtı yetersiz bulursanız tebliğden itibaren 30 gün
        içinde Kişisel Verileri Koruma Kurulu'na şikâyette bulunabilirsiniz (KVKK m.14).

        Bu dosyayı güvenli bir yerde saklayın; içinde kimlik, finans ve sağlık bilgileriniz bulunabilir.
        """;

    public static string Summary(string kind, string person, DateTime createdAt, DateTime dueAt, string channel) => string.Join('\n',
        "İLGİLİ KİŞİ BAŞVURUSU ÖZETİ",
        $"Başvuran: {person}",
        $"Tür: {kind}",
        $"Kanal: {channel}",
        $"Başvuru tarihi: {TrTime.ToTr(createdAt):dd.MM.yyyy HH:mm}",
        $"Yasal yanıt süresi sonu (KVKK m.13, 30 gün): {TrTime.ToTr(dueAt):dd.MM.yyyy}",
        $"Paket hazırlanma: {TrTime.ToTr(DateTime.UtcNow):dd.MM.yyyy HH:mm}");

    /// <summary>Bölüm → kayıt sayısı ("_" ile başlayan açıklama anahtarları sayılmaz).</summary>
    public static Dictionary<string, int> SectionCounts(Dictionary<string, object?> bundle) =>
        bundle.Where(kv => !kv.Key.StartsWith('_') && kv.Value is System.Collections.ICollection).ToDictionary(kv => kv.Key, kv => ((System.Collections.ICollection)kv.Value!).Count);

    /// <summary>Paketi üretir, şifreli saklar (önceki paket silinir) ve denetim kaydı yazar.</summary>
    public static async Task<Package?> CreateAsync(Sql sql, string tenant, Guid requestId, PeopleDirectory people, string actorId, string actorName, CancellationToken ct)
    {
        var req = (await sql.QueryAsync("""
            SELECT "EmployeeId","Kind","PersonName","CreatedAt","DueAt","Channel","IdentityVerified" FROM governance_data_requests
            WHERE "TenantSlug" = $1 AND "Id" = $2
            """, r => (Emp: r.GuidOrNull(0), Kind: r.GetString(1), Person: r.GetString(2), Created: r.GetFieldValue<DateTime>(3),
                Due: r.GetFieldValue<DateTime>(4), Channel: r.GetString(5), Verified: r.GetBoolean(6)), ct, tenant, requestId)).FirstOrDefault();
        if (req.Emp is not { } empId || !req.Verified) return null;
        var person = await people.FindAsync(tenant, empId, ct);
        if (person is null) return null;
        var bundle = await PersonalDataExport.BuildAsync(sql, tenant, person, includeCompensation: true, ct);
        var access = await PersonalDataExport.AccessLogAsync(sql, tenant, empId, ct);
        var kindTr = req.Kind switch { "Access" => "Bilgi/erişim talebi", "Rectification" => "Düzeltme", "Erasure" => "Silme", "Objection" => "İtiraz", var k => k };
        var zip = BuildZip(bundle, access, Summary(kindTr, person.Name, req.Created, req.Due, req.Channel), DateTime.UtcNow);
        if (zip.Length > MaxBytes) throw new InvalidOperationException("Veri paketi 15 MB sınırını aşıyor.");
        var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var id = Guid.NewGuid();
        var file = $"kisisel-veri-paketi-{DateTime.UtcNow:yyyyMMdd}.zip";
        var sections = SectionCounts(bundle);
        sections["erisimKayitlari"] = access.Count;
        await sql.ExecuteAsync("DELETE FROM governance_data_request_packages WHERE \"TenantSlug\" = $1 AND \"RequestId\" = $2", ct, tenant, requestId);
        await sql.ExecuteAsync("""
            INSERT INTO governance_data_request_packages ("Id","TenantSlug","RequestId","EmployeeId","FileName","ContentEnc","Sha256","SizeBytes","Sections","CreatedBy","CreatedAt","ExpiresAt")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10,now(),now() + make_interval(days => $11))
            """, ct, id, tenant, requestId, empId, file, SecretBox.Protect(Convert.ToBase64String(zip)), sha, zip.Length,
            JsonSerializer.Serialize(sections), actorName, KeepDays);
        await ComplianceAudit.WriteAsync(sql, tenant, "DataRequest", requestId.ToString(), "PackagePrepared",
            new { packageId = id, sha256 = sha, bytes = zip.Length, sections }, actorId, actorName, ct);
        return new Package(id, file, sha, zip.Length, sections);
    }

    public static Task<int> PurgeExpiredAsync(Sql sql, CancellationToken ct) =>
        sql.ExecuteAsync("DELETE FROM governance_data_request_packages WHERE \"ExpiresAt\" < now()", ct);
}

/* ------------------------------------------------------------------ 55) yeniden onay kampanyası */

public static class ConsentCampaigns
{
    public const int AutoReminderDays = 7;
    public const int MaxAutoReminders = 3;

    public sealed record LatestConsent(string UserId, string Version, bool Granted);

    /// <summary>
    /// Kampanyanın hedefi: aydınlatma (zorunlu bilgilendirme) için hesabı olan TÜM çalışanlar; açık rıza tipleri için
    /// eski sürüme ONAY vermiş olanlar (rızaları eski metne dayandığından yeniden sorulur). Reddetmiş ya da hiç
    /// yanıtlamamış kişi kampanya hedefi değildir (rıza zorlanmaz).
    /// </summary>
    public static bool IsTarget(bool required, LatestConsent? latest, string version) =>
        required || (latest is { Granted: true } && latest.Version != version);

    /// <summary>Kişi yeni sürüme yanıt verdi mi (zorunluda "okudum", rızada onay ya da ret).</summary>
    public static bool IsDone(bool required, LatestConsent? latest, string version) =>
        latest is not null && latest.Version == version && (!required || latest.Granted);

    public static async Task StartAsync(Sql sql, string tenant, string type, string version, string? previousVersion, string startedBy, CancellationToken ct)
    {
        try
        {
            await sql.ExecuteAsync("""
                UPDATE governance_consent_campaigns SET "Status" = 'Closed', "ClosedAt" = now(), "ClosedBy" = 'Sistem (yeni sürüm yayımlandı)'
                WHERE "TenantSlug" = $1 AND "ConsentType" = $2 AND "Status" = 'Open'
                """, ct, tenant, type);
            await sql.ExecuteAsync("""
                INSERT INTO governance_consent_campaigns ("Id","TenantSlug","ConsentType","Version","PreviousVersion","Status","StartedBy","StartedAt")
                VALUES ($1,$2,$3,$4,$5,'Open',$6,now()) ON CONFLICT ("TenantSlug","ConsentType","Version") DO NOTHING
                """, ct, Guid.NewGuid(), tenant, type, version, previousVersion, startedBy);
        }
        catch (PostgresException e) when (e.SqlState == "42P01") { } // göç uygulanmadı: kampanya yok, yayın sürer
    }

    public sealed record Campaign(Guid Id, string Type, string Version, string? PreviousVersion, string Status, string StartedBy, DateTime StartedAt,
        DateTime? ClosedAt, int ReminderCount, DateTime? LastReminderAt);

    public static Task<List<Campaign>> ListAsync(Sql sql, string tenant, CancellationToken ct, bool openOnly = false) =>
        sql.QueryAsync($"""
            SELECT "Id","ConsentType","Version","PreviousVersion","Status","StartedBy","StartedAt","ClosedAt","ReminderCount","LastReminderAt"
            FROM governance_consent_campaigns WHERE "TenantSlug" = $1 {(openOnly ? "AND \"Status\" = 'Open'" : "")} ORDER BY "StartedAt" DESC LIMIT 200
            """, r => new Campaign(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Str(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTime>(6),
                r.Ts(7), r.GetInt32(8), r.Ts(9)), ct, tenant);

    public static async Task<Dictionary<(string User, string Type), LatestConsent>> LatestAsync(Sql sql, string tenant, CancellationToken ct) =>
        (await sql.QueryAsync("""
            SELECT DISTINCT ON ("UserId","ConsentType") "UserId","ConsentType","Version","Granted"
            FROM governance_consents WHERE "TenantSlug" = $1 ORDER BY "UserId","ConsentType","RecordedAt" DESC
            """, r => (User: r.GetString(0), Type: r.GetString(1), C: new LatestConsent(r.GetString(0), r.GetString(2), r.GetBoolean(3))), ct, tenant))
        .ToDictionary(x => (x.User, x.Type), x => x.C);

    public sealed record Progress(int Target, int Done, List<Person> Pending);

    public static Progress Compute(bool required, string type, string version, IEnumerable<Person> people, IReadOnlyDictionary<(string, string), LatestConsent> latest)
    {
        var target = 0; var done = 0; var pending = new List<Person>();
        foreach (var p in people.Where(p => p.UserId is not null))
        {
            latest.TryGetValue((p.UserId!, type), out var l);
            if (IsDone(required, l, version)) { target++; done++; continue; }
            if (!IsTarget(required, l, version)) continue;
            target++;
            pending.Add(p);
        }
        return new Progress(target, done, pending);
    }

    /// <summary>Bekleyenlere uygulama içi hatırlatma (yalnızca metin başlığı; kişisel veri yok).</summary>
    public static async Task<int> RemindAsync(Sql sql, string tenant, Campaign c, string title, IEnumerable<Guid> pending, string actorId, string actorName, CancellationToken ct)
    {
        var ids = pending.ToList();
        var sent = await BulkNotifyLocalized.InAppAsync(sql, tenant, ids,
            "Güncellenen metni okuyup yanıtlayın", "Please review the updated notice",
            $"\"{title}\" metni güncellendi (sürüm {c.Version}). Profilim › Gizlilik (KVKK) ekranından okuyup yanıtlayın.",
            $"The notice \"{title}\" was updated (version {c.Version}). Review and respond under My profile › Privacy (KVKK).",
            "privacy.reconsent", ct);
        await sql.ExecuteAsync("""
            UPDATE governance_consent_campaigns SET "ReminderCount" = "ReminderCount" + 1, "LastReminderAt" = now() WHERE "Id" = $1
            """, ct, c.Id);
        await ComplianceAudit.WriteAsync(sql, tenant, "ConsentCampaign", c.Id.ToString(), "ReminderSent",
            new { c.Type, c.Version, recipients = sent }, actorId, actorName, ct);
        return sent;
    }

    /// <summary>Haftalık otomatik hatırlatma (en çok 3 kez); kampanya başladıktan 7 gün sonra başlar.</summary>
    public static async Task<int> AutoRemindAsync(Sql sql, CancellationToken ct)
    {
        List<(string Tenant, Guid Id)> due;
        try
        {
            due = await sql.QueryAsync("""
                SELECT "TenantSlug","Id" FROM governance_consent_campaigns
                WHERE "Status" = 'Open' AND "ReminderCount" < $1 AND "StartedAt" < now() - make_interval(days => $2)
                  AND ("LastReminderAt" IS NULL OR "LastReminderAt" < now() - make_interval(days => $2))
                """, r => (r.GetString(0), r.GetGuid(1)), ct, MaxAutoReminders, AutoReminderDays);
        }
        catch (PostgresException e) when (e.SqlState == "42P01") { return 0; }
        var total = 0;
        var people = new PeopleDirectory(sql);
        foreach (var tenantGroup in due.GroupBy(d => d.Tenant))
        {
            var tenant = tenantGroup.Key;
            var campaigns = await ListAsync(sql, tenant, ct, openOnly: true);
            var types = await EffectiveTypesAsync(sql, tenant, ct);
            var staff = await people.ListAsync(tenant, ct);
            var latest = await LatestAsync(sql, tenant, ct);
            foreach (var c in campaigns.Where(c => tenantGroup.Any(d => d.Id == c.Id)))
            {
                var t = types.FirstOrDefault(x => x.Type == c.Type);
                if (t is null || t.Version != c.Version) continue;
                var prog = Compute(t.Required, c.Type, c.Version, staff, latest);
                if (prog.Pending.Count == 0) continue;
                total += await RemindAsync(sql, tenant, c, t.Title, prog.Pending.Select(p => p.Id), "system", "Sistem (otomatik hatırlatma)", ct);
            }
        }
        return total;
    }

    /// <summary>Kiracının geçerli metinleri (EF olmadan; arka plan işi için). Yerleşik + kiracının yayımladığı son sürüm.</summary>
    public static async Task<List<Controllers.PrivacyController.ConsentType>> EffectiveTypesAsync(Sql sql, string tenant, CancellationToken ct)
    {
        var notices = await sql.QueryAsync("""
            SELECT DISTINCT ON ("Type") "Type","Title","Version","Text" FROM governance_privacy_notices
            WHERE "TenantSlug" = $1 ORDER BY "Type","PublishedAt" DESC
            """, r => (Type: r.GetString(0), Title: r.GetString(1), Version: r.GetString(2), Text: r.GetString(3)), ct, tenant);
        return Controllers.PrivacyController.ConsentTypes.Select(t =>
        {
            var n = notices.FirstOrDefault(x => x.Type == t.Type);
            return n.Type is null ? t : t with { Title = n.Title, Version = n.Version, Text = n.Text };
        }).ToList();
    }
}

/* ------------------------------------------------------------------ 53) VERBİS envanteri */

public static class VerbisInventory
{
    private static readonly System.Text.Encodings.Web.HtmlEncoder HtmlText =
        System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    public sealed record Item(Guid Id, string Key, string Source, string Module, string Activity, string[] Subjects, string[] DataCategories,
        string Purpose, string LegalBasis, bool Special, string Retention, string? RetentionCategory, string[] Recipients,
        string[] TransferProviders, string Measures, bool IsActive, DateTime UpdatedAt, string? UpdatedByName);

    /// <summary>Ürün kataloğundaki (ve özel alanlardan türeyen) faaliyetlerden eksik olanları ekler; düzenlenmiş satırlara dokunmaz.</summary>
    public static async Task<int> SeedAsync(Sql sql, string tenant, IEnumerable<ProcessingActivity> activities, string? userId, string userName, CancellationToken ct)
    {
        var added = 0;
        foreach (var a in activities)
            added += await sql.ExecuteAsync("""
                INSERT INTO governance_privacy_inventory ("Id","TenantSlug","ActivityKey","Source","Module","Activity","Subjects","DataCategories","Purpose",
                    "LegalBasis","Special","Retention","RetentionCategory","Recipients","TransferProviders","Measures","IsActive","CreatedAt","UpdatedAt","UpdatedBy","UpdatedByName")
                VALUES ($1,$2,$3,'Catalog',$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,true,now(),now(),$16,$17)
                ON CONFLICT ("TenantSlug","ActivityKey") DO NOTHING
                """, ct, Guid.NewGuid(), tenant, a.Id, a.Module, a.Activity, a.Subjects, a.DataCategories, a.Purpose, a.LegalBasis, a.Special,
                a.Retention, a.RetentionCategory, a.Recipients, a.TransferProviders, a.Measures, userId, userName);
        return added;
    }

    public static Task<List<Item>> ListAsync(Sql sql, string tenant, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT "Id","ActivityKey","Source","Module","Activity","Subjects","DataCategories","Purpose","LegalBasis","Special","Retention",
                   "RetentionCategory","Recipients","TransferProviders","Measures","IsActive","UpdatedAt","UpdatedByName"
            FROM governance_privacy_inventory WHERE "TenantSlug" = $1 ORDER BY "Module","Activity"
            """, r => new Item(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<string[]>(5),
                r.GetFieldValue<string[]>(6), r.GetString(7), r.GetString(8), r.GetBoolean(9), r.GetString(10), r.Str(11),
                r.GetFieldValue<string[]>(12), r.GetFieldValue<string[]>(13), r.GetString(14), r.GetBoolean(15), r.GetFieldValue<DateTime>(16), r.Str(17)),
            ct, tenant);

    /// <summary>"Kimlik (ad, soyad)" → ("Kimlik", "ad, soyad"). Parantez yoksa açıklama boş.</summary>
    public static (string Category, string Detail) SplitCategory(string s)
    {
        s = (s ?? "").Trim();
        var i = s.IndexOf(" (", StringComparison.Ordinal);
        if (i > 0 && s.EndsWith(')')) return (s[..i].Trim(), s[(i + 2)..^1].Trim());
        return (s, "");
    }

    public sealed record TransferInfo(string Key, string Name, string Country, string Mechanism, bool InUse);

    /// <summary>VERBİS sütunları (Veri Sorumluları Sicil Bilgi Sistemi alanlarıyla aynı sırada).</summary>
    public static readonly string[] Columns =
    {
        "Veri kategorisi", "Kişisel veri", "İşleme amacı", "Hukuki sebep", "İlgili kişi grubu", "Alıcı / alıcı grubu",
        "Saklama süresi", "Yurt dışına aktarım", "Özel nitelikli", "İdari ve teknik tedbirler", "İşleme faaliyeti", "Modül",
    };

    public static readonly string[] ColumnsEn =
    {
        "Data category", "Personal data", "Processing purpose", "Legal basis", "Data subject group", "Recipient / recipient group",
        "Retention period", "Transfer abroad", "Special category", "Administrative and technical measures", "Processing activity", "Module",
    };

    public static string[] ColumnsFor(bool en) => en ? ColumnsEn : Columns;

    /// <summary>Etkin her faaliyet × veri kategorisi bir satır. Yurt dışı aktarım, kayıtlı dayanağıyla birlikte yazılır.</summary>
    /// <remarks>
    /// <paramref name="en"/>: arayüz dili İngilizceyken katalog metinleri <see cref="PrivacyCatalogEn"/> ile çevrilir
    /// (kiracının düzenlediği metin aynen kalır). VERBİS'e giriş Türkçe yapılır; Türkçe arayüzde dosya Türkçedir.
    /// </remarks>
    public static List<string[]> Rows(IEnumerable<Item> items, IReadOnlyDictionary<string, TransferInfo> transfers, bool en = false)
    {
        var rows = new List<string[]>();
        string none = en ? "None" : "Yok", yes = en ? "Yes" : "Evet", no = en ? "No" : "Hayır", unused = en ? ", not in use" : ", kullanılmıyor";
        string T(string v) => en ? PrivacyCatalogEn.T(v) : v;
        foreach (var src in items.Where(i => i.IsActive))
        {
            var a = en ? PrivacyCatalogEn.Translate(src) : src;
            var abroad = a.TransferProviders.Length == 0 ? none
                : string.Join("; ", a.TransferProviders.Select(k => transfers.TryGetValue(k, out var t)
                    ? $"{T(t.Name)} ({T(t.Country)}) — {T(t.Mechanism)}{(t.InUse ? "" : unused)}" : k));
            var categories = a.DataCategories.Length == 0 ? new[] { "" } : a.DataCategories;
            foreach (var c in categories)
            {
                var (cat, detail) = SplitCategory(c);
                rows.Add(new[]
                {
                    cat, detail, a.Purpose, a.LegalBasis, string.Join(", ", a.Subjects), a.Recipients.Length == 0 ? none : string.Join(", ", a.Recipients),
                    a.Retention, abroad, a.Special ? yes : no, a.Measures, a.Activity, a.Module,
                });
            }
        }
        return rows;
    }

    /// <summary>CSV (noktalı virgül ayraçlı, UTF-8 BOM — Türkçe Excel doğrudan açar). Formül enjeksiyonuna karşı = + - @ ile başlayan hücre kaçırılır.</summary>
    public static string Csv(IEnumerable<string[]> rows, bool en = false)
    {
        static string Cell(string v)
        {
            v ??= "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
            return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
        var sb = new StringBuilder("﻿");
        sb.AppendLine(string.Join(';', ColumnsFor(en).Select(Cell)));
        foreach (var r in rows) sb.AppendLine(string.Join(';', r.Select(Cell)));
        return sb.ToString();
    }

    /// <summary>Yazdırılabilir HTML (tarayıcıdan PDF'e). Tüm değerler HTML kaçışlıdır.</summary>
    public static string Html(string company, IEnumerable<string[]> rows, DateTime generatedAt, DateTime? lastUpdated, string? lastUpdatedBy, int activities, bool en = false)
    {
        // Türkçe harfler sayısal varlığa çevrilmeden (okunur kaynak), < > & " ' kaçışlı.
        static string E(string? s) => HtmlText.Encode(s ?? "");
        string L(string tr, string e) => en ? e : tr;
        var title = L($"Kişisel veri işleme envanteri — {company}", $"Personal data processing inventory — {company}");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(en ? "en" : "tr").Append("\"><head><meta charset=\"utf-8\"><title>")
          .Append(E(title))
          .Append("</title><style>body{font:11px/1.4 system-ui,sans-serif;margin:24px;color:#111}h1{font-size:18px;margin:0 0 4px}")
          .Append("p.meta{color:#555;margin:0 0 12px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:4px 6px;vertical-align:top;text-align:left}")
          .Append("th{background:#f1f1f1}tr{page-break-inside:avoid}@page{size:A4 landscape;margin:12mm}@media print{.no-print{display:none}}</style></head><body>")
          .Append("<button class=\"no-print\" onclick=\"window.print()\">").Append(E(L("Yazdır", "Print"))).Append("</button>")
          .Append("<h1>").Append(E(title)).Append("</h1><p class=\"meta\">")
          .Append(E(L($"KVKK m.16 / VERBİS. Oluşturma: {TrTime.ToTr(generatedAt):dd.MM.yyyy HH:mm}. ", $"KVKK art. 16 / VERBİS. Generated: {TrTime.ToTr(generatedAt):dd.MM.yyyy HH:mm}. ")))
          .Append(E(lastUpdated is null ? "" : L("Son güncelleme: ", "Last updated: ") + $"{TrTime.ToTr(lastUpdated.Value):dd.MM.yyyy HH:mm}{(lastUpdatedBy is null ? "" : " — " + lastUpdatedBy)}. "))
          .Append(E(L($"{activities} işleme faaliyeti.", $"{activities} processing activities."))).Append("</p><table><thead><tr>");
        foreach (var c in ColumnsFor(en)) sb.Append("<th>").Append(E(c)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in rows)
        {
            sb.Append("<tr>");
            foreach (var v in r) sb.Append("<td>").Append(E(v)).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }

    /// <summary>Düzenleme girdisinin doğrulaması (null = geçerli). Liste alanları kırpılır, boşlar atılır.</summary>
    public static string? Validate(string? module, string? activity, string? purpose, string? legalBasis, string? retention, IReadOnlyCollection<string>? categories, IReadOnlyCollection<string>? subjects)
    {
        if (string.IsNullOrWhiteSpace(module) || module.Trim().Length > 120) return "Modül gerekli (en fazla 120 karakter).";
        if (string.IsNullOrWhiteSpace(activity) || activity.Trim().Length > 300) return "İşleme faaliyeti gerekli (en fazla 300 karakter).";
        if (string.IsNullOrWhiteSpace(purpose)) return "İşleme amacı gerekli.";
        if (string.IsNullOrWhiteSpace(legalBasis)) return "Hukuki sebep gerekli (KVKK m.5 / m.6).";
        if (string.IsNullOrWhiteSpace(retention)) return "Saklama süresi gerekli.";
        if (categories is null || categories.Count(c => !string.IsNullOrWhiteSpace(c)) == 0) return "En az bir veri kategorisi gerekli.";
        if (subjects is null || subjects.Count(c => !string.IsNullOrWhiteSpace(c)) == 0) return "En az bir ilgili kişi grubu gerekli.";
        foreach (var v in new[] { purpose, legalBasis, retention })
            if (v!.Length > 4000) return "Metin alanları en fazla 4000 karakter.";
        return null;
    }

    public static string[] CleanList(IEnumerable<string>? items, int max = 50) =>
        (items ?? Enumerable.Empty<string>()).Select(s => s?.Trim() ?? "").Where(s => s.Length is > 0 and <= 300)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToArray();
}
