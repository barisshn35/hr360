-- Dalga 12 (madde 87, 89, 90): kullanıcı başına arayüz tercihleri.
--  87) liste ekranlarında kayıtlı filtre/görünümler   (anahtar: views:<tablo>)
--  89) kişiselleştirilebilir ana panel (widget sırası/gizli)  (anahtar: dashboard)
--  90) "Yenilikler" paneli: en son görülen sürüm notu        (anahtar: whatsnew)
-- Sahibi notification-service (kişi tercihlerinin sahibi). Kişi, Keycloak kimliğiyle ("UserSub")
-- tutulur: çalışan kaydı olmayan hesaplar (ör. İK yöneticisi) de tercih saklayabilir.
-- KVKK: yalnızca kişinin kendi arayüz ayarı; başkasına ait kişisel veri yazılmaz (değer 16 KB ile sınırlı).
-- İdempotent; mevcut veriyi değiştirmez.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

CREATE TABLE IF NOT EXISTS notification_ui_prefs (
    "Id"         uuid PRIMARY KEY,
    "TenantSlug" character varying(64) NOT NULL,
    "UserSub"    character varying(64) NOT NULL,
    "Key"        character varying(100) NOT NULL,
    "Value"      jsonb NOT NULL,
    "UpdatedAt"  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_notification_ui_prefs_Owner_Key" ON notification_ui_prefs ("TenantSlug", "UserSub", "Key");

-- Yeni tablolara kiracı yalıtım politikası (RLS isteğe bağlı; açık değilse davranış değişmez).
DO $$ BEGIN
    IF to_regprocedure('hr360_rls_apply_policies()') IS NOT NULL THEN PERFORM hr360_rls_apply_policies(); END IF;
END $$;
