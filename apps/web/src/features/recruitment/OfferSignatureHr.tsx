/**
 * Dalga 11 (madde 76): İK tarafı teklif e-imzası — imza bağlantısı (bir kez gösterilir, yenile/iptal)
 * ve imza kanıtı (governance kanıtı + belge bütünlüğü) penceresi.
 */
import { useQuery } from '@tanstack/react-query'
import { Copy, Download, ShieldAlert, ShieldCheck } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { recruitmentApi, type SignedLetter } from '@/api/recruitment'
import { errMsg, useAction } from '@/features/shared/kit'
import { SignatureEvidenceDetails, SimpleSignatureDisclaimer } from '@/features/documents/OtpSignPanel'
import { tx } from '@/lib/i18n'

/** İmzalı belgeyi (HTML) indirir. */
export function downloadSignedLetter(doc: SignedLetter) {
  const url = URL.createObjectURL(new Blob([doc.html], { type: 'text/html;charset=utf-8' }))
  const a = document.createElement('a')
  a.href = url
  a.download = doc.fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** Yeni üretilen imza bağlantısı (yalnızca şimdi gösterilir). */
export function SigningLinkModal({ path, onClose }: { path: string | null; onClose: () => void }) {
  if (!path) return null
  const link = `${window.location.origin}${path}`
  return (
    <Modal open onClose={onClose} title={tx('Teklif imza bağlantısı')}
      footer={<Button onClick={onClose}>{tx('Kapat')}</Button>}>
      <div className="space-y-3 text-[13px]">
        <p>{tx('Bağlantı adayın e-posta adresine gönderildi. Güvenlik için yalnızca şimdi gösterilir; gerekirse yenileyebilir ya da iptal edebilirsiniz.')}</p>
        <div className="flex gap-2">
          <input readOnly value={link} aria-label={tx('İmza bağlantısı')} className="h-9 flex-1 rounded-lg border border-input bg-muted/40 px-2 text-[12.5px]" />
          <Button variant="outline" onClick={() => void navigator.clipboard?.writeText(link)}><Copy className="size-4" /> {tx('Kopyala')}</Button>
        </div>
        <InfoNote>{tx('Aday mektubu bu bağlantıdan görür ve e-postasına gelen tek kullanımlık kodla imzalar. Kariyer sayfasından başvuran aday kişisel öz-hizmet bağlantısını da kullanabilir.')}</InfoNote>
      </div>
    </Modal>
  )
}

const Check = ({ ok, yes, no }: { ok: boolean | undefined; yes: string; no: string }) => (
  <p className="flex items-center gap-1.5 text-[12.5px]">
    {ok ? <ShieldCheck className="size-4 text-[hsl(var(--success))]" /> : <ShieldAlert className="size-4 text-destructive" />}
    {ok ? yes : no}
  </p>
)

/** İmza kanıtı penceresi (İK / onaycı; görüntüleme denetlenir). */
export function OfferSignatureModal({ offerId, onClose }: { offerId: string | null; onClose: () => void }) {
  const q = useQuery({
    queryKey: ['recruitment', 'offer-signature', offerId],
    queryFn: ({ signal }) => recruitmentApi.offerSignature(offerId!, signal),
    enabled: !!offerId,
  })
  const download = useAction(() => recruitmentApi.signedLetter(offerId!), { onDone: downloadSignedLetter })
  if (!offerId) return null
  const d = q.data
  return (
    <Modal open size="lg" onClose={onClose} title={tx('Teklif imza kanıtı')}
      footer={<>
        {d?.signed && <Button variant="outline" disabled={download.isPending} onClick={() => download.mutate(undefined)}><Download className="size-4" /> {tx('İmzalı belgeyi indir')}</Button>}
        <Button onClick={onClose}>{tx('Kapat')}</Button>
      </>}>
      {q.isPending ? <RowsSkeleton rows={4} /> : q.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p>
        : !d?.signed ? <p className="text-[13px] text-muted-foreground">{tx('Teklif henüz elektronik olarak imzalanmadı.')}</p> : (
          <div className="space-y-3">
            <SimpleSignatureDisclaimer />
            {d.evidence
              ? <SignatureEvidenceDetails e={{ ...d.evidence, ipPrefix: null, title: null }} />
              : <p className="text-[12.5px] text-destructive">{tx('İmza kanıtına şu anda ulaşılamıyor (imza servisi).')}</p>}
            <div className="space-y-1 rounded-xl border border-border p-3">
              <Check ok={d.evidence?.matchesLetter} yes={tx('Kanıttaki belge özeti teklif mektubuyla eşleşiyor')} no={tx('Kanıttaki belge özeti teklif mektubuyla eşleşmiyor')} />
              <Check ok={d.letterUnchanged} yes={tx('Mektup imzadan sonra değişmedi')} no={tx('Mektup imzadan sonra değişmiş (ya da anonimleştirilmiş)')} />
              <Check ok={d.documentIntegrityOk} yes={tx('Saklanan imzalı belgenin özeti doğrulandı')} no={tx('Saklanan imzalı belgenin özeti uyuşmuyor')} />
            </div>
          </div>
        )}
    </Modal>
  )
}
