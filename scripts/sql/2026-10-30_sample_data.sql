-- Örnek veri (yenilik haritası #35): boş bir şirketi tek tıkla örnek çalışan, departman, takdir,
-- fikir ve etkinlikle doldurma; sonra tek tıkla temizleme. Sahibi tenant-service. Burada yalnızca
-- örnek verinin hangi departmanları açtığı tutulur (temizlikte yalnızca bunlar silinir). İdempotent.
CREATE TABLE IF NOT EXISTS platform_sample_data (
    "TenantSlug"     character varying(64) PRIMARY KEY,
    "DepartmentIds"  uuid[] NOT NULL DEFAULT '{}',
    "CreatedBy"      text,
    "CreatedAt"      timestamptz NOT NULL DEFAULT now()
);
