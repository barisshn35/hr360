-- Güvenlik dalgası 3 / 19: kiracı yalıtımı için satır düzeyi güvenlik (RLS) — İSTEĞE BAĞLI.
-- İdempotent. Bu dosya RLS'Yİ AÇMAZ: yalnızca politikaları ve aç/kapa fonksiyonlarını oluşturur.
-- Politika olan ama RLS'si kapalı tabloda davranış değişmez.
--
-- Tasarım:
--   * "TenantSlug" sütunu olan her public tablosunda hr360_tenant_isolation politikası.
--   * Oturum değişkeni app.tenant (SET app.tenant = 'demo' ya da set_config('app.tenant', 'demo', true)).
--     Tanımsız/boşsa politika HER satırı gösterir (izin verici): RLS servisler değişkeni ayarlamadan
--     açılsa bile hiçbir şey kırılmaz. Tanımlıysa yalnızca o kiracının satırları okunur/yazılır.
--   * Tablo sahibi (hr360admin) RLS'den muaftır (FORCE kullanılmaz); politika yalnızca servis başına
--     rollerle (deploy/postgres/roles.sql) bağlanıldığında etkilidir.
--   * Açmak:   SELECT hr360_rls_enable();    Kapatmak: SELECT hr360_rls_disable();
--     Durum:   SELECT * FROM hr360_rls_status();
--   * Görünümler (analytics_*) sahibinin yetkisiyle çalışır; RLS'yi atlar.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- "TenantSlug" sütunu olup politikası olmayan tablolara politikayı ekler; eklenen sayısını döner.
CREATE OR REPLACE FUNCTION hr360_rls_apply_policies() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    FOR t IN
        SELECT c.relname
          FROM pg_class c
          JOIN pg_namespace ns ON ns.oid = c.relnamespace
          JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'TenantSlug' AND NOT a.attisdropped
         WHERE ns.nspname = 'public' AND c.relkind IN ('r', 'p')
           AND NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'hr360_tenant_isolation')
         ORDER BY c.relname
    LOOP
        -- Yalnızca eksik politika eklenir (var olanlara dokunulmaz: çalışan sistemde gereksiz tablo kilidi yok).
        EXECUTE format($p$CREATE POLICY hr360_tenant_isolation ON public.%I AS PERMISSIVE FOR ALL
            USING (coalesce(current_setting('app.tenant', true), '') = '' OR "TenantSlug" = current_setting('app.tenant', true))
            WITH CHECK (coalesce(current_setting('app.tenant', true), '') = '' OR "TenantSlug" = current_setting('app.tenant', true))$p$,
            t.relname);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

-- Politikası olan tablolarda RLS'yi açar (önce eksik politikaları tamamlar). Açılan tablo sayısını döner.
CREATE OR REPLACE FUNCTION hr360_rls_enable() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    PERFORM hr360_rls_apply_policies();
    FOR t IN
        SELECT c.relname FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
         WHERE p.polname = 'hr360_tenant_isolation' AND c.relnamespace = 'public'::regnamespace
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t.relname);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

CREATE OR REPLACE FUNCTION hr360_rls_disable() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; n integer := 0;
BEGIN
    FOR t IN
        SELECT c.relname FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
         WHERE p.polname = 'hr360_tenant_isolation' AND c.relnamespace = 'public'::regnamespace AND c.relrowsecurity
    LOOP
        EXECUTE format('ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY', t.relname);
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

CREATE OR REPLACE FUNCTION hr360_rls_status() RETURNS TABLE (table_name text, rls_enabled boolean, has_policy boolean)
LANGUAGE sql STABLE AS $$
    SELECT c.relname::text, c.relrowsecurity,
           EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid AND p.polname = 'hr360_tenant_isolation')
      FROM pg_class c
      JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'TenantSlug' AND NOT a.attisdropped
     WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p')
     ORDER BY 1
$$;

-- Yalnızca yönetici çağırabilsin (servis rolleri RLS'yi kapatamasın).
REVOKE ALL ON FUNCTION hr360_rls_apply_policies() FROM PUBLIC;
REVOKE ALL ON FUNCTION hr360_rls_enable() FROM PUBLIC;
REVOKE ALL ON FUNCTION hr360_rls_disable() FROM PUBLIC;

DO $$ BEGIN PERFORM hr360_rls_apply_policies(); END $$;
