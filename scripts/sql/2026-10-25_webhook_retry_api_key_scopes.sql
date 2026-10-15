-- ===================================================================
-- Dalga 12 (madde 93-94): webhook teslimat yeniden denemesi ve kapsamlı API anahtarları
--  * governance_webhook_deliveries: olay kimliği, deneme sırası, sonraki deneme zamanı
--    (üstel geri çekilme), yeniden deneme durumu ve elle yeniden gönderim izi.
--    Yük (payload) teslimat tablosunda TUTULMAZ: yeniden gönderimde governance_events
--    (30 gün saklanır, kişisel alanları ayıklanmış kopya) kullanılır — veri en aza indirme.
--  * governance_api_keys: son kullanma, döndürme (rotate) zinciri, toplam kullanım,
--    son kullanılan yetki.
--  * governance_api_key_usage: anahtar × gün × yetki başına istek/ret sayacı
--    (IP veya istek içeriği tutulmaz). 180 günden eskiler bakım işinde silinir.
-- İdempotent.
-- ===================================================================

ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "EventId" uuid;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "Attempt" integer NOT NULL DEFAULT 1;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "NextRetryAt" timestamptz;
-- null: başarılı/yeniden denenmeyecek · pending: sırada · retrying: işleniyor · retried: yeni deneme yapıldı
-- gave_up: deneme hakkı bitti/uç kapalı · resent: elle yeniden gönderildi
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "RetryState" character varying(16);
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "Manual" boolean NOT NULL DEFAULT false;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "ParentDeliveryId" uuid;
ALTER TABLE governance_webhook_deliveries ADD COLUMN IF NOT EXISTS "TriggeredByName" text;

CREATE INDEX IF NOT EXISTS "IX_governance_webhook_deliveries_Retry"
    ON governance_webhook_deliveries ("NextRetryAt") WHERE "RetryState" = 'pending';
CREATE INDEX IF NOT EXISTS "IX_governance_webhook_deliveries_Failed"
    ON governance_webhook_deliveries ("TenantSlug", "OccurredAt" DESC) WHERE "Error" IS NOT NULL;

ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "ExpiresAt" timestamptz;
ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "RotatedFromId" uuid;
ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "UsageCount" bigint NOT NULL DEFAULT 0;
ALTER TABLE governance_api_keys ADD COLUMN IF NOT EXISTS "LastUsedScope" character varying(64);

CREATE TABLE IF NOT EXISTS governance_api_key_usage (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "KeyId" uuid NOT NULL,
    "Day" date NOT NULL,
    "Scope" character varying(64) NOT NULL,
    "Count" integer NOT NULL DEFAULT 0,
    "DeniedCount" integer NOT NULL DEFAULT 0,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_api_key_usage" ON governance_api_key_usage ("KeyId", "Day", "Scope");
CREATE INDEX IF NOT EXISTS "IX_governance_api_key_usage_Tenant" ON governance_api_key_usage ("TenantSlug", "Day");
