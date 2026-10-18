-- Satır düzeyi güvenlik: kiracılar arası ORTAK satırlar (TenantSlug NULL). İdempotent.
-- Bazı tablolarda NULL kiracı "herkese ortak" demektir (ör. expense_fx_rates: TCMB kur önbelleği; kiracının
-- elle girdiği kur kendi TenantSlug'ıyla durur). Kiracı oturumunda (app.tenant dolu) bu satırlar okunabilmeli
-- ve önbelleğe yazılabilmeli. İstisna YALNIZCA aşağıdaki listedeki tablolar içindir; diğerlerinde NULL satır
-- (ör. audit_log'daki platform kayıtları) kiracı oturumuna kapalı kalır.

CREATE OR REPLACE FUNCTION hr360_rls_shared_tables() RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
    SELECT ARRAY['expense_fx_rates']::text[]
$$;

CREATE OR REPLACE FUNCTION hr360_rls_expr(tbl text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
    SELECT $e$coalesce(current_setting('app.tenant', true), '') = '' OR "TenantSlug" = current_setting('app.tenant', true)$e$
        || CASE WHEN tbl = ANY (hr360_rls_shared_tables()) THEN $e$ OR "TenantSlug" IS NULL$e$ ELSE '' END
$$;

-- Eksik politikaları ekler (ifade tabloya göre: ortak tablolarda NULL satır da görünür).
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
        EXECUTE format('CREATE POLICY hr360_tenant_isolation ON public.%I AS PERMISSIVE FOR ALL USING (%s) WITH CHECK (%s)',
            t.relname, hr360_rls_expr(t.relname), hr360_rls_expr(t.relname));
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

-- Var olan politikaları listeye göre güncelle (yalnızca ortak tablolar; diğerleri aynen kalır).
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY hr360_rls_shared_tables() LOOP
        IF EXISTS (SELECT 1 FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
                    WHERE p.polname = 'hr360_tenant_isolation' AND c.relname = t AND c.relnamespace = 'public'::regnamespace) THEN
            EXECUTE format('ALTER POLICY hr360_tenant_isolation ON public.%I USING (%s) WITH CHECK (%s)', t, hr360_rls_expr(t), hr360_rls_expr(t));
        END IF;
    END LOOP;
END $$;

REVOKE ALL ON FUNCTION hr360_rls_apply_policies() FROM PUBLIC;
