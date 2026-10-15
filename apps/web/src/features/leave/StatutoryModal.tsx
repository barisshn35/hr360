import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Modal } from '@/components/ui/Modal'
import { Button } from '@/components/ui/button'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Checkbox } from '@/components/ui/checkbox'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useDirectory } from '@/api/directory'
import { leaveApi } from '@/api/leave'
import { formatDate, formatNumber } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * İK: kıdeme göre yasal yıllık izin hakkı ve kullanılmayan iznin devri (G8, dalga 9 / madde 71).
 * Ön izlemede değişecek bakiyeler listelenir; İK satırları seçip onaylar ("yalnızca yıl dönümü gelenler"
 * ile hak doğmamış kişiler atlanır). Bakiye değişiklikleri denetim kaydına yazılır.
 */
export function StatutoryModal({ onClose }: { onClose: () => void }) {
  const now = new Date().getFullYear()
  const [year, setYear] = useState(String(now))
  const [maxDays, setMaxDays] = useState('')
  const [onlyAccrued, setOnlyAccrued] = useState(true)
  const [picked, setPicked] = useState<Set<string> | null>(null)
  const dir = useDirectory()
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const q = useQuery({ queryKey: ['leave', 'statutory', year], queryFn: ({ signal }) => leaveApi.statutory(Number(year), signal) })
  const settings = useQuery({ queryKey: ['leave', 'settings'], queryFn: ({ signal }) => leaveApi.settings(signal) })
  // Devir üst sınırı şirket ayarından önerilir (boş = tamamı).
  useEffect(() => {
    if (settings.data?.carryOverMaxDays != null) setMaxDays((v) => v || String(settings.data!.carryOverMaxDays))
  }, [settings.data])
  const changes = useMemo(() => (q.data ?? []).filter((r) => r.proposedEntitled != null && (!onlyAccrued || r.accrued !== false)), [q.data, onlyAccrued])
  useEffect(() => setPicked(null), [year, onlyAccrued])
  const selected = picked ?? new Set(changes.map((r) => r.employeeId))
  const toggle = (id: string, on: boolean) => {
    const next = new Set(selected)
    if (on) next.add(id)
    else next.delete(id)
    setPicked(next)
  }
  const apply = useAction(() => leaveApi.applyStatutory(Number(year), [...selected], onlyAccrued), {
    success: (r) => tx('{0} bakiye güncellendi', [r.changed]), invalidate: [['leave']], onDone: () => setPicked(null),
  })
  const carry = useAction(() => leaveApi.carryOver(Number(year) - 1, maxDays ? Number(maxDays) : undefined), {
    success: (r) => tx('{0} çalışanın {1} günü {2} yılına devredildi', [r.employees, formatNumber(r.days), year]), invalidate: [['leave']],
  })
  const rows = (q.data ?? []).filter((r) => r.statutoryDays > 0 || (r.currentEntitled ?? 0) > 0)
  const changeIds = new Set(changes.map((r) => r.employeeId))
  const below = changes.filter((r) => selected.has(r.employeeId)).length
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Yasal izin hakkı ve devir')}
      note={tx('İş Kanunu m.53: 1-5 yıl 14 gün, 5-15 yıl 20 gün, 15 yıl ve üstü 26 gün; 18 yaş ve altı ile 50 yaş ve üstüne en az 20 gün. Hak, işe giriş yıl dönümünde doğar.')}>
      <div className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-3">
          <SelectField label={tx('Yıl')} value={year} onChange={setYear} options={[now - 1, now, now + 1].map((y) => ({ value: String(y), label: String(y) }))} />
        </div>
        {q.isPending ? <RowsSkeleton rows={4} /> : (
          <div className="max-h-72 overflow-y-auto rounded-xl border border-border">
            <table className="w-full text-[12.5px]">
              <thead className="bg-muted/40 text-left text-muted-foreground"><tr><th className="w-8 px-3 py-2"><span className="sr-only">{tx('Seç')}</span></th><th className="px-3 py-2">{tx('Çalışan')}</th><th className="px-3 py-2">{tx('Kıdem')}</th><th className="px-3 py-2">{tx('Yıl dönümü')}</th><th className="px-3 py-2 text-right">{tx('Yasal')}</th><th className="px-3 py-2 text-right">{tx('Tanımlı')}</th><th className="px-3 py-2 text-right">{tx('Uygulanınca')}</th></tr></thead>
              <tbody className="divide-y divide-border">
                {rows.map((r) => (
                  <tr key={r.employeeId}>
                    <td className="px-3 py-1.5">
                      {changeIds.has(r.employeeId) && (
                        <Checkbox aria-label={tx('{0} için uygula', [nameOf(r.employeeId)])} checked={selected.has(r.employeeId)} onCheckedChange={(v) => toggle(r.employeeId, v === true)} />
                      )}
                    </td>
                    <td className="px-3 py-1.5">{nameOf(r.employeeId)}{r.ageRule && <> {' '}<StatusBadge tone="info">{tx('yaş kuralı')}</StatusBadge></>}</td>
                    <td className="px-3 py-1.5">{tx('{0} yıl', [r.serviceYears])}</td>
                    <td className="px-3 py-1.5">{formatDate(r.anniversary)}{r.accrued === false && r.statutoryDays > 0 && <> {' '}<StatusBadge>{tx('henüz doğmadı')}</StatusBadge></>}</td>
                    <td className="px-3 py-1.5 text-right tabular-nums">{r.statutoryDays}</td>
                    <td className="px-3 py-1.5 text-right tabular-nums">{r.currentEntitled ?? '—'}{r.carriedOver ? ` (+${formatNumber(r.carriedOver)} ${tx('devir')})` : ''}</td>
                    <td className="px-3 py-1.5 text-right tabular-nums">{r.proposedEntitled != null ? <b>{formatNumber(r.proposedEntitled)}</b> : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <label className="flex items-center gap-2 text-[13px]">
          <Checkbox checked={onlyAccrued} onCheckedChange={(v) => setOnlyAccrued(v === true)} />
          {tx('Yalnızca yıl dönümü gelmiş (hakkı doğmuş) çalışanlar')}
        </label>
        <div className="flex flex-wrap items-center gap-3">
          <Button disabled={apply.isPending || !below} onClick={() => apply.mutate(undefined)}>{tx('Seçilenleri onayla ve bakiyelere yaz ({0})', [below])}</Button>
          <span className="text-[12.5px] text-muted-foreground">{tx('Bakiye yoksa açılır, yasal günün altındaysa yükseltilir; asla düşürülmez. Değişiklikler denetim kaydına yazılır.')}</span>
        </div>
        <div className="grid gap-3 border-t border-border pt-4 sm:grid-cols-[1fr_auto] sm:items-end">
          <TextField label={tx('{0} yılından devredilecek en fazla gün (boş: tamamı)', [Number(year) - 1])} inputMode="numeric" value={maxDays} onChange={(e) => setMaxDays(e.target.value)} />
          <Button variant="outline" disabled={carry.isPending} onClick={() => carry.mutate(undefined)}>{tx('Kullanılmayan izni devret')}</Button>
        </div>
        <InfoNote>{tx('Yıllık ücretli izin yanmaz; üst sınır yalnızca şirket içi devir politikası içindir. Devir tekrar çalıştırılabilir: yalnızca fark aktarılır. Doğum tarihi yalnızca yaş kuralı için kullanılır ve gösterilmez (KVKK).')}</InfoNote>
      </div>
    </Modal>
  )
}
