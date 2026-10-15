using System.Text.Json;
using GovernanceService.Tenancy;

namespace GovernanceService.Models;

/// <summary>Kafka'dan gelen her olayın kopyası (canlı radar + kural/webhook kaynağı). 30 gün tutulur.</summary>
public class GovernanceEvent
{
    public Guid Id { get; set; }
    public string? TenantSlug { get; set; }
    public string Topic { get; set; } = "";
    public string EventType { get; set; } = "";
    public JsonDocument? Payload { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/* ================================================================== KVKK */

public class Consent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string PersonName { get; set; } = "";
    public string ConsentType { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Granted { get; set; }
    public string? IpAddress { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public class DataRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string PersonName { get; set; } = "";
    /// <summary>Access | Rectification | Erasure | Objection</summary>
    public string Kind { get; set; } = "Access";
    public string? Details { get; set; }
    /// <summary>Received | InProgress | Completed | Rejected</summary>
    public string Status { get; set; } = "Received";
    public string? Response { get; set; }
    /// <summary>KVKK m.13: en geç 30 gün içinde yanıt.</summary>
    public DateTime DueAt { get; set; } = DateTime.UtcNow.AddDays(30);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    /// <summary>Panel | Email | Kep | Mail | InPerson — başvurunun geldiği kanal.</summary>
    public string Channel { get; set; } = "Panel";
    /// <summary>Panel dışı başvuruda başvurucunun iletişim bilgisi (yanıt için).</summary>
    public string? Contact { get; set; }
    /// <summary>KVKK: kimliği doğrulanmadan başvuruya kişisel veri içeren yanıt verilmez.</summary>
    public bool IdentityVerified { get; set; }
    public string? VerificationMethod { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>
/// Yurt dışına kişisel veri aktaran bir hizmet için hukuki dayanak kaydı (KVKK m.9).
/// Kayıt olmadan ilgili entegrasyon açılamaz ve veri gönderilmez.
/// </summary>
public class TransferAgreement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>slack | microsoft | google | zoom | anthropic | openai</summary>
    public string Provider { get; set; } = "";
    /// <summary>StandardContract | Adequacy | BindingCorporateRules | Undertaking</summary>
    public string Mechanism { get; set; } = "StandardContract";
    public DateOnly SignedAt { get; set; }
    /// <summary>Standart sözleşmenin Kurul'a bildirildiği tarih (imzadan itibaren 5 iş günü).</summary>
    public DateOnly? NotifiedAt { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Silme / anonimleştirme işlemlerinin kaydı (imha tutanağı). En az 3 yıl saklanır.</summary>
public class DestructionLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>Anonymize | Delete</summary>
    public string Action { get; set; } = "";
    public int Affected { get; set; }
    public int RetentionMonths { get; set; }
    /// <summary>Periodic | Manual | Request</summary>
    public string Trigger { get; set; } = "Periodic";
    public string Actor { get; set; } = "";
    public string Method { get; set; } = "";
    public DateTime RanAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Otomatik analize itiraz (KVKK m.11/1-g). Açık ya da kabul edilmiş itirazda kişinin
/// skoru üretilmez ve gösterilmez.
/// </summary>
public class AnalysisObjection : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string UserId { get; set; } = "";
    public string PersonName { get; set; } = "";
    /// <summary>AttritionRisk | PerformanceScore | AiSummary</summary>
    public string Analysis { get; set; } = "";
    public string? Reason { get; set; }
    /// <summary>Open | Upheld | Rejected</summary>
    public string Status { get; set; } = "Open";
    public string? Response { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
    public DateTime DueAt { get; set; } = DateTime.UtcNow.AddDays(30);
}

public class RetentionPolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>RejectedCandidates | TerminatedEmployees | AuditLog | Notifications</summary>
    public string Category { get; set; } = "";
    public int RetentionMonths { get; set; }
    /// <summary>Anonymize | Delete</summary>
    public string Action { get; set; } = "Anonymize";
    public bool IsEnabled { get; set; }
    public DateTime? LastRunAt { get; set; }
    public int LastAffected { get; set; }
}

/* ======================================================= Belge şablonları */

public class DocTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Genel";
    /// <summary>{{calisan.ad}} gibi yer tutucular içeren HTML/metin.</summary>
    public string Body { get; set; } = "";
    /// <summary>Çalışan bu belgeyi kendisi talep edebilir (Profilim › Belge talebi).</summary>
    public bool SelfService { get; set; }
    /// <summary>Talep İK onayından sonra düzenlenir; false ise hemen üretilir.</summary>
    public bool RequiresApproval { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Çalışanın belge talebi (çalışma belgesi, maaş yazısı...). Düzenlenen belge şifreli saklanır
/// ve doğrulama koduyla (kişisel veri göstermeden) doğrulanabilir. KVKK: belgeyi yalnızca
/// çalışan ve İK açar; saklama süresi dolunca silinir ("Belge talepleri").
/// </summary>
public class DocumentRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public Guid TemplateId { get; set; }
    public string TemplateName { get; set; } = "";
    public string? Purpose { get; set; }
    /// <summary>Pending | Issued | Rejected</summary>
    public string Status { get; set; } = "Pending";
    public string? DecisionNote { get; set; }
    public string? DecidedBy { get; set; }
    public string? VerificationCode { get; set; }
    /// <summary>Düzenlenen belgenin HTML'i (SecretBox ile şifreli).</summary>
    public string? DocumentEnc { get; set; }
    public string? DocumentHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? IssuedAt { get; set; }
}

/* ========================================================== Kural motoru */

public class RuleCondition
{
    /// <summary>Olay yükündeki alan (ör. "Days", "WorkflowType", "NewStatus").</summary>
    public string Field { get; set; } = "";
    /// <summary>eq | neq | gt | gte | lt | lte | contains</summary>
    public string Op { get; set; } = "eq";
    public string Value { get; set; } = "";
}

public class RuleAction
{
    /// <summary>notify | webhook | slack | teams</summary>
    public string Type { get; set; } = "notify";
    /// <summary>notify: çalışan id'si veya "requester"/"approver"/"hr"; webhook/slack/teams: URL (boşsa tanımlı entegrasyonlar).</summary>
    public string? Target { get; set; }
    /// <summary>{{alan}} yer tutuculu mesaj.</summary>
    public string Message { get; set; } = "";
}

public class Rule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Olay tipi (ör. "leave.approved") veya "*".</summary>
    public string Trigger { get; set; } = "";
    public List<RuleCondition> Conditions { get; set; } = new();
    public List<RuleAction> Actions { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public int FireCount { get; set; }
    public DateTime? LastFiredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RuleRun : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public string EventType { get; set; } = "";
    public string Result { get; set; } = "";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/* ============================================== Webhook, API anahtarı, entegrasyon */

public class Webhook : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>HMAC-SHA256 imza anahtarı (X-HR360-Signature).</summary>
    public string Secret { get; set; } = "";
    public List<string> Events { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
    public int? LastStatus { get; set; }
    public DateTime? LastDeliveredAt { get; set; }
    public int FailureCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>null/manual: İK ekranından; "rest-hook": Zapier/n8n aboneliği (açık API).</summary>
    public string? Source { get; set; }
    /// <summary>REST hook aboneliğini açan API anahtarı.</summary>
    public Guid? ApiKeyId { get; set; }
}

public class WebhookDelivery : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid WebhookId { get; set; }
    public string EventType { get; set; } = "";
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    /// <summary>Dalga 12: teslim edilen olay (governance_events.Id; ping için rastgele).</summary>
    public Guid? EventId { get; set; }
    /// <summary>Aynı olayın bu uca kaçıncı deneme gönderimi (1 = ilk).</summary>
    public int Attempt { get; set; } = 1;
    /// <summary>Otomatik yeniden deneme zamanı (üstel geri çekilme); RetryState = pending iken dolu.</summary>
    public DateTime? NextRetryAt { get; set; }
    /// <summary>null | pending | retrying | retried | gave_up | resent</summary>
    public string? RetryState { get; set; }
    /// <summary>İK ekranından elle yeniden gönderildi.</summary>
    public bool Manual { get; set; }
    public Guid? ParentDeliveryId { get; set; }
    public string? TriggeredByName { get; set; }
}

public class ApiKey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>Dalga 12: son kullanma (null = süresiz).</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Döndürme (rotate) ile yerini aldığı eski anahtar.</summary>
    public Guid? RotatedFromId { get; set; }
    public long UsageCount { get; set; }
    public string? LastUsedScope { get; set; }
}

public class Integration : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>Slack | Teams</summary>
    public string Kind { get; set; } = "Slack";
    public string Name { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public List<string> Events { get; set; } = new();
    /// <summary>Slack slash komutu imza doğrulaması için (isteğe bağlı).</summary>
    public string? SigningSecret { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int? LastStatus { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/* ======================================================== Takvim + bilgi bankası */

public class CalendarFeed : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class KbArticle : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================================ Sohbet uygulamaları (Slack / Teams botu) */

/// <summary>
/// Kiracının Slack uygulaması ya da Microsoft Teams botu. Gelen webhook'tan
/// (governance_integrations) farkı: kişiye özel mesaj, onay düğmeleri ve komutlar.
/// Sırlar SecretBox ile şifreli tutulur.
/// </summary>
public class ChatApp : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>Slack | Teams</summary>
    public string Platform { get; set; } = "Slack";
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool NotifyApprovals { get; set; } = true;
    public bool NotifyRequesters { get; set; } = true;
    // Slack
    public string? SlackTeamId { get; set; }
    public string? SlackTeamName { get; set; }
    public string? SlackBotUserId { get; set; }
    public string? SlackBotTokenEnc { get; set; }
    public string? SlackSigningSecretEnc { get; set; }
    // Teams (Azure Bot, tek kiracılı)
    public string? TeamsAppId { get; set; }
    public string? TeamsAppPasswordEnc { get; set; }
    public string? TeamsAzureTenantId { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Sohbet hesabı, kişi HR360'a bir kez giriş yapıp bağlamayı onaylamadan veri almaz
    /// (yalnızca e-posta eşleşmesine güvenilmez).
    /// </summary>
    public bool RequireVerifiedIdentity { get; set; } = true;
    /// <summary>Minimal: onay mesajında ad kısaltılır, konu yazılmaz. Standard: tam bilgi.</summary>
    public string MessageDetail { get; set; } = "Minimal";
    /// <summary>Hafta içi sabah özeti (kararı bekleyen talepler, ekipten izinde olanlar).</summary>
    public bool DailyDigest { get; set; } = true;
    // Dalga 5e: Mattermost / Rocket.Chat (kendi sunucunuzda) — sunucu adresi, bot jetonu, gelen istek jetonu.
    public string? ServerUrl { get; set; }
    public string? BotTokenEnc { get; set; }
    /// <summary>Mattermost: botun kullanıcı kimliği. Rocket.Chat: X-User-Id.</summary>
    public string? BotUserId { get; set; }
    /// <summary>Mattermost slash komutu / giden webhook jetonu ya da Rocket.Chat giden entegrasyon jetonu.</summary>
    public string? IncomingTokenEnc { get; set; }
    /// <summary>BG20: bu uygulamada kapatılan komut/özellik anahtarları.</summary>
    public List<string> DisabledFeatures { get; set; } = new();
    /// <summary>Duyuru ve kutlama kanalı (Slack kanal kimliği, Teams konuşma kimliği, Mattermost kanal kimliği, Rocket.Chat oda kimliği).</summary>
    public string? ChannelId { get; set; }
    public bool CelebrationsEnabled { get; set; }
    /// <summary>BG6: kişinin sessiz saatlerinde kritik olmayan bot mesajları ertelenir.</summary>
    public bool RespectQuietHours { get; set; } = true;
    /// <summary>BG10: düğmeler bu kadar gün sonra geçersizdir.</summary>
    public int ButtonTtlDays { get; set; } = 7;
}

/// <summary>Gönderilemeyen sohbet mesajı; arka planda artan aralıklarla yeniden denenir.</summary>
public class ChatOutbox : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid AppId { get; set; }
    /// <summary>approval | text</summary>
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Sohbet hesabı ↔ çalışan eşleşmesi (e-posta ile önerilir, kişi HR360'ta onaylar).</summary>
public class ChatIdentity : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid AppId { get; set; }
    public string Platform { get; set; } = "Slack";
    public string ExternalUserId { get; set; } = "";
    public Guid? EmployeeId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>Slack: DM kanal kimliği. Teams: 1:1 konuşma kimliği.</summary>
    public string? ConversationId { get; set; }
    /// <summary>Teams: Bot Connector adresi (konuşmaya özel).</summary>
    public string? ServiceUrl { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Kişi HR360'a giriş yapıp bu sohbet hesabını kendisine bağladı.</summary>
    public DateTime? VerifiedAt { get; set; }
    public string? LinkCodeHash { get; set; }
    public DateTime? LinkCodeExpiresAt { get; set; }
    public DateOnly? LastDigestOn { get; set; }
}

/// <summary>Gönderilen onay mesajı; karar verilince mesaj güncellenir.</summary>
public class ChatMessage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid AppId { get; set; }
    public string Platform { get; set; } = "Slack";
    public Guid WorkflowRequestId { get; set; }
    public Guid StepId { get; set; }
    public Guid RecipientEmployeeId { get; set; }
    public string ConversationId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string? ServiceUrl { get; set; }
    /// <summary>Open | Decided | Closed</summary>
    public string State { get; set; } = "Open";
    public string? Subject { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/* ============================================================ Takvim ve toplantı entegrasyonu */

/// <summary>Kiracının Google / Microsoft 365 / Zoom uygulama bilgileri (OAuth istemcisi).</summary>
public class ProviderConfig : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>Google | Microsoft | Zoom</summary>
    public string Provider { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecretEnc { get; set; }
    /// <summary>Microsoft: Entra ID kiracı kimliği ya da "organizations".</summary>
    public string? MsTenant { get; set; }
    /// <summary>Zoom: Server-to-Server OAuth hesap kimliği.</summary>
    public string? ZoomAccountId { get; set; }
    /// <summary>Zoom: düzenleyicinin Zoom hesabı yoksa toplantının açılacağı kullanıcı.</summary>
    public string? ZoomDefaultHost { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Bir çalışanın bağladığı Google / Outlook takvimi.</summary>
public class CalendarConnection : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string UserId { get; set; } = "";
    /// <summary>Google | Microsoft</summary>
    public string Provider { get; set; } = "";
    public string? AccountEmail { get; set; }
    public string? AccessTokenEnc { get; set; }
    public string? RefreshTokenEnc { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool SyncLeaves { get; set; } = true;
    /// <summary>Active | Error</summary>
    public string Status { get; set; } = "Active";
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Bekleyen OAuth yetkilendirmesi (state + PKCE doğrulayıcısı), 10 dk geçerli.</summary>
public class OAuthState : ITenantOwned
{
    public string State { get; set; } = "";
    public string TenantSlug { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public string UserId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

/// <summary>HR360 kaydı ↔ dış takvimdeki etkinlik (silme/güncelleme için).</summary>
public class CalendarEventLink : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public Guid ConnectionId { get; set; }
    /// <summary>leave | meeting</summary>
    public string SourceType { get; set; } = "";
    public Guid SourceId { get; set; }
    public string ExternalEventId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>1:1, mülakat ya da serbest toplantı; Zoom / Teams / Google Meet bağlantısıyla.</summary>
public class Meeting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>one-on-one | interview | custom</summary>
    public string SourceType { get; set; } = "custom";
    public Guid? SourceId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime StartsAt { get; set; }
    public int DurationMinutes { get; set; } = 30;
    /// <summary>zoom | teams | google | none</summary>
    public string Provider { get; set; } = "none";
    public string? JoinUrl { get; set; }
    public string? ExternalMeetingId { get; set; }
    public Guid OrganizerEmployeeId { get; set; }
    public List<Guid> ParticipantEmployeeIds { get; set; } = new();
    public List<string> ExternalEmails { get; set; } = new();
    /// <summary>Scheduled | Cancelled</summary>
    public string Status { get; set; } = "Scheduled";
    public string? Warnings { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/* ============================================================ Yapay zekâ (LLM) */

/// <summary>Kiracının LLM kullanım izni. Varsayılan kapalı (KVKK: veri dışarı çıkabilir).</summary>
public class AiSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Performans özeti gibi kişisel veri içeren isteklere izin (ad yine takma adla gönderilir).</summary>
    public bool AllowPersonalData { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>LLM çağrı kaydı (içerik tutulmaz): hesap verebilirlik ve kota için.</summary>
public class AiUsage : ITenantOwned
{
    public long Id { get; set; }
    public string TenantSlug { get; set; } = "";
    public string? UserId { get; set; }
    public string Task { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}


/* ======================================================= KVKK operasyonları */

/// <summary>Aydınlatma metni ya da açık rıza metninin kiracıya özel sürümü (K2).</summary>
public class PrivacyNotice : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    /// <summary>Onay tipi kodu (KVKK_AYDINLATMA, ACIK_RIZA_SAGLIK, ...).</summary>
    public string Type { get; set; } = "KVKK_AYDINLATMA";
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string? ChangeNote { get; set; }
    public string PublishedBy { get; set; } = "";
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Kişisel veri ihlali kaydı (K3): 72 saatte Kurul'a bildirim, ilgililere bilgilendirme.</summary>
public class DataBreach : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? OccurredAt { get; set; }
    public string? DataCategories { get; set; }
    public int? AffectedCount { get; set; }
    /// <summary>Etkilenen çalışanlar (JSON dizi) — bilgilendirme için.</summary>
    public string AffectedEmployeesJson { get; set; } = "[]";
    /// <summary>Low | Medium | High</summary>
    public string Severity { get; set; } = "Medium";
    public string? Cause { get; set; }
    public string? Measures { get; set; }
    /// <summary>Open | Reported | Closed</summary>
    public string Status { get; set; } = "Open";
    public DateTime? ReportedToBoardAt { get; set; }
    public string? BoardReference { get; set; }
    public DateTime? SubjectsNotifiedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Gizlilik etki kontrol listesi (K9): yeni entegrasyon, özel alan ya da süreç öncesi.</summary>
public class PrivacyAssessment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantSlug { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Integration | CustomField | Process</summary>
    public string Kind { get; set; } = "Integration";
    /// <summary>Entegrasyon ise sağlayıcı anahtarı (slack, google...).</summary>
    public string? ProviderKey { get; set; }
    /// <summary>{"soruKodu": {"answer": "yes|no|na", "note": "..."}}</summary>
    public string AnswersJson { get; set; } = "{}";
    /// <summary>Low | Medium | High (yanıtlardan hesaplanır)</summary>
    public string Risk { get; set; } = "Low";
    /// <summary>Draft | Approved</summary>
    public string Status { get; set; } = "Draft";
    public string CreatedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
