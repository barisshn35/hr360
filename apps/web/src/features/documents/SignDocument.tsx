import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { useQueryClient } from '@tanstack/react-query'
import { platformGovApi, type SignatureEvidence } from '@/api/platformGov'
import { tx } from '@/lib/i18n'
import { OtpSignPanel, SignatureEvidenceDetails, SimpleSignatureDisclaimer, type UnifiedEvidence } from './OtpSignPanel'

/** Y28 — her imza ekranında görünen yasal uyarı (tek metin; OtpSignPanel ile ortak). */
export const SimpleSignatureNotice = SimpleSignatureDisclaimer

/** Governance kanıtı → ortak kanıt görünümü. */
export const fromGovEvidence = (e: SignatureEvidence): UnifiedEvidence => ({
  signedAt: e.signedAt, method: e.method, documentSha256: e.documentSha256, evidenceSha256: e.evidenceSha256, ipPrefix: e.ipPrefix,
  integrityOk: e.integrityOk, documentId: e.documentId, documentVersion: e.documentVersion, title: e.title,
})

export function EvidenceView({ e }: { e: SignatureEvidence }) {
  return <SignatureEvidenceDetails e={fromGovEvidence(e)} />
}

/** Belgeyi tek kullanımlık kodla imzalama: kod iste → kodu gir → kanıt. */
export function SignDocumentModal({ docId, name, onClose }: { docId: string; name: string; onClose: () => void }) {
  const qc = useQueryClient()
  return (
    <Modal open onClose={onClose} title={tx('Kodla imzala: {0}', [name])} size="lg"
      footer={<Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button>}>
      <div className="space-y-3">
        <SimpleSignatureDisclaimer />
        <OtpSignPanel
          chooseChannel
          onRequest={(channel) => platformGovApi.requestSignOtp(docId, channel)}
          onSign={(c, code) => platformGovApi.sign(docId, c.otpId!, code)}
          toEvidence={fromGovEvidence}
          onSigned={() => {
            void qc.invalidateQueries({ queryKey: ['doc-requests'] })
            void qc.invalidateQueries({ queryKey: ['my-signatures'] })
          }}
        />
      </div>
    </Modal>
  )
}
