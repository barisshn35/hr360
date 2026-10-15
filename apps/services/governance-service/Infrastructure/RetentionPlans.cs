using System.Security.Cryptography;
using System.Text;
using GovernanceService.Models;

namespace GovernanceService.Infrastructure;

/* ======================================================================
 * Dalga 10 (madde 56): saklama süresi uygulayıcısının tablo bazında planı.
 *
 * Her kategori, çalıştırılacak adımların (tablo, işlem, SQL) listesine çevrilir.
 * Aynı plan hem gerçek çalıştırmada hem kuru çalıştırmada (önizleme: tablo
 * başına etkilenecek satır sayısı) kullanılır; böylece önizleme ile uygulama
 * birbirinden ayrışamaz. Ayrılmış çalışan anonimleştirmesi tüm servislerin
 * kişiyle bağlantılı serbest metin, ad ve iletişim alanlarını kapsar; yasal
 * saklama yükümlülüğü olan kayıtlar (bordro, masraf/fiş, İSG) bilerek bırakılır
 * ve rapora "saklanan" olarak yazılır.
 * ==================================================================== */

/// <summary>Planın tek adımı. <c>Exec</c> çalıştırılır, <c>Count</c> önizlemede sayılır (aynı koşul).</summary>
public sealed record RetentionStep(string Table, string Label, string Kind, string Exec, string Count, bool UseRetentionConnection = false);

/// <summary>Kayıtların başvurduğu nesne deposu anahtarı (silinmeden önce toplanır).</summary>
public sealed record StorageRef(string Table, string Column, string Select);

/// <summary>Bilerek dokunulmayan kayıt (yasal saklama ya da kendi politikası).</summary>
public sealed record RetainedTable(string Table, string Reason);

public sealed record RetentionPlan(string Category, string Action, int Subjects, object?[] Args,
    IReadOnlyList<RetentionStep> Steps, IReadOnlyList<StorageRef> Storage, IReadOnlyList<RetainedTable> Retained);

public sealed record StepResult(string Table, string Label, string Kind, int Rows, string? Skipped = null);

public static class RetentionPlans
{
    // Fiil ve tablo adı tek bir metin sabitinde ("UPDATE tablo SET", "DELETE FROM tablo") yazılır: en az yetki
    // rol üreticisi (scripts/db-roles.py) yazma yetkisini metin sabitlerinden çıkarır.
    private static string TableOf(string head) => head.Split(' ', StringSplitOptions.RemoveEmptyEntries)[head.StartsWith("DELETE") ? 2 : 1];

    private static RetentionStep Upd(string head, string label, string set, string where) =>
        new(TableOf(head), label, "Anonymize", $"{head} {set} WHERE {where}", $"SELECT count(*) FROM {TableOf(head)} WHERE {where}");

    private static RetentionStep Del(string head, string label, string where, bool retentionConn = false) =>
        new(TableOf(head), label, "Delete", $"{head} WHERE {where}", $"SELECT count(*) FROM {TableOf(head)} WHERE {where}", retentionConn);

    /* ---------------------------------------------------------------- ayrılmış çalışanlar */

