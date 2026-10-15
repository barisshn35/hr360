import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { BadgeCheck, FileCheck2, FileSignature, Send } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { TextAreaField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { docSignatureApi, type SignatureEvidenceView, type SignatureRequestView, type SignatureStatus } from '@/api/docSignature'
import { documentTypeLabels, type DocumentType, type HrDocument } from '@/api/expense'
import { platformGovApi, type SignatureEvidence } from '@/api/platformGov'
import { formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { OtpSignPanel, SignatureEvidenceDetails, SimpleSignatureDisclaimer, type UnifiedEvidence } from './OtpSignPanel'
import { fromGovEvidence } from './SignDocument'

/** Y28 — yasal açıklama tek bileşende (OtpSignPanel); eski içe aktarmalar için yeniden dışa aktarılır. */
export { SimpleSignatureDisclaimer }

const statusTone: Record<SignatureStatus, StatusTone> = { Pending: 'warning', Signed: 'success', Cancelled: 'neutral' }
export const signatureStatusLabel = (s: SignatureStatus) =>
  s === 'Pending' ? tx('İmza bekliyor') : s === 'Signed' ? tx('İmzalandı') : tx('İptal edildi')

export function SignatureStatusBadge({ status }: { status: SignatureStatus }) {
  return <StatusBadge tone={statusTone[status]}>{signatureStatusLabel(status)}</StatusBadge>
}

/** expense kanıt görünümü (governance ya da eski kanıt) → ortak kanıt görünümü. */
export const fromRequestEvidence = (e: SignatureEvidenceView): UnifiedEvidence => ({
  signedAt: e.signedAt, method: e.method.startsWith('OTP-') ? e.method : `OTP-${e.otpChannel}`, documentSha256: e.documentHash,
  evidenceSha256: e.evidenceHash, ipPrefix: e.ipMasked, integrityOk: e.integrityOk, documentId: e.documentId,
  documentVersion: e.documentVersion, signerEmployeeId: e.signerEmployeeId, userAgentHash: e.userAgentHash,
})

/** İmza kanıtı: belge özeti, imzalayan, zaman, kısaltılmış IP, yöntem, bütünlük. */
export function EvidenceDetails({ e }: { e: SignatureEvidenceView }) {
  return <SignatureEvidenceDetails e={fromRequestEvidence(e)} />
}

/* ------------------------------------------------------------------ İK: imzaya gönder */

export function SendForSignatureModal({ doc, onClose }: { doc: HrDocument; onClose: () => void }) {
  const [message, setMessage] = useState('')
  const send = useAction(() => docSignatureApi.request(doc.id, message.trim() || undefined), {
    success: tx('Belge imzaya gönderildi; çalışana bildirim iletildi'),
    invalidate: [['doc-signatures']],
    onDone: onClose,
  })
  return (
    <Modal
      open
      onClose={onClose}
      title={tx('İmzaya gönder')}
      note={doc.fileName}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
          <Button onClick={() => send.mutate(undefined)} disabled={send.isPending}><Send className="size-4" />{' '}{tx('Gönder')}</Button>
        </>
      }
    >
      <div className="space-y-4">
        <SimpleSignatureDisclaimer />
        <TextAreaField label={tx('Çalışana not (isteğe bağlı)')} rows={3} maxLength={500} value={message} onChange={(e) => setMessage(e.target.value)} />
        <InfoNote>{tx('Çalışan belgeyi İmzalarım sayfasında görür ve tek kullanımlık kodla imzalar. İmzalanan belge değiştirilemez; yeniden imza için yeni talep gerekir.')}</InfoNote>
      </div>
    </Modal>
  )
}

/** İK: dokümanın imza talepleri ve kanıtları. */
export function DocumentSignaturesModal({ doc, onClose }: { doc: HrDocument; onClose: () => void }) {
  const q = useQuery({ queryKey: ['doc-signatures', doc.id], queryFn: ({ signal }) => docSignatureApi.forDocument(doc.id, signal) })
  const cancel = useAction((id: string) => docSignatureApi.cancel(id), { success: tx('İmza talebi iptal edildi'), invalidate: [['doc-signatures']] })
  return (
    <Modal open onClose={onClose} title={tx('İmza geçmişi')} note={doc.fileName} size="lg">
      <div className="space-y-4">
        <SimpleSignatureDisclaimer />
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('İmza bilgileri alınamadı.')}</p> : (
          <>
            <p className="text-[12px] text-muted-foreground">
              {tx('Güncel belge özeti')}: <span className="break-all font-mono text-[11px]">{q.data.document.contentHash}</span>
            </p>
            {q.data.items.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Bu doküman henüz imzaya gönderilmedi.')}</p> : q.data.items.map((r) => (
              <div key={r.id} className="space-y-3 rounded-xl border border-border p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <SignatureStatusBadge status={r.status} />
                  <span className="text-[12.5px] text-muted-foreground">{tx('Gönderildi')}: {formatDateTime(r.createdAt)}</span>
                  {r.status === 'Pending' && (
                    <Button size="sm" variant="outline" className="ml-auto" disabled={cancel.isPending} onClick={() => cancel.mutate(r.id)}>{tx('Talebi iptal et')}</Button>
                  )}
                </div>
                {r.message && <p className="text-[12.5px]">{r.message}</p>}
                {r.evidence && <EvidenceDetails e={r.evidence} />}
              </div>
            ))}
          </>
        )}
      </div>
    </Modal>
  )
}

