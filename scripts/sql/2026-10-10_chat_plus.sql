-- Dalga 5e: sohbet botu özellikleri (B6–B20, BG4–BG20).
-- Mattermost / Rocket.Chat sağlayıcıları, komut/özellik yönetimi, bekleyen işlemler (fiş, adım
-- doğrulaması, toplu onay, İK vakası), konuşma bağlamı (30 gün, şifreli), kullanım sayaçları,
-- iş tekrarı önleme, nabız anketi (anonim yanıt + ayrı "yanıtladı" kaydı), çıkış anketi ilerlemesi,
-- yıldönümü kutlaması izni. Tekrar çalıştırılabilir.

-- Sağlayıcıdan bağımsız alanlar (Mattermost / Rocket.Chat) ve yönetim ayarları.
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "ServerUrl" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "BotTokenEnc" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "BotUserId" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "IncomingTokenEnc" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "DisabledFeatures" text[] NOT NULL DEFAULT '{}';
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "ChannelId" text;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "CelebrationsEnabled" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "RespectQuietHours" boolean NOT NULL DEFAULT true;
ALTER TABLE governance_chat_apps ADD COLUMN IF NOT EXISTS "ButtonTtlDays" integer NOT NULL DEFAULT 7;

-- Onay bekleyen sohbet işlemleri: fiş önerisi, izin iptali onayı, toplu onay, adım doğrulaması (BG13),
-- İK vakası önerisi. Yük şifreli (enc1:), kısa ömürlü; tek kullanımlık kodun yalnızca özeti tutulur.
CREATE TABLE IF NOT EXISTS governance_chat_pending (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Kind" text NOT NULL,
    "PayloadEnc" text NOT NULL,
    "State" text NOT NULL DEFAULT 'Pending',
    "CodeHash" text,
    "OtpHash" text,
    "OtpExpiresAt" timestamptz,
    "Summary" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "ExpiresAt" timestamptz NOT NULL,
    "ConfirmedAt" timestamptz
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_pending_emp" ON governance_chat_pending ("TenantSlug", "EmployeeId", "State");
CREATE INDEX IF NOT EXISTS "IX_governance_chat_pending_code" ON governance_chat_pending ("CodeHash");

-- BG16 asistan konuşma bağlamı: son N tur, 30 gün, şifreli; "geçmişimi sil" ile silinir.
CREATE TABLE IF NOT EXISTS governance_chat_context (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Role" text NOT NULL,
    "TextEnc" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_context_emp" ON governance_chat_context ("TenantSlug", "EmployeeId", "CreatedAt");

-- BG20 kullanım sayaçları (yalnızca sayı; kişi ya da mesaj yok) ve son hatalar.
CREATE TABLE IF NOT EXISTS governance_chat_usage (
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "Day" date NOT NULL,
    "Feature" text NOT NULL,
    "Outcome" text NOT NULL,
    "Count" integer NOT NULL DEFAULT 0,
    PRIMARY KEY ("TenantSlug", "AppId", "Day", "Feature", "Outcome")
);
CREATE TABLE IF NOT EXISTS governance_chat_errors (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "AppId" uuid NOT NULL,
    "Feature" text NOT NULL,
    "Message" text NOT NULL,
    "At" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_governance_chat_errors_app" ON governance_chat_errors ("AppId", "At" DESC);

-- Zamanlanmış bot mesajlarının tekrarını önler (karşılama, kontrol listesi, kutlama, hatırlatma...).
CREATE TABLE IF NOT EXISTS governance_chat_job_log (
    "TenantSlug" character varying(64) NOT NULL,
    "Key" text NOT NULL,
    "SentAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("TenantSlug", "Key")
);

-- B12 nabız anketi: soru engagement_surveys'te (Kind = Pulse, anonim); yanıt engagement_survey_responses'a
-- kimliksiz yazılır. Burada yalnızca gönderim planı ve "yanıtladı" bilgisi (tekrarı önlemek için) tutulur.
CREATE TABLE IF NOT EXISTS governance_chat_pulses (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "SurveyId" uuid NOT NULL,
    "Question" text NOT NULL,
    "SendAt" timestamptz NOT NULL,
    "ClosesAt" timestamptz,
    "Status" text NOT NULL DEFAULT 'Scheduled',
    "SentCount" integer NOT NULL DEFAULT 0,
    "CreatedBy" text NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS governance_chat_pulse_answered (
    "TenantSlug" character varying(64) NOT NULL,
    "PulseId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "AnsweredOn" date NOT NULL,
    PRIMARY KEY ("PulseId", "EmployeeId")
);

-- B20 çıkış anketi ilerlemesi (yanıtlar şifreli; bitince ayrılış kaydına yazılır ve bu satırın yanıtları silinir).
CREATE TABLE IF NOT EXISTS governance_chat_exit_progress (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "CaseId" uuid NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "Step" integer NOT NULL DEFAULT 0,
    "AnswersEnc" text,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "CompletedAt" timestamptz,
    UNIQUE ("CaseId")
);

-- B11 yıldönümü kutlaması için açık izin (doğum günü için engagement_profiles."ShowBirthday" kullanılır).
CREATE TABLE IF NOT EXISTS governance_chat_optins (
    "TenantSlug" character varying(64) NOT NULL,
    "EmployeeId" uuid NOT NULL,
    "ShowAnniversary" boolean NOT NULL DEFAULT false,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("TenantSlug", "EmployeeId")
);
