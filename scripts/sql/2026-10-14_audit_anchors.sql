-- Güvenlik dalgası 1: denetim zincirinin gecelik çapası (governance AuditChainGuard).
-- Kiracı başına her gece zincir başı (son sıra no + özet) yazılır; sonraki kontrolde önceki çapa
-- satırının hâlâ aynı özetle durduğu ve zincirin geriye gitmediği denetlenir.
-- Platform düzeyi kayıtlar (TenantSlug NULL) için "TenantSlug" = ''.
CREATE TABLE IF NOT EXISTS governance_audit_anchors (
    "Id" uuid PRIMARY KEY,
    "TenantSlug" varchar(64) NOT NULL,
    "CheckedAt" timestamptz NOT NULL DEFAULT now(),
    "Ok" boolean NOT NULL,
    "Rows" bigint NOT NULL DEFAULT 0,
    "FromSeq" bigint,
    "ToSeq" bigint,
    "HeadHash" text,
    "Problem" text
);
CREATE INDEX IF NOT EXISTS "IX_governance_audit_anchors_Tenant" ON governance_audit_anchors ("TenantSlug", "CheckedAt" DESC);