/** İK listesi için: doküman → son imza durumu. */
export function useSignatureStatus(enabled = true) {
  return useQuery({
    queryKey: ['doc-signatures', 'status'],
    queryFn: ({ signal }) => docSignatureApi.statusSummary(signal),
    enabled,
    select: (rows) => new Map(rows.map((r) => [r.documentId, r])),
  })
}

/* ------------------------------------------------------------------ çalışan: imzala */

export function SignDocumentOtpModal({ req, onClose }: { req: SignatureRequestView; onClose: () => void }) {
  const qc = useQueryClient()
  const [accepted, setAccepted] = useState(false)
  const [signed, setSigned] = useState(false)
  const d = req.document
  return (
    <Modal open onClose={onClose} title={signed ? tx('İmza kanıtı') : tx('Belgeyi imzala')} note={d?.fileName} size="lg"
      footer={<Button variant={signed ? 'default' : 'outline'} onClick={onClose}>{signed ? tx('Kapat') : tx('Vazgeç')}</Button>}>
      <div className="space-y-4">
        <SimpleSignatureDisclaimer />
        {!signed && (
          <>
            {d && (
              <dl className="grid grid-cols-[130px_1fr] gap-x-3 gap-y-1.5 rounded-xl border border-border p-3 text-[12.5px]">
                <dt className="text-muted-foreground">{tx('Belge')}</dt><dd className="font-medium">{d.fileName}</dd>
                <dt className="text-muted-foreground">{tx('Tür')}</dt><dd>{documentTypeLabels[d.type as DocumentType] ?? d.type}</dd>
                <dt className="text-muted-foreground">{tx('Kayıt tarihi')}</dt><dd>{formatDateTime(d.uploadedAt)}</dd>
                <dt className="text-muted-foreground">{tx('Dosya')}</dt><dd className="break-all">{d.storageKey || tx('dosya bağlı değil')}</dd>
                <dt className="text-muted-foreground">{tx('Belge özeti')}</dt><dd className="break-all font-mono text-[11px]">{d.contentHash}</dd>
              </dl>
            )}
            {req.message && <InfoNote>{req.message}</InfoNote>}
            <label className="flex items-start gap-2 text-[13px]">
              <Checkbox checked={accepted} onCheckedChange={(v) => setAccepted(v === true)} className="mt-0.5" />
              <span>{tx('Belgeyi inceledim; bunun basit elektronik imza olduğunu ve nitelikli elektronik imza yerine geçmediğini biliyorum.')}</span>
            </label>
          </>
        )}
        <OtpSignPanel
          disabled={!accepted}
          disabledHint={tx('Kod istemek için önce belgeyi incelediğinizi onaylayın.')}
          onRequest={() => docSignatureApi.sendOtp(req.id)}
          onSign={(c, code) => docSignatureApi.sign(req.id, code, c.otpId)}
          toEvidence={(r) => fromRequestEvidence(r.evidence)}
          onSigned={() => {
            setSigned(true)
            void qc.invalidateQueries({ queryKey: ['doc-signatures'] })
            void qc.invalidateQueries({ queryKey: ['my-signatures'] })
          }}
        />
      </div>
    </Modal>
  )
}

