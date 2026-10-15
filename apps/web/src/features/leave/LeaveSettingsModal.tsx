import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { TextField } from '@/components/ui/Field'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { leaveApi, type LeaveSettings } from '@/api/leave'
import { formatNumber, parseDecimal } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * İK: izin ayarları (dalga 9) — saatlik izinde günlük çalışma saati, ekip çakışma uyarısı eşiği,
 * yıllık izin devrinde önerilen üst sınır.
 */
export function LeaveSettingsModal({ onClose }: { onClose: () => void }) {
  const q = useQuery({ queryKey: ['leave', 'settings'], queryFn: ({ signal }) => leaveApi.settings(signal) })
  return (
    <Modal open onClose={onClose} title={tx('İzin ayarları')} note={tx('Şirket genelinde geçerlidir; değişiklikler denetim kaydına yazılır.')}>
      {q.isPending || !q.data ? <RowsSkeleton rows={3} /> : <Form s={q.data} onClose={onClose} />}
    </Modal>
  )
}

function Form({ s, onClose }: { s: LeaveSettings; onClose: () => void }) {
  const [dayHours, setDayHours] = useState(s.customDayHours != null ? formatNumber(s.customDayHours) : '')
  const [warn, setWarn] = useState(s.conflictWarnEnabled)
  const [threshold, setThreshold] = useState(String(s.conflictThresholdPercent))
  const [carry, setCarry] = useState(s.carryOverMaxDays != null ? formatNumber(s.carryOverMaxDays) : '')
  const dh = dayHours.trim() ? parseDecimal(dayHours) : null
  const th = Number(threshold)
  const co = carry.trim() ? parseDecimal(carry) : null
  const errs = {
    dayHours: dayHours.trim() && (dh === null || dh < 1 || dh > 12 || !Number.isInteger(dh * 2)) ? tx('1-12 arası, 0,5 adımlarla.') : undefined,
    threshold: !Number.isInteger(th) || th < 1 || th > 100 ? tx('%1-100 arası bir tam sayı.') : undefined,
    carry: carry.trim() && (co === null || co < 0 || co > 365) ? tx('0-365 gün.') : undefined,
  }
  const save = useAction(() => leaveApi.saveSettings({ dayHours: dh, conflictWarnEnabled: warn, conflictThresholdPercent: th, carryOverMaxDays: co }), {
    success: tx('İzin ayarları kaydedildi'), invalidate: [['leave', 'settings']], onDone: onClose,
  })
  return (
    <div className="space-y-4">
      <TextField label={tx('Günlük çalışma saati (saatlik izin)')} inputMode="decimal" value={dayHours} placeholder={formatNumber(s.dayHours)}
        error={errs.dayHours} hint={tx('Boş: varsayılan ({0} saat). Saatlik izinde gün = saat / günlük saat.', [formatNumber(s.dayHours)])} onChange={(e) => setDayHours(e.target.value)} />
      <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={warn} onCheckedChange={(v) => setWarn(v === true)} /> {tx('Ekip izin çakışması uyarısı')}</label>
      {warn && (
        <TextField label={tx('Uyarı eşiği (% ekip aynı anda izinde)')} inputMode="numeric" value={threshold} error={errs.threshold} onChange={(e) => setThreshold(e.target.value)} />
      )}
      <TextField label={tx('Yıllık izin devrinde önerilen üst sınır (gün)')} inputMode="decimal" value={carry} error={errs.carry}
        hint={tx('Boş: sınırsız. Yıllık ücretli izin yasal olarak yanmaz; bu yalnızca şirket içi devir politikasıdır.')} onChange={(e) => setCarry(e.target.value)} />
      <InfoNote>{tx('Çakışma uyarısı engel değildir. Talep eden yalnızca sayıyı görür (ekip 5 kişiden küçükse yalnızca eşik aşıldı bilgisini); adları yalnızca yönetici ve İK görür, izin türü hiç gösterilmez.')}</InfoNote>
      <div className="flex justify-end gap-2">
        <Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
        <Button disabled={save.isPending || !!(errs.dayHours || errs.threshold || errs.carry)} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
      </div>
    </div>
  )
}
