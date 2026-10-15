-- Org senaryo taslaklarının sahibi (Keycloak kullanıcı kimliği). Yönetici yalnızca kendi taslaklarını
-- görür/düzenler; İK tümünü. Eski satırlarda sahip bilinmez (NULL): yalnızca İK görür.
ALTER TABLE engagement_org_scenarios ADD COLUMN IF NOT EXISTS "CreatedByUserId" text NULL;
CREATE INDEX IF NOT EXISTS "IX_engagement_org_scenarios_Tenant_Owner"
    ON engagement_org_scenarios ("TenantSlug", "CreatedByUserId");