/** Çalışanın imzasını bekleyen İK belgeleri (ve birleşme öncesi imzalanmış olanlar — eski kanıt). */
export function MySignaturesPanel({ compact = false }: { compact?: boolean }) {
  const q = useQuery({ queryKey: ['doc-signatures', 'mine'], queryFn: ({ signal }) => docSignatureApi.mine(signal) })
  const [open, setOpen] = useState<SignatureRequestView | null>(null)
  // İmza motoruyla imzalananlar "İmzaladığım belgeler" listesinde; burada bekleyenler ve eski kanıtlılar kalır.
  const items = (q.data?.items ?? []).filter((r) => r.status === 'Pending' || (!compact && r.status === 'Signed' && r.evidence?.source === 'legacy'))
  if (compact && items.length === 0) return null
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><FileSignature className="size-4 text-primary" />{' '}{tx('İmzanızı bekleyen belgeler')}</span>}
        note={tx('Belgeyi inceleyin, tek kullanımlık kodla imzalayın.')} />
      <PanelBody className="space-y-3">
        <SimpleSignatureDisclaimer />
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('Belgeler alınamadı.')}</p> : items.length === 0 ? (
          <EmptyState title={tx('İmza bekleyen belge yok')} detail={tx('İK bir belgeyi imzanıza gönderdiğinde burada görünür.')} />
        ) : (
          <ul className="divide-y divide-border rounded-xl border border-border">
            {items.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-3 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13.5px] font-medium">{r.document?.fileName}</p>
                  <p className="text-[12px] text-muted-foreground">{r.status === 'Signed' ? tx('İmzalandı: {0}', [formatDateTime(r.signedAt)]) : tx('Gönderildi: {0}', [formatDateTime(r.createdAt)])}</p>
                </div>
                <SignatureStatusBadge status={r.status} />
                {r.status === 'Pending'
                  ? <Button size="sm" onClick={() => setOpen(r)}><FileSignature className="size-4" />{' '}{tx('İmzala')}</Button>
                  : r.evidence && <Button size="sm" variant="outline" onClick={() => setOpen(r)}><BadgeCheck className="size-4" />{' '}{tx('Kanıtı gör')}</Button>}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {open && (open.status === 'Pending'
        ? <SignDocumentOtpModal req={open} onClose={() => setOpen(null)} />
        : <Modal open onClose={() => setOpen(null)} title={tx('İmza kanıtı')} note={open.document?.fileName} size="lg">
            <div className="space-y-4"><SimpleSignatureDisclaimer />{open.evidence && <EvidenceDetails e={open.evidence} />}</div>
          </Modal>)}
    </Panel>
  )
}

const documentKindLabel = (t: string) =>
  t === 'HrDocument' ? tx('Özlük belgesi') : t === 'DocumentRequest' ? tx('Belge talebi') : t

/** İmzaladığım belgeler — tek imza motorundaki tüm kanıtlar (belge talepleri + İK özlük belgeleri). */
export function MySignedDocumentsPanel() {
  const q = useQuery({ queryKey: ['my-signatures'], queryFn: ({ signal }) => platformGovApi.mySignatures(signal) })
  const [open, setOpen] = useState<SignatureEvidence | null>(null)
  const items = q.data?.items ?? []
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><FileCheck2 className="size-4 text-primary" />{' '}{tx('İmzaladığım belgeler')}</span>}
        note={tx('Basit elektronik imzayla imzaladığınız tüm belgeler ve imza kanıtları.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('İmzalı belgeler alınamadı.')}</p> : items.length === 0 ? (
          <EmptyState title={tx('Henüz imzaladığınız belge yok')} detail={tx('Kodla imzaladığınız belgeler burada listelenir.')} />
        ) : (
          <ul className="divide-y divide-border rounded-xl border border-border">
            {items.map((e) => (
              <li key={e.id} className="flex flex-wrap items-center gap-3 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13.5px] font-medium">{e.title ?? tx('Belge')}</p>
                  <p className="text-[12px] text-muted-foreground">{documentKindLabel(e.documentType)} · {tx('İmzalandı: {0}', [formatDateTime(e.signedAt)])}</p>
                </div>
                <StatusBadge tone={e.integrityOk ? 'success' : 'danger'}>{e.integrityOk ? tx('Kanıt doğrulandı') : tx('Kanıt uyuşmuyor')}</StatusBadge>
                <Button size="sm" variant="outline" onClick={() => setOpen(e)}><BadgeCheck className="size-4" />{' '}{tx('Kanıtı gör')}</Button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {open && (
        <Modal open onClose={() => setOpen(null)} title={tx('İmza kanıtı')} note={open.title ?? undefined} size="lg">
          <div className="space-y-4"><SimpleSignatureDisclaimer /><SignatureEvidenceDetails e={fromGovEvidence(open)} /></div>
        </Modal>
      )}
    </Panel>
  )
}

/** /panel/imzalarim — tüm çalışanlara açık. */
export function MySignaturesPage() {
  return (
    <div className="space-y-5">
      <PageHeader title={tx('İmzalarım')} description={tx('İK tarafından imzanıza gönderilen belgeler ve imza kanıtlarınız.')} />
      <MySignaturesPanel />
      <MySignedDocumentsPanel />
    </div>
  )
}
