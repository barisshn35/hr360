-- Dalga 11 / madde 76: iş teklifi mektubunun aday tarafından basit elektronik imzayla kabulü. İdempotent.
-- Kod, imza ve kanıt governance-service'teki TEK imza motorundadır (DocumentType 'OfferLetter');
-- recruitment-service iç uçlarla (X-Internal-Token) çağırır. İmzalayan çalışan değil ADAYDIR:
-- governance_signatures."SignerEmployeeId" bu satırlarda aday kimliğini taşır, "SignerKind" = 'Candidate'.
-- Canlıya: docker exec -i hr360-postgres-1 psql -v ON_ERROR_STOP=1 -q -U hr360admin -d hr360_operational < bu_dosya

-- ---------------------------------------------------------------- governance: imzalayan türü
-- Mevcut satırlar çalışan imzasıdır (varsayılan). Kanıt satırları değiştirilemez (BEFORE UPDATE tetikleyicisi);
-- sütun ekleme UPDATE sayılmaz.
ALTER TABLE governance_signatures ADD COLUMN IF NOT EXISTS "SignerKind" text NOT NULL DEFAULT 'Employee';

-- ---------------------------------------------------------------- recruitment: imza bağlantısı ve imzalı belge
-- SignTokenHash: aday imza bağlantısı jetonunun SHA-256 özeti (jeton saklanmaz; İK yeniler/iptal eder).
-- LetterSha256: imzalanan mektup metninin özeti (kanıttaki belge özeti).
-- SignedLetterHtml: imzalı belge (mektup + kanıt bloğu; ücret içerir); SignedDocumentSha256 onun özeti.
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignTokenHash" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignTokenCreatedAt" timestamp with time zone NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "LetterSha256" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignedLetterHtml" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignedDocumentSha256" text NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignatureEvidenceId" uuid NULL;
ALTER TABLE recruitment_offers ADD COLUMN IF NOT EXISTS "SignedAt" timestamp with time zone NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_recruitment_offers_sign_token"
    ON recruitment_offers ("SignTokenHash") WHERE "SignTokenHash" IS NOT NULL;

-- Saklama bağı: teklif silinince (aday KVKK silme talebi / imha — aday kaydından CASCADE) kodlar ve kanıt da silinir.
-- SECURITY DEFINER: servis başına rollerde recruitment rolüne governance tablolarında yetki verilmez; fonksiyon
-- yalnızca bu iki DELETE'i, silinen teklifin kimliğiyle yapar (search_path sabit).
CREATE OR REPLACE FUNCTION governance_signatures_cascade_offer_letter() RETURNS trigger
    SECURITY DEFINER SET search_path = public AS $$
BEGIN
    DELETE FROM governance_signature_otps WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = OLD."Id";
    DELETE FROM governance_signatures WHERE "DocumentType" = 'OfferLetter' AND "DocumentId" = OLD."Id";
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS trg_governance_signatures_cascade_offer_letter ON recruitment_offers;
CREATE TRIGGER trg_governance_signatures_cascade_offer_letter
    AFTER DELETE ON recruitment_offers
    FOR EACH ROW EXECUTE FUNCTION governance_signatures_cascade_offer_letter();
