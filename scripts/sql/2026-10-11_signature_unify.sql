-- Y28 — tek imza motoru (governance-service). İdempotent.
-- governance_signatures / governance_signature_otps artık belge türünden bağımsız:
--   DocumentType = 'DocumentRequest' (düzenlenmiş belge talepleri, governance)
--   DocumentType = 'HrDocument'      (İK özlük dokümanları, expense_documents; expense-service iç uçlarla çağırır)
-- expense_signature_evidence bu değişiklikten önceki imzalar için SALT OKUNUR kalır.

-- ---------------------------------------------------------------- kodlar
ALTER TABLE governance_signature_otps ADD COLUMN IF NOT EXISTS "DocumentType" text NOT NULL DEFAULT 'DocumentRequest';
CREATE INDEX IF NOT EXISTS "IX_governance_signature_otps_type_doc"
    ON governance_signature_otps ("TenantSlug", "DocumentType", "DocumentId", "CreatedAt");

-- ---------------------------------------------------------------- kanıtlar
ALTER TABLE governance_signatures ADD COLUMN IF NOT EXISTS "Title" text NULL;
CREATE INDEX IF NOT EXISTS "IX_governance_signatures_signer" ON governance_signatures ("TenantSlug", "SignerEmployeeId", "SignedAt");
CREATE INDEX IF NOT EXISTS "IX_governance_signatures_type_doc" ON governance_signatures ("TenantSlug", "DocumentType", "DocumentId");
-- Benzersizlik belge türünü de kapsar (aynı belge sürümünü aynı kişi bir kez imzalar).
DROP INDEX IF EXISTS "UX_governance_signatures_doc";
CREATE UNIQUE INDEX IF NOT EXISTS "UX_governance_signatures_type_doc"
    ON governance_signatures ("DocumentType", "DocumentId", "DocumentVersion", "SignerEmployeeId");

-- Belge talebine bağlı yabancı anahtarlar kaldırılır (HrDocument kimlikleri expense_documents'tadır).
-- Saklama bağı tetikleyicilerle korunur: belge silinince (saklama sonu imha) kodlar ve kanıt da silinir.
ALTER TABLE governance_signatures DROP CONSTRAINT IF EXISTS "governance_signatures_DocumentId_fkey";
ALTER TABLE governance_signature_otps DROP CONSTRAINT IF EXISTS "governance_signature_otps_DocumentId_fkey";

CREATE OR REPLACE FUNCTION governance_signatures_cascade_document_request() RETURNS trigger AS $$
BEGIN
    DELETE FROM governance_signature_otps WHERE "DocumentType" = 'DocumentRequest' AND "DocumentId" = OLD."Id";
    DELETE FROM governance_signatures WHERE "DocumentType" = 'DocumentRequest' AND "DocumentId" = OLD."Id";
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_cascade_document_request ON governance_document_requests;
CREATE TRIGGER trg_governance_signatures_cascade_document_request
    AFTER DELETE ON governance_document_requests
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_cascade_document_request();

CREATE OR REPLACE FUNCTION governance_signatures_cascade_hr_document() RETURNS trigger AS $$
BEGIN
    DELETE FROM governance_signature_otps WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = OLD."Id";
    DELETE FROM governance_signatures WHERE "DocumentType" = 'HrDocument' AND "DocumentId" = OLD."Id";
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_cascade_hr_document ON expense_documents;
CREATE TRIGGER trg_governance_signatures_cascade_hr_document
    AFTER DELETE ON expense_documents
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_cascade_hr_document();

-- Kanıt satırı değiştirilemez (silme yalnızca saklama sonu imha için serbest).
CREATE OR REPLACE FUNCTION governance_signatures_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'governance_signatures kayıtları değiştirilemez';
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_immutable ON governance_signatures;
CREATE TRIGGER trg_governance_signatures_immutable
    BEFORE UPDATE ON governance_signatures
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_immutable();

-- ---------------------------------------------------------------- expense: talep → governance kanıtı
-- Yeni imzalarda kanıt governance_signatures'tadır (DocumentType 'HrDocument'); talep yalnızca kimliğini tutar.
-- OTP sütunları (OtpHash, OtpExpiresAt, ...) artık yazılmaz; eski satırlar için yerinde bırakılır.
ALTER TABLE expense_document_signatures ADD COLUMN IF NOT EXISTS "EvidenceRef" uuid NULL;