    /// <summary>
    /// Ayrılmış çalışan(lar)ın kişisel verisini tüm servislerde geri döndürülemez biçimde siler/anonimleştirir.
    /// Parametreler: $1 kiracı, $2 çalışan kimlikleri (uuid[]), $3 Keycloak kullanıcı kimlikleri (text[]).
    /// Sıra önemlidir: kullanıcı kimliğiyle bağlanan tablolar, employee_employees."KeycloakUserId" silinmeden
    /// önce ($3 önceden okunur) işlenir; çalışan kaydı en sonda anonimleşir.
    /// </summary>
    public static readonly RetentionStep[] EmployeeSteps =
    {
        Upd("UPDATE engagement_profiles SET", "Profil (doğum tarihi, adres, IBAN, TCKN, acil durum kişisi, beceriler)",
            """
            "BirthDate" = NULL, "Bio" = NULL, "Pronouns" = NULL, "Address" = NULL, "EmergencyContactName" = NULL, "EmergencyContactPhone" = NULL,
            "Iban" = NULL, "NationalId" = NULL, "LinkedInUrl" = NULL, "Skills" = '{}', "Interests" = '{}'
            """,
            """
            "TenantSlug" = $1 AND "EmployeeId" = ANY($2) AND ("BirthDate" IS NOT NULL OR "Bio" IS NOT NULL OR "Address" IS NOT NULL
              OR "EmergencyContactName" IS NOT NULL OR "EmergencyContactPhone" IS NOT NULL OR "Iban" IS NOT NULL OR "NationalId" IS NOT NULL
              OR "LinkedInUrl" IS NOT NULL OR "Pronouns" IS NOT NULL OR cardinality("Skills") > 0 OR cardinality("Interests") > 0)
            """),
        Upd("UPDATE engagement_kudos SET", "Takdir (alan kişinin adı)", "\"ToName\" = 'Anonim'",
            "\"TenantSlug\" = $1 AND \"ToEmployeeId\" = ANY($2) AND \"ToName\" <> 'Anonim'"),
        Upd("UPDATE engagement_kudos SET", "Takdir (gönderen kişinin adı)", "\"FromName\" = 'Anonim'",
            "\"TenantSlug\" = $1 AND (\"FromEmployeeId\" = ANY($2) OR \"FromUserId\" = ANY($3)) AND \"FromName\" <> 'Anonim'"),
        Del("DELETE FROM engagement_mentor_profiles", "Mentorluk profili", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Upd("UPDATE engagement_mentorships SET", "Mentorluk eşleşmesi (ad, hedef)",
            """
            "MentorName" = CASE WHEN "MentorUserId" = ANY($3) THEN 'Anonim' ELSE "MentorName" END,
            "MenteeName" = CASE WHEN "MenteeUserId" = ANY($3) THEN 'Anonim' ELSE "MenteeName" END, "Goal" = NULL
            """,
            "\"TenantSlug\" = $1 AND (\"MentorUserId\" = ANY($3) OR \"MenteeUserId\" = ANY($3))"),
        Upd("UPDATE engagement_internal_applications SET", "İç ilan başvurusu (ad, motivasyon)", "\"PersonName\" = 'Anonim', \"Motivation\" = NULL",
            "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3)) AND (\"PersonName\" <> 'Anonim' OR \"Motivation\" IS NOT NULL)"),
        Upd("UPDATE engagement_one_on_ones SET", "Birebir görüşme (ad, gündem, notlar)",
            """
            "EmployeeName" = CASE WHEN "EmployeeId" = ANY($2) THEN 'Anonim' ELSE "EmployeeName" END,
            "ManagerName" = CASE WHEN "ManagerUserId" = ANY($3) THEN 'Anonim' ELSE "ManagerName" END,
            "Agenda" = NULL, "SharedNotes" = NULL, "PrivateNotes" = NULL
            """,
            "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"EmployeeUserId\" = ANY($3) OR \"ManagerUserId\" = ANY($3))"),
        Del("DELETE FROM engagement_presence", "Ofis/uzaktan çalışma durumu", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Del("DELETE FROM engagement_desk_bookings", "Masa rezervasyonu", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Upd("UPDATE engagement_offboarding_cases SET", "Ayrılış kaydı (ad, çıkış görüşmesi, hesap notu)",
            "\"EmployeeName\" = 'Anonim', \"ExitInterview\" = NULL, \"AccountNote\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND (\"EmployeeName\" <> 'Anonim' OR \"ExitInterview\" IS NOT NULL OR \"AccountNote\" IS NOT NULL)"),
        Upd("UPDATE leave_requests SET", "İzin talebi gerekçesi (tarih ve gün sayısı kalır)", "\"Reason\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Reason\" IS NOT NULL"),
        Upd("UPDATE timeshift_time_entries SET", "Puantaj notu (süreler kalır)", "\"Note\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Note\" IS NOT NULL"),
        Upd("UPDATE timeshift_clock_punches SET", "Giriş-çıkış ham konumu", "\"RawLatitude\" = NULL, \"RawLongitude\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND (\"RawLatitude\" IS NOT NULL OR \"RawLongitude\" IS NOT NULL)"),
        Del("DELETE FROM timeshift_clock_credentials", "Kart/PIN kimlik bilgisi", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM timeshift_shift_preferences", "Vardiya tercihleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Upd("UPDATE timeshift_overtime_requests SET", "Fazla mesai gerekçesi", "\"Reason\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Reason\" IS NOT NULL"),
        Upd("UPDATE expense_travel_requests SET", "Seyahat (pasaport numarası)", "\"PassportCipher\" = NULL, \"PassportPurgedAt\" = now()",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"PassportCipher\" IS NOT NULL"),
        Upd("UPDATE performance_reviews SET", "Değerlendirme metinleri (puanlar kalır)", "\"Strengths\" = NULL, \"Improvements\" = NULL, \"Comments\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND (\"Strengths\" IS NOT NULL OR \"Improvements\" IS NOT NULL OR \"Comments\" IS NOT NULL)"),
        Upd("UPDATE performance_feedback SET", "Kişiye verilen geri bildirim metni", "\"Body\" = '(anonimleştirildi)', \"ReasonDetail\" = NULL",
            "\"TenantSlug\" = $1 AND \"ToEmployeeId\" = ANY($2) AND \"Body\" <> '(anonimleştirildi)'"),
        Upd("UPDATE performance_potential_ratings SET", "Potansiyel değerlendirme notu", "\"Note\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Note\" IS NOT NULL"),
        // Dalga 11: kalibrasyon karar notları (hücre ve onay kanıtı kalır), 360 (kişi hakkındaki talep yanıtlarıyla
        // silinir; değerlendiren olarak katılım işareti kapanmış taleplerde silinir — yanıtlar zaten kimliksizdir),
        // eğitim hatırlatma kayıtları, çalışan önerisindeki öneren notu (ödül tutarı/kararı bordro ispatı olarak kalır).
        Upd("UPDATE performance_calibration_items SET", "Kalibrasyon karar notu (hücre ve onay kalır)", "\"DecisionNote\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"DecisionNote\" IS NOT NULL"),
        Upd("UPDATE performance_calibration_changes SET", "Kalibrasyon değişiklik notu (değişiklik kaydı kalır)", "\"Note\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Note\" IS NOT NULL"),
        Del("DELETE FROM performance_f360_requests", "Kişi hakkındaki 360 geri bildirim talepleri ve yanıtları",
            "\"TenantSlug\" = $1 AND \"SubjectEmployeeId\" = ANY($2)"),
        Del("DELETE FROM performance_f360_participants", "360 değerlendiren katılım işareti (kapanmış talepler)",
            "\"TenantSlug\" = $1 AND \"ReviewerEmployeeId\" = ANY($2) AND \"RequestId\" IN (SELECT \"Id\" FROM performance_f360_requests WHERE \"Status\" <> 'Open')"),
        Del("DELETE FROM learning_due_reminders", "Eğitim/İSG son tarih hatırlatma kayıtları",
            "\"TenantSlug\" = $1 AND (\"SubjectEmployeeId\" = ANY($2) OR \"RecipientEmployeeId\" = ANY($2))"),
        Upd("UPDATE recruitment_referrals SET", "Çalışan önerisi (öneren notu, yakınlık; ödül kararı kalır)", "\"Note\" = NULL, \"Relationship\" = NULL, \"RewardNote\" = NULL",
            "\"TenantSlug\" = $1 AND \"ReferrerEmployeeId\" = ANY($2) AND (\"Note\" IS NOT NULL OR \"Relationship\" IS NOT NULL OR \"RewardNote\" IS NOT NULL)"),
        // Dalga 12: Google/Microsoft hesap açma-kapama istekleri. İşlem kaydı (sağlayıcı, eylem, durum, karar) kanıt
        // olarak kalır; hesap e-postası, harici kimlik, not ve hata metni silinir. Bekleyen istekler (hesap kapatma
        // henüz yapılmadı) bozulmasın diye yalnızca sonuçlanmış istekler anonimleştirilir.
        Upd("UPDATE governance_provisioning_requests SET", "Hesap açma/kapama isteği (hesap e-postası, harici kimlik, not)",
            "\"AccountEmail\" = NULL, \"ExternalId\" = NULL, \"Note\" = NULL, \"Error\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Status\" NOT IN ('Pending', 'Processing') AND (\"AccountEmail\" IS NOT NULL OR \"ExternalId\" IS NOT NULL OR \"Note\" IS NOT NULL OR \"Error\" IS NOT NULL)"),
        Upd("UPDATE learning_certifications SET", "Sertifika kimlik numarası", "\"CredentialId\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"CredentialId\" IS NOT NULL"),
        Upd("UPDATE learning_scorm_runtime SET", "SCORM oturum verisi", "\"SuspendData\" = NULL, \"LessonLocation\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND (\"SuspendData\" IS NOT NULL OR \"LessonLocation\" IS NOT NULL)"),
        Upd("UPDATE onboarding_asset_assignments SET", "Zimmet notu", "\"Notes\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND \"Notes\" IS NOT NULL"),
        Del("DELETE FROM notification_messages", "Bildirim geçmişi", "\"TenantSlug\" = $1 AND \"RecipientEmployeeId\" = ANY($2)"),
        Del("DELETE FROM notification_push_subscriptions", "Anlık bildirim abonelikleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM notification_preferences", "Bildirim tercihleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_chat_context", "Sohbet asistanı bağlamı", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_chat_exit_progress", "Sohbetten çıkış anketi ara yanıtları", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_chat_identities", "Sohbet platformu hesap bağlantısı", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_calendar_connections", "Takvim bağlantısı (OAuth jetonları)", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Del("DELETE FROM governance_calendar_feeds", "Takvim aboneliği bağlantısı", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Del("DELETE FROM governance_chat_pending", "Sohbetten bekleyen işlemler", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_chat_optins", "Sohbet bildirim tercihleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_chat_pulse_answered", "Nabız anketi yanıtladı işaretleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_oauth_states", "Yarım kalmış OAuth bağlantı durumları", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Del("DELETE FROM governance_signature_otps", "E-imza doğrulama kodları", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Del("DELETE FROM governance_ethics_committee", "Etik kurulu üyeliği", "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3))"),
        Del("DELETE FROM notification_category_prefs", "Bildirim kategori tercihleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        Upd("UPDATE expense_hr_cases SET", "İK talepleri (açıklama ve çözüm metni; konu ve tarih kalır)", "\"Description\" = NULL, \"Resolution\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND (\"Description\" IS NOT NULL OR \"Resolution\" IS NOT NULL)"),
        Upd("UPDATE governance_consents SET", "Rıza kayıtları (ad, IP adresi; rıza kanıtı kalır)", "\"PersonName\" = 'Anonim', \"IpAddress\" = NULL",
            "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3)) AND (\"PersonName\" <> 'Anonim' OR \"IpAddress\" IS NOT NULL)"),
        Upd("UPDATE governance_acknowledgements SET", "Duyuru/politika okuma kayıtları (ad)", "\"PersonName\" = 'Anonim'",
            "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3)) AND \"PersonName\" <> 'Anonim'"),
        Upd("UPDATE governance_analysis_objections SET", "Otomatik analize itiraz (ad, gerekçe)", "\"PersonName\" = 'Anonim', \"Reason\" = NULL",
            "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2) AND (\"PersonName\" <> 'Anonim' OR \"Reason\" IS NOT NULL)"),
        Upd("UPDATE governance_data_requests SET", "İlgili kişi başvuruları (ad, iletişim, açıklama; tarih ve sonuç kalır)",
            "\"PersonName\" = 'Anonim', \"Contact\" = NULL, \"Details\" = NULL",
            "\"TenantSlug\" = $1 AND (\"EmployeeId\" = ANY($2) OR \"UserId\" = ANY($3)) AND (\"PersonName\" <> 'Anonim' OR \"Contact\" IS NOT NULL OR \"Details\" IS NOT NULL)"),
        Del("DELETE FROM governance_data_request_packages", "Erişim başvurusu veri paketleri", "\"TenantSlug\" = $1 AND \"EmployeeId\" = ANY($2)"),
        // Çalışan kaydı en sonda: KeycloakUserId ($3) yukarıdaki adımlar için önceden okundu.
        Upd("UPDATE employee_employees SET", "Çalışan kaydı (ad, e-posta, telefon, hesap bağlantısı)",
            """
            "FirstName" = 'Anonim', "LastName" = upper(left("Id"::text, 6)), "Email" = 'anon-' || "Id" || '@anonim.invalid',
            "Phone" = NULL, "KeycloakUserId" = NULL
            """,
            "\"TenantSlug\" = $1 AND \"Id\" = ANY($2)"),
    };

    public static readonly StorageRef[] EmployeeStorage =
    {
        // Masraf fişleri ve belgeleri VUK gereği saklanır: anahtarları listelenir ama silinmez (bkz. EmployeeRetained).
    };

    public static readonly RetainedTable[] EmployeeRetained =
    {
        new("compensation_*", "Ücret ve bordro kayıtları: 5510 s. K. m.86 ve VUK m.253 gereği 10 yıl; \"Bordro pusulaları\" politikasıyla silinir."),
        new("expense_claims / expense_items / expense_documents", "Masraf, fiş ve belgeler: VUK saklama yükümlülüğü; fiş/belge dosyaları (nesne deposu anahtarları) silinmez."),
        new("governance_osh_* ", "İş kazası, eğitim ve sağlık muayenesi kayıtları: 6331 s. K. ve yönetmelik gereği en az 15 yıl; otomatik imha yok."),
        new("governance_disciplinary_cases", "Kendi saklama politikası (\"Kapatılmış disiplin vakaları\")."),
        new("governance_custom_field_values", "Alan bazında saklama süresi (\"Özel alan değerleri\" politikası)."),
        new("audit_log", "Değiştirilemez denetim kaydı; \"Denetim kayıtları\" politikasıyla süre sonunda silinir."),
        new("leave_requests / timeshift_time_entries / employee_assignments", "Tarih, gün ve süre alanları istatistik ve yasal ispat için kalır (kimlik alanları yukarıda anonimleşir)."),
    };

    /// <summary>
    /// Kişi kimliği taşıyan ama anonimleştirmede bilerek dokunulmayan tablolar ve gerekçesi (kapsam denetimi:
    /// "imha doğrulama" ekranı, bu listede ya da adımlarda olmayan tabloyu "incelenmeli" diye gösterir).
    /// Sonu "*" ile biten ad önektir.
    /// </summary>
    public static readonly (string Table, string Reason)[] CoverageExempt =
    {
        ("compensation_*", "Ücret/bordro: 10 yıl yasal saklama (Bordro pusulaları politikası)"),
        ("expense_claims", "Masraf: VUK saklama yükümlülüğü"),
        ("expense_documents", "Belge kaydı ve dosya anahtarı: VUK / imza kanıtı"),
        ("expense_document_signatures", "E-imza kanıtı (belge özeti)"),
        ("governance_osh_exams", "İSG sağlık muayenesi: 15 yıl"),
        ("governance_disciplinary_cases", "Kendi politikası (Kapatılmış disiplin vakaları)"),
        ("governance_custom_field_values", "Kendi politikası (alan bazında süre)"),
        ("governance_document_requests", "Kendi politikası (Belge talepleri)"),
        ("governance_chat_messages", "Kendi politikası (Sohbet botu mesaj kayıtları)"),
        ("governance_access_review_items", "Erişim gözden geçirme kanıtı (rol kararları)"),
        ("learning_enrollments", "Eğitim kayıtları: İSG eğitimlerinin ispatı (6331 s. K.)"),
        ("learning_module_progress", "Eğitim ilerlemesi: İSG eğitimlerinin ispatı"),
        ("learning_quiz_attempts", "Sınav sonuçları: eğitim ispatı"),
        ("learning_cert_reminders", "Sertifika hatırlatma kayıtları (yalnızca tarih)"),
        ("learning_competency_assessments", "Yetkinlik değerlendirmeleri (puan; değerlendiren adı iş kaydı)"),
        ("leave_balances", "Yalnızca gün sayıları"),
        ("employee_assignments", "Görev geçmişi (istatistik); ad yok"),
        ("onboarding_plans", "İşe başlama görevleri; ad yok"),
        ("organization_team_members", "Ekip geçmişi (istatistik)"),
        ("performance_goals", "Hedef başlıkları (iş içeriği)"),
        ("performance_snapshots", "Puan özetleri (istatistik)"),
        ("performance_ninebox_overrides", "Kalibrasyon kanıtı (yönetici kararı)"),
        ("timeshift_assignments", "Vardiya planı: çalışma süresi ispatı (İş Kanunu m.63)"),
        ("timeshift_shift_overrides", "Vardiya değişiklikleri: çalışma süresi ispatı"),
        ("timeshift_shift_team_members", "Vardiya ekibi üyeliği"),
        ("workflow_delegations", "Onay yetki devri kanıtı"),
        ("recruitment_applications", "Aday kategorisi (Olumsuz sonuçlanan aday başvuruları)"),
        ("tenant_directory_users", "Dizin eşitlemesi (AD/LDAP/SCIM kaynağından yönetilir)"),
        ("audit_log", "Değiştirilemez denetim kaydı (Denetim kayıtları politikası)"),
    };

    public static string? ExemptReason(string table) =>
        CoverageExempt.FirstOrDefault(e => e.Table.EndsWith('*') ? table.StartsWith(e.Table[..^1], StringComparison.Ordinal) : e.Table == table).Reason;

    /* ---------------------------------------------------------------- olumsuz sonuçlanan adaylar */

    private static readonly RetentionStep ReferralScrub =
        Upd("UPDATE recruitment_referrals SET", "Çalışan önerisi (adaya dair öneren notu, yakınlık)", "\"Note\" = NULL, \"Relationship\" = NULL",
            "\"TenantSlug\" = $1 AND \"CandidateId\" = ANY($2) AND (\"Note\" IS NOT NULL OR \"Relationship\" IS NOT NULL)");

    /// <summary>recruitment-service RetentionService.AnonymizeAsync ile aynı alanlar. $1 kiracı, $2 aday kimlikleri.</summary>
    public static readonly RetentionStep[] CandidateAnonymizeSteps =
    {
        Upd("UPDATE recruitment_applications SET", "Başvuru (ön yazı, notlar, öz-hizmet bağlantısı)",
            "\"CoverNote\" = NULL, \"Notes\" = NULL, \"SelfServiceTokenHash\" = NULL, \"DuplicateReason\" = NULL",
            "\"TenantSlug\" = $1 AND \"CandidateId\" = ANY($2)"),
        Upd("UPDATE recruitment_interviews SET", "Mülakat (not, yer, toplantı bağlantısı)", "\"Notes\" = NULL, \"Location\" = NULL, \"MeetingUrl\" = NULL",
            "\"TenantSlug\" = $1 AND \"ApplicationId\" IN (SELECT \"Id\" FROM recruitment_applications WHERE \"CandidateId\" = ANY($2))"),
        Upd("UPDATE recruitment_scorecards SET", "Puan kartı notları ve ölçüt kanıt notları (puanlar kalır)",
            "\"Notes\" = NULL, \"ScoresJson\" = coalesce((SELECT jsonb_agg(e - 'evidence' - 'Evidence') FROM jsonb_array_elements(\"ScoresJson\"::jsonb) e)::text, '[]')",
            "\"TenantSlug\" = $1 AND \"InterviewId\" IN (SELECT i.\"Id\" FROM recruitment_interviews i JOIN recruitment_applications a ON a.\"Id\" = i.\"ApplicationId\" WHERE a.\"CandidateId\" = ANY($2))"),
        Upd("UPDATE recruitment_offers SET", "Teklif mektubu, imzalı mektup ve yan haklar",
            "\"LetterText\" = '(anonimleştirildi)', \"Benefits\" = NULL, \"DecisionNote\" = NULL, \"SignedLetterHtml\" = NULL, \"SignTokenHash\" = NULL",
            "\"TenantSlug\" = $1 AND \"ApplicationId\" IN (SELECT \"Id\" FROM recruitment_applications WHERE \"CandidateId\" = ANY($2))"),
        Del("DELETE FROM recruitment_status_links", "Aday durum bağlantıları (jeton özeti)",
            "\"TenantSlug\" = $1 AND \"ApplicationId\" IN (SELECT \"Id\" FROM recruitment_applications WHERE \"CandidateId\" = ANY($2))"),
        ReferralScrub,
        Upd("UPDATE recruitment_candidates SET", "Aday kaydı (ad, iletişim, özgeçmiş metni ve dosya bağlantısı, beceriler)",
            """
            "FirstName" = 'Anonim', "LastName" = 'Aday', "Email" = 'anon-' || "Id" || '@anonim.invalid', "Phone" = NULL,
            "NormalizedEmail" = NULL, "NormalizedPhone" = NULL, "ResumeStorageKey" = NULL, "ResumeText" = NULL, "Skills" = '{}',
            "Source" = NULL, "TalentPoolConsent" = false, "AnonymizedAt" = now()
            """,
            "\"TenantSlug\" = $1 AND \"Id\" = ANY($2)"),
    };

    public static readonly RetentionStep[] CandidateDeleteSteps =
    {
        // Öneri satırı aday silinince kalır (FK SET NULL; ödül kaydı): öneren notu önce temizlenir.
        ReferralScrub,
        // Başvurular, mülakatlar, puan kartları, teklifler ve durum bağlantıları yabancı anahtar (ON DELETE CASCADE) ile silinir.
        Del("DELETE FROM recruitment_candidates", "Aday kaydı ve bağlı başvuru/mülakat/puan kartı/teklif kayıtları", "\"TenantSlug\" = $1 AND \"Id\" = ANY($2)"),
    };

    public static readonly StorageRef[] CandidateStorage =
    {
        new("recruitment_candidates", "ResumeStorageKey",
            "SELECT \"ResumeStorageKey\" FROM recruitment_candidates WHERE \"TenantSlug\" = $1 AND \"Id\" = ANY($2) AND coalesce(\"ResumeStorageKey\", '') <> ''"),
    };

    /* ---------------------------------------------------------------- tek tablolu kategoriler ($1 kiracı, $2 ay) */

    public static RetentionStep[] SimpleSteps(string category) => category switch
    {
        // audit_log'dan silme yetkisi en az yetki kurulumunda yalnızca hr360_retention rolündedir.
        "AuditLog" => new[] { Del("DELETE FROM audit_log", "Denetim kayıtları", "\"TenantSlug\" = $1 AND \"OccurredAt\" < now() - make_interval(months => $2)", true) },
        "Notifications" => new[] { Del("DELETE FROM notification_messages", "Bildirim geçmişi", "\"TenantSlug\" = $1 AND \"CreatedAt\" < now() - make_interval(months => $2)") },
        "AiUsage" => new[] { Del("DELETE FROM governance_ai_usage", "Yapay zekâ kullanım kayıtları", "\"TenantSlug\" = $1 AND \"At\" < now() - make_interval(months => $2)") },
        "ChatContext" => new[] { Del("DELETE FROM governance_chat_context", "Sohbet asistanı bağlamı", "\"TenantSlug\" = $1 AND \"CreatedAt\" < now() - least(make_interval(months => $2), interval '30 days')") },
        "ChatMessages" => new[] { Del("DELETE FROM governance_chat_messages", "Sohbet botu mesaj kayıtları", "\"TenantSlug\" = $1 AND \"State\" <> 'Open' AND \"CreatedAt\" < now() - make_interval(months => $2)") },
        "Payslips" => new[]
        {
            new RetentionStep("compensation_payslips", "Bordro pusulaları (kapanmış dönemler)", "Delete",
                """
                DELETE FROM compensation_payslips s USING compensation_payroll_periods p
                WHERE s."PeriodId" = p."Id" AND p."Status" = 'Closed' AND s."TenantSlug" = $1
                  AND make_date(s."Year", s."Month", 1) < (now() - make_interval(months => $2))::date
                """,
                """
                SELECT count(*) FROM compensation_payslips s JOIN compensation_payroll_periods p ON s."PeriodId" = p."Id"
                WHERE p."Status" = 'Closed' AND s."TenantSlug" = $1 AND make_date(s."Year", s."Month", 1) < (now() - make_interval(months => $2))::date
                """),
        },
        "DocumentRequests" => new[] { Del("DELETE FROM governance_document_requests", "Belge talepleri ve şifreli belgeler", "\"TenantSlug\" = $1 AND \"Status\" <> 'Pending' AND \"CreatedAt\" < now() - make_interval(months => $2)") },
        "DisciplinaryCases" => new[] { Del("DELETE FROM governance_disciplinary_cases", "Kapatılmış disiplin vakaları", "\"TenantSlug\" = $1 AND \"Status\" = 'Closed' AND \"ClosedAt\" < now() - make_interval(months => $2)") },
        // Mesajlar ON DELETE CASCADE ile silinir.
        "EthicsReports" => new[] { Del("DELETE FROM governance_ethics_reports", "Kapatılmış etik bildirimleri ve yazışmaları", "\"TenantSlug\" = $1 AND \"Status\" = 'Closed' AND \"ClosedAt\" < now() - make_interval(months => $2)") },
        "Announcements" => new[]
        {
            new RetentionStep("governance_acknowledgements", "Süresi dolmuş duyuruların okuma kayıtları", "Delete",
                """
                DELETE FROM governance_acknowledgements k USING governance_announcements a
                WHERE k."SubjectType" = 'Announcement' AND k."SubjectId" = a."Id" AND a."TenantSlug" = $1
                  AND a."ExpireAt" IS NOT NULL AND a."ExpireAt" < now() - make_interval(months => $2)
                """,
                """
                SELECT count(*) FROM governance_acknowledgements k JOIN governance_announcements a ON k."SubjectId" = a."Id"
                WHERE k."SubjectType" = 'Announcement' AND a."TenantSlug" = $1 AND a."ExpireAt" IS NOT NULL AND a."ExpireAt" < now() - make_interval(months => $2)
                """),
            Del("DELETE FROM governance_announcements", "Süresi dolmuş duyurular", "\"TenantSlug\" = $1 AND \"ExpireAt\" IS NOT NULL AND \"ExpireAt\" < now() - make_interval(months => $2)"),
        },
        "WebhookDeliveries" => new[] { Del("DELETE FROM governance_webhook_deliveries", "Webhook gönderim kayıtları", "\"TenantSlug\" = $1 AND \"OccurredAt\" < now() - make_interval(months => $2)") },
        "CustomFieldValues" => new[]
        {
            new RetentionStep("governance_custom_field_values", "Ayrılmış çalışanların özel alan değerleri (süre alan bazında)", "Delete",
                """
                DELETE FROM governance_custom_field_values v USING governance_custom_fields f, employee_employees e
                WHERE v."FieldId" = f."Id" AND e."Id" = v."EmployeeId" AND e."Status" = 'Terminated' AND v."TenantSlug" = $1 AND $2 > 0
                  AND coalesce((SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id"), e."CreatedAt"::date)
                      < (now() - make_interval(months => f."RetentionMonths"))::date
                """,
                """
                SELECT count(*) FROM governance_custom_field_values v JOIN governance_custom_fields f ON v."FieldId" = f."Id"
                JOIN employee_employees e ON e."Id" = v."EmployeeId"
                WHERE e."Status" = 'Terminated' AND v."TenantSlug" = $1 AND $2 > 0
                  AND coalesce((SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id"), e."CreatedAt"::date)
                      < (now() - make_interval(months => f."RetentionMonths"))::date
                """),
        },
        _ => Array.Empty<RetentionStep>(),
    };

    /* ---------------------------------------------------------------- plan */

    /// <summary>
    /// Politikadan çalıştırılacak planı kurar. Ayrılmış çalışan ve aday kategorilerinde önce hedef kişiler seçilir
    /// (önizlemede de aynı seçim yapılır). <paramref name="employeeId"/> verilirse süre aranmaz (tek kişi, elle).
    /// </summary>
    public static async Task<RetentionPlan> BuildAsync(Sql sql, string tenant, string category, string action, int months, CancellationToken ct, Guid? employeeId = null)
    {
        months = Math.Max(employeeId is null ? 1 : 0, months);
        switch (category)
        {
            case "TerminatedEmployees":
            {
                var rows = employeeId is null
                    ? await sql.QueryAsync("""
                        SELECT e."Id", e."KeycloakUserId" FROM employee_employees e
                        WHERE e."TenantSlug" = $1 AND e."Status" = 'Terminated' AND e."Email" NOT LIKE 'anon-%'
                          AND coalesce((SELECT max(a."EffectiveTo") FROM employee_assignments a WHERE a."EmployeeId" = e."Id"), e."CreatedAt"::date)
                              < (now() - make_interval(months => $2))::date
                        """, r => (Id: r.GetGuid(0), User: r.Str(1)), ct, tenant, months)
                    : await sql.QueryAsync("""
                        SELECT e."Id", e."KeycloakUserId" FROM employee_employees e WHERE e."TenantSlug" = $1 AND e."Id" = $2 AND e."Status" = 'Terminated'
                        """, r => (Id: r.GetGuid(0), User: r.Str(1)), ct, tenant, employeeId.Value);
                var ids = rows.Select(r => r.Id).ToArray();
                var users = rows.Where(r => !string.IsNullOrEmpty(r.User)).Select(r => r.User!).ToArray();
                return new RetentionPlan(category, "Anonymize", ids.Length, new object?[] { tenant, ids, users },
                    ids.Length == 0 ? Array.Empty<RetentionStep>() : EmployeeSteps, EmployeeStorage, EmployeeRetained);
            }
            case "RejectedCandidates":
            {
                var ids = (await sql.QueryAsync("""
                    SELECT c."Id" FROM recruitment_candidates c
                    WHERE c."TenantSlug" = $1 AND c."AnonymizedAt" IS NULL AND c."Email" NOT LIKE 'anon-%'
                      AND NOT EXISTS (
                        SELECT 1 FROM recruitment_applications a WHERE a."CandidateId" = c."Id"
                        AND (a."Status" NOT IN ('Rejected','Withdrawn') OR coalesce(a."StatusChangedAt", a."AppliedAt") > now() - make_interval(months => $2)))
                      AND c."CreatedAt" < now() - make_interval(months => $2)
                    """, r => r.GetGuid(0), ct, tenant, months)).ToArray();
                var steps = action == "Delete" ? CandidateDeleteSteps : CandidateAnonymizeSteps;
                return new RetentionPlan(category, action, ids.Length, new object?[] { tenant, ids },
                    ids.Length == 0 ? Array.Empty<RetentionStep>() : steps, CandidateStorage, Array.Empty<RetainedTable>());
            }
            default:
                return new RetentionPlan(category, action, -1, new object?[] { tenant, months }, SimpleSteps(category),
                    Array.Empty<StorageRef>(), Array.Empty<RetainedTable>());
        }
    }

    private static readonly Lazy<Sql?> RetentionSql = new(() =>
        Environment.GetEnvironmentVariable("RETENTION_DB_CONNECTION") is { Length: > 0 } cs
            ? new Sql(Npgsql.NpgsqlDataSource.Create(cs)) : null);

    /// <summary>Tablo henüz yok / sütun yok (kısmi kurulum) ise adım atlanır ve raporda belirtilir.</summary>
    private static bool Missing(Npgsql.PostgresException e) => e.SqlState is "42P01" or "42703";

    /// <summary>Önizleme (kuru çalıştırma): her adımın etkileyeceği satır sayısı. Hiçbir şey değişmez.</summary>
    public static async Task<List<StepResult>> PreviewAsync(Sql sql, RetentionPlan plan, CancellationToken ct)
    {
        var list = new List<StepResult>();
        foreach (var s in plan.Steps)
        {
            try
            {
                var n = Convert.ToInt32(await sql.ScalarAsync(s.Count, ct, plan.Args) ?? 0);
                list.Add(new StepResult(s.Table, s.Label, s.Kind, n));
            }
            catch (Npgsql.PostgresException e) when (Missing(e))
            {
                list.Add(new StepResult(s.Table, s.Label, s.Kind, 0, e.SqlState));
            }
        }
        return list;
    }

    /// <summary>Planı uygular; nesne deposu anahtarları kayıtlar değişmeden önce silme kuyruğuna alınır.</summary>
    public static async Task<(List<StepResult> Steps, int Queued)> ExecuteAsync(Sql sql, RetentionPlan plan, CancellationToken ct)
    {
        var queued = 0;
        var tenant = (string)plan.Args[0]!;
        foreach (var r in plan.Storage)
        {
            try
            {
                var keys = await sql.QueryAsync(r.Select, x => x.GetString(0), ct, plan.Args);
                queued += await StorageDeletions.EnqueueAsync(sql, tenant, plan.Category, r.Table, r.Column, keys, ct);
            }
            catch (Npgsql.PostgresException e) when (Missing(e)) { }
        }
        var list = new List<StepResult>();
        foreach (var s in plan.Steps)
        {
            try
            {
                var n = await ((s.UseRetentionConnection ? RetentionSql.Value : null) ?? sql).ExecuteAsync(s.Exec, ct, plan.Args);
                list.Add(new StepResult(s.Table, s.Label, s.Kind, n));
            }
            catch (Npgsql.PostgresException e) when (Missing(e))
            {
                list.Add(new StepResult(s.Table, s.Label, s.Kind, 0, e.SqlState));
            }
        }
        return (list, queued);
    }

    /// <summary>İmha tutanağının etkilenen sayısı: kişi kategorilerinde kişi sayısı, diğerlerinde silinen satır.</summary>
    public static int Affected(RetentionPlan plan, IEnumerable<StepResult> steps) =>
        plan.Subjects >= 0 ? plan.Subjects : steps.Sum(s => s.Rows);
}

/* ======================================================================
 * Madde 57: nesne deposu (MinIO) silme doğrulaması.
 *
 * Silinen/anonimleştirilen kayıtların başvurduğu dosya anahtarları kuyruğa
 * alınır; governance bakım turu tenant-service'in iç ucundan (S3 kimlik
 * bilgileri yalnızca orada) nesneyi siler ve yokluğunu doğrular. Anahtarın
 * kendisi (ör. "ozgecmis/ayse-yilmaz.pdf") işlendikten sonra silinir, yalnızca
 * SHA-256 özeti kanıt olarak kalır.
 * ==================================================================== */
public static class StorageDeletions
{
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static async Task<int> EnqueueAsync(Sql sql, string tenant, string category, string table, string column, IEnumerable<string> keys, CancellationToken ct)
    {
        var n = 0;
        foreach (var k in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct())
        {
            await sql.ExecuteAsync("""
                INSERT INTO governance_storage_deletions ("Id","TenantSlug","Category","SourceTable","SourceColumn","StorageKey","KeyHash","Status","CreatedAt")
                VALUES ($1,$2,$3,$4,$5,$6,$7,'Pending',now())
                """, ct, Guid.NewGuid(), tenant, category, table, column, k, Hash(k));
            n++;
        }
        return n;
    }

    public sealed record ObjectResult(string Key, string Status, string? Detail);

    /// <summary>Tenant-service yanıt durumunu kuyruk durumuna çevirir (bilinmeyen durum = Failed).</summary>
    public static string MapStatus(string? s) => s switch
    {
        "Deleted" => "Deleted",
        "Absent" => "Absent",
        "NoBucket" or "NotAllowed" => "NotStored",
        _ => "Failed",
    };

    /// <summary>
    /// Bekleyen (ve en çok 5 kez başarısız olmuş) anahtarları işler. Ulaşılamazsa durum değişmez, bir sonraki turda yeniden denenir.
    /// </summary>
    public static async Task<int> ProcessAsync(Sql sql, IHttpClientFactory http, string? tenant, CancellationToken ct)
    {
        var rows = await sql.QueryAsync("""
            SELECT "Id","TenantSlug","StorageKey" FROM governance_storage_deletions
            WHERE "Status" IN ('Pending','Failed') AND "Attempts" < 5 AND "StorageKey" IS NOT NULL AND ($1::text IS NULL OR "TenantSlug" = $1)
            ORDER BY "CreatedAt" LIMIT 500
            """, r => (Id: r.GetGuid(0), Tenant: r.GetString(1), Key: r.GetString(2)), ct, tenant);
        if (rows.Count == 0) return 0;
        var token = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_TOKEN");
        if (string.IsNullOrEmpty(token)) return 0;
        var baseUrl = Chat.EnvVar.Or("TENANT_SERVICE_URL", "http://tenant-service:8080").TrimEnd('/');
        var done = 0;
        foreach (var group in rows.GroupBy(r => r.Tenant))
        {
            List<ObjectResult>? results;
            try
            {
                var client = http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/internal/storage/delete")
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new { tenantSlug = group.Key, keys = group.Select(g => g.Key).ToArray() }),
                };
                req.Headers.TryAddWithoutValidation(Security.InternalServiceToken.Header, token);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                results = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<List<ObjectResult>>(resp.Content,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                continue;
            }
            foreach (var row in group)
            {
                var res = results?.FirstOrDefault(x => x.Key == row.Key);
                var status = MapStatus(res?.Status);
                // Sonuçlanan anahtarın kendisi silinir; yalnızca özet kalır. Başarısızda yeniden denemek için tutulur.
                await sql.ExecuteAsync("""
                    UPDATE governance_storage_deletions SET "Status" = $2::text, "Detail" = $3, "Attempts" = "Attempts" + 1, "ProcessedAt" = now(),
                        "StorageKey" = CASE WHEN $2::text = 'Failed' AND "Attempts" < 4 THEN "StorageKey" ELSE NULL END
                    WHERE "Id" = $1
                    """, ct, row.Id, status, res?.Detail);
                done++;
            }
        }
        return done;
    }
}
