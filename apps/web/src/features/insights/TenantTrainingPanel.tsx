import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Database, Play } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { ml10Api } from '@/api/wave10'
import { formatDate, formatDateTime } from '@/lib/format'
import { errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * Dalga 10 (madde 36): devir modelini şirket verisiyle eğitme izni ve çalıştırma. Satırlar toplu ve
 * kimliksizdir; değerlendirme zaman bazlıdır (eski dönemle eğitilir, yakın dönemle sınanır). Aday model
 * paylaşılan modeli DEĞİŞTİRMEZ: platform yöneticisinin onayını bekler (Sürümler paneli).
 */
export function TenantTrainingPanel() {
  const q = useQuery({ queryKey: ['ml-model', 'tenant-training'], queryFn: ({ signal }) => ml10Api.tenantTraining(signal) })
  const [preview, setPreview] = useState(false)
  const p = useQuery({ queryKey: ['ml-model', 'tenant-training', 'preview'], queryFn: ({ signal }) => ml10Api.trainingPreview(signal), enabled: preview })
  const consent = useAction((enabled: boolean) => ml10Api.setTrainingConsent(enabled), {
    success: (r) => (r.enabled ? tx('Şirket verisiyle eğitim izni verildi') : tx('Şirket verisiyle eğitim izni geri alındı')), invalidate: [['ml-model', 'tenant-training']],
  })
  const run = useAction(() => ml10Api.runTenantTraining(), {
    success: (r) => (r.awaitingApproval ? tx('Aday v{0} kaydedildi; yayına alma platform onayı bekliyor', [r.candidateVersion ?? '?']) : tx('Eğitim tamamlandı')),
    invalidate: [['ml-model']],
  })
  const confirm = useConfirm()
  if (q.isPending) return <Panel><RowsSkeleton rows={3} /></Panel>
  if (q.isError) return <Panel><PanelBody><ErrorState message={errMsg(q.error)} /></PanelBody></Panel>
  const s = q.data
  const toggle = async (v: boolean) => {
    if (v && !(await confirm({
      title: tx('Model şirket verisiyle eğitilsin mi?'),
      note: tx('Eğitimde yalnızca kimliksiz, toplu özellikler (kıdem, ücret/bant oranı, son puan, son terfiden geçen süre, fazla mesai, eğitim saati) ve ayrıldı/kalmadı etiketi kullanılır. Ad, cinsiyet, yaş gönderilmez; satırlar saklanmaz. İzin istediğiniz an geri alınabilir.'),
      action: tx('İzin ver'),
    }))) return
    consent.mutate(v)
  }
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Database className="size-4 text-primary" />{' '}{tx('Modeli şirket verisiyle eğit (izin)')}</span>}
        note={tx('Paylaşılan model yalnızca izin veren şirketlerin toplu ve kimliksiz verisiyle eğitilir. Aday sürüm yayına otomatik girmez; platform yöneticisi onaylar.')} />
      <PanelBody className="space-y-4 text-[13px]">
        <label className="flex items-center gap-2">
          <Checkbox checked={s.enabled} disabled={!s.canConsent || consent.isPending} onCheckedChange={(v) => void toggle(v === true)} />
          {' '}{tx('Şirket verisiyle eğitime izin veriyorum')}
          {s.enabled ? <StatusBadge tone="success">{tx('İzin verildi')}</StatusBadge> : <StatusBadge>{tx('Kapalı')}</StatusBadge>}
        </label>
        {s.consentAt && <p className="text-[12px] text-muted-foreground">{tx('Son değişiklik: {0} · {1}', [formatDateTime(s.consentAt), s.consentBy ?? '—'])}</p>}
        {!s.canConsent && <InfoNote>{tx('İzni yalnızca şirket yöneticisi verebilir.')}</InfoNote>}
        <InfoNote>{tx('Zaman bazlı doğrulama: eğitim {0} tarihindeki çalışanlarla (sonraki 12 ayda ayrılanlar etiketli), değerlendirme {1} tarihindekilerle yapılır. En az {2} çalışan ve {3} ayrılan gerekir; yoksa eğitim reddedilir.',
          [formatDate(s.trainSnapshot), formatDate(s.evalSnapshot), s.thresholds.minTrainRows, s.thresholds.minTrainLeavers])}</InfoNote>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => { setPreview(true); void p.refetch() }}>{tx('Veriyi önizle')}</Button>
          <Button disabled={!s.enabled || run.isPending} onClick={() => run.mutate(undefined)}><Play className="size-4" />{' '}{tx('Aday model eğit')}</Button>
        </div>
        {preview && (p.isFetching ? <RowsSkeleton rows={2} /> : p.data && (
          <div className="rounded-xl border border-border p-3 text-[12.5px]">
            <p>{tx('Eğitim: {0} çalışan, {1} ayrılan · Değerlendirme: {2} çalışan, {3} ayrılan', [p.data.train.rows, p.data.train.leavers, p.data.eval.rows, p.data.eval.leavers])}</p>
            <p className="text-muted-foreground">{tx('Eksik değer (nötr değerle doldurulan): ücret oranı {0}, performans puanı {1}', [p.data.train.imputed.compa_ratio ?? 0, p.data.train.imputed.last_rating ?? 0])}</p>
            {p.data.ok ? <StatusBadge tone="success">{tx('Eşikler sağlanıyor')}</StatusBadge> : (
              <ul className="mt-1 list-disc pl-5 text-destructive">{p.data.refusals.map((r) => <li key={r}>{r}</li>)}</ul>
            )}
          </div>
        ))}
        {s.lastTraining && (
          <p className="text-[12px] text-muted-foreground">
            {tx('Son eğitim {0}: aday v{1}', [formatDateTime(s.lastTraining.at), s.lastTraining.candidateVersion ?? '?'])}
            {s.lastTraining.awaitingApproval ? ` · ${tx('onay bekliyor')}` : s.lastTraining.promoted ? ` · ${tx('yayında')}` : ` · ${tx('yayımlanmadı')}`}
            {s.lastTraining.reason ? ` · ${s.lastTraining.reason}` : ''}
          </p>
        )}
      </PanelBody>
    </Panel>
  )
}
