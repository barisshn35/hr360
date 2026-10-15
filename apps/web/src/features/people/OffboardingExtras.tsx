/**
 * G15: offboarding zimmet iade listesi (İK istisnası: kayıp / kayıttan düşme, gerekçeli),
 * giriş hesabının kapatılma durumu ve KVKK imha planı (planlanan anonimleştirme tarihi).
 */
import { useState } from 'react'
import { KeyRound, PackageCheck, ShieldX } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import type { OffboardingCase } from '@/api/engagement'
import { assetResolutionLabels, opsApi, type AssetCheck } from '@/api/opsPlus'
import { formatDate, formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const resolutionTone = { Open: 'warning', Returned: 'success', Lost: 'danger', WrittenOff: 'neutral' } as const

function OverrideModal({ caseId, item, onClose }: { caseId: string; item: AssetCheck; onClose: () => void }) {
  const [resolution, setResolution] = useState<'Lost' | 'WrittenOff'>('Lost')
  const [note, setNote] = useState('')
  const save = useAction(() => opsApi.overrideAsset(caseId, item.assignmentId, resolution, note.trim()), {
    success: (r) => r.warning ?? tx('Zimmet istisnası kaydedildi'), invalidate: [['offboarding']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={tx('İK istisnası — {0}', [item.assetTag])}
      note={tx('İade edilemeyen zimmet gerekçesiyle kapatılır; kayıt zimmet envanterine de işlenir ve denetim kaydına yazılır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button variant="destructive" onClick={() => save.mutate(undefined)} disabled={save.isPending || note.trim().length < 5}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        <SelectField label={tx('Durum')} value={resolution} onChange={(v) => setResolution(v as 'Lost' | 'WrittenOff')}
          options={[{ value: 'Lost', label: assetResolutionLabels.Lost }, { value: 'WrittenOff', label: assetResolutionLabels.WrittenOff }]} />
        <TextAreaField label={tx('Gerekçe')} rows={3} value={note} onChange={(e) => setNote(e.target.value)} hint={tx('En az 5 karakter. Örn. çalışan kaybettiğini bildirdi, tutanak tutuldu.')} />
      </div>
    </Modal>
  )
}

export function AssetReturnChecklist({ c }: { c: OffboardingCase }) {
  const { roles } = useAuth()
  const hr = isHr(roles, 'ext-engagement-manage')
  const [override, setOverride] = useState<AssetCheck | null>(null)
  const items = c.assetChecks ?? []
  return (
    <div>
      <p className="mb-2 flex items-center gap-2 text-[14px] font-semibold"><PackageCheck className="size-4 text-primary" />{' '}{tx('Zimmet iadesi')}</p>
      {items.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Çalışanın açık zimmeti yok.')}</p> : (
        <ul className="space-y-1.5">
          {items.map((a) => (
            <li key={a.assignmentId} className="flex flex-wrap items-center gap-2 rounded-xl border border-border p-2.5 text-[13px]">
              <span className="min-w-0 flex-1">{a.label}
                {(a.note || a.resolvedBy) && <span className="block text-[11.5px] text-muted-foreground">{[a.note, a.resolvedBy && a.resolvedAt ? `${a.resolvedBy}, ${formatDate(a.resolvedAt)}` : null].filter(Boolean).join(' · ')}</span>}
              </span>
              <StatusBadge tone={resolutionTone[a.resolution]}>{assetResolutionLabels[a.resolution]}</StatusBadge>
              {hr && c.status === 'Open' && a.resolution === 'Open' && <Button size="sm" variant="outline" onClick={() => setOverride(a)}>{tx('İstisna')}</Button>}
            </li>
          ))}
        </ul>
      )}
      {items.some((a) => a.resolution === 'Open') && (
        <p className="mt-2 text-[12px] text-muted-foreground">{tx('Süreç, tüm zimmetler iade alınana (Zimmet ekranından) ya da İK istisnasıyla kapatılana kadar tamamlanamaz.')}</p>
      )}
      {override && <OverrideModal caseId={c.id} item={override} onClose={() => setOverride(null)} />}
    </div>
  )
}

export function AccountAndRetention({ c }: { c: OffboardingCase }) {
  const { roles } = useAuth()
  const hr = isHr(roles, 'ext-engagement-manage')
  const retry = useAction(() => opsApi.retryDisable(c.id), { success: (r) => r.accountNote ?? tx('Yeniden denendi'), invalidate: [['offboarding']] })
  const status = c.accountStatus
  return (
    <div className="space-y-3">
      <div>
        <p className="mb-1 flex items-center gap-2 text-[14px] font-semibold"><KeyRound className="size-4 text-primary" />{' '}{tx('Giriş hesabı')}</p>
        {!status ? (
          <p className="text-[13px] text-muted-foreground">{tx('Süreç tamamlanınca hesap kapatılır ve tüm oturumlar sonlandırılır.')}</p>
        ) : (
          <div className="flex flex-wrap items-center gap-2 text-[13px]">
            <StatusBadge tone={status === 'Disabled' ? 'success' : status === 'Failed' ? 'danger' : 'neutral'}>
              {status === 'Disabled' ? tx('Kapatıldı') : status === 'Failed' ? tx('Kapatılamadı') : status === 'NoAccount' ? tx('Hesap yok') : tx('Atlandı')}
            </StatusBadge>
            <span className="text-muted-foreground">{c.accountNote}{c.accountDisabledAt ? ` · ${formatDateTime(c.accountDisabledAt)}` : ''}</span>
            {hr && status === 'Failed' && <Button size="sm" variant="outline" onClick={() => retry.mutate(undefined)} disabled={retry.isPending}>{tx('Yeniden dene')}</Button>}
          </div>
        )}
      </div>
      <div>
        <p className="mb-1 flex items-center gap-2 text-[14px] font-semibold"><ShieldX className="size-4 text-primary" />{' '}{tx('İmha planı (KVKK)')}</p>
        {c.plannedAnonymizationOn ? (
          <InfoNote>{tx('Kişisel veriler, saklama politikasına göre ({0} ay) {1} tarihinde anonimleştirilecek. Süre Ayarlar › KVKK › Saklama politikalarından değişir.', [c.retentionMonths ?? '—', formatDate(c.plannedAnonymizationOn)])}</InfoNote>
        ) : <p className="text-[13px] text-muted-foreground">{tx('Planlanan tarih süreç tamamlanınca hesaplanır.')}</p>}
      </div>
    </div>
  )
}
