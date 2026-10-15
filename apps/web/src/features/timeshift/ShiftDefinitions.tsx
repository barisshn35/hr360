/**
 * Sabit vardiya tanımları (ad, saat, mola). "Atama" ve takas bu tanımlara dayanır; tanım yoksa
 * atama listesi boş kalır. Tanımı İK ekler/siler; atama bağlı tanım sunucuda 409 ile korunur.
 */
import { useState } from 'react'
import { Clock3, Plus, Trash2 } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useShifts } from '@/api/queries'
import { timeshiftApi } from '@/api/timeshift'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const hhmm = (t?: string | null) => (t ? t.slice(0, 5) : '—')

/** "08:00"–"20:00" arası dakika; gece yarısını geçen vardiyada ertesi güne taşar. */
function spanMinutes(start: string, end: string) {
  const m = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))
  const d = m(end) - m(start)
  return d > 0 ? d : d + 24 * 60
}

export function ShiftDefinitionsPanel() {
  const { roles } = useAuth()
  const canEdit = isHr(roles, 'ext-timeshift-manage')
  const shifts = useShifts()
  const confirm = useConfirm()
  const [form, setForm] = useState({ name: '', start: '08:00', end: '16:00', brk: '60' })
  const [adding, setAdding] = useState(false)
  const inv = [['timeshift']]
  const brk = Number(form.brk)
  const span = form.start && form.end && form.start !== form.end ? spanMinutes(form.start, form.end) : 0
  const errs = {
    name: form.name.trim() ? undefined : tx('Ad zorunlu.'),
    end: !form.start || !form.end ? tx('Saat zorunlu.') : form.start === form.end ? tx('Başlangıç ve bitiş aynı olamaz.') : undefined,
    brk: form.brk.trim() === '' || !Number.isInteger(brk) || brk < 0 || brk > 240 ? tx('0–240 dakika arası bir tam sayı girin.') : span && brk >= span ? tx('Mola vardiya süresinden kısa olmalı.') : undefined,
  }
  const create = useAction(
    () => timeshiftApi.createShift({ name: form.name.trim(), startTime: `${form.start}:00`, endTime: `${form.end}:00`, breakMinutes: brk }),
    { success: tx('Vardiya tanımlandı'), invalidate: inv, onDone: () => { setForm({ ...form, name: '' }); setAdding(false) } },
  )
  const remove = useAction((id: string) => timeshiftApi.deleteShift(id), { success: tx('Vardiya tanımı silindi'), invalidate: inv })
  const list = shifts.data ?? []
  return (
    <Panel>
      <PanelHead
        title={<span className="flex items-center gap-2"><Clock3 className="size-4 text-primary" />{' '}{tx('Vardiya tanımları')}</span>}
        note={tx('Atama ve takas bu tanımlardan yapılır.')}
        action={canEdit && !adding ? <Button size="sm" variant="outline" onClick={() => setAdding(true)}><Plus className="size-4" />{' '}{tx('Yeni vardiya tanımla')}</Button> : undefined}
      />
      <PanelBody className="space-y-3">
        {adding && (
          <div className="grid gap-3 rounded-xl border border-border p-3 md:grid-cols-[1fr_auto_auto_auto_auto] md:items-end">
            <TextField label={tx('Ad')} value={form.name} error={form.name ? errs.name : undefined} placeholder={tx('ör. Gündüz')} onChange={(e) => setForm({ ...form, name: e.target.value })} />
            <TextField label={tx('Başlangıç')} type="time" value={form.start} onChange={(e) => setForm({ ...form, start: e.target.value })} />
            <TextField label={tx('Bitiş')} type="time" value={form.end} error={errs.end} onChange={(e) => setForm({ ...form, end: e.target.value })} />
            <TextField label={tx('Mola (dk)')} type="number" min={0} max={240} className="w-24" value={form.brk} error={errs.brk} onChange={(e) => setForm({ ...form, brk: e.target.value })} />
            <span className="flex gap-2">
              <Button variant="outline" onClick={() => setAdding(false)}>{tx('Vazgeç')}</Button>
              <Button disabled={create.isPending || !!(errs.name || errs.end || errs.brk)} onClick={() => create.mutate(undefined)}>{tx('Ekle')}</Button>
            </span>
          </div>
        )}
        {shifts.isPending ? <RowsSkeleton rows={2} /> : list.length === 0 ? (
          <EmptyState
            icon={Clock3}
            title={tx('Vardiya tanımı yok')}
            detail={canEdit
              ? tx('Atama listesi boş, çünkü henüz vardiya tanımlanmadı. "Yeni vardiya tanımla" ile gündüz, gece gibi vardiyaları ekleyin.')
              : tx('Atama listesi boş, çünkü henüz vardiya tanımlanmadı. Vardiya tanımlarını İK ekler; İK ile iletişime geçin.')}
          />
        ) : (
          <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
            {list.map((s) => {
              const night = s.endTime < s.startTime
              return (
                <li key={s.id} className="flex flex-wrap items-center gap-3 px-4 py-2">
                  <span className="min-w-0 flex-1 font-medium">{s.name}</span>
                  <span className="tabular-nums text-muted-foreground">{hhmm(s.startTime)}–{hhmm(s.endTime)} · {tx('{0} dk mola', [s.breakMinutes])}</span>
                  {night && <StatusBadge tone="info">{tx('Gece')}</StatusBadge>}
                  {canEdit && (
                    <Button size="sm" variant="ghost" className="text-destructive" aria-label={tx('Sil')} disabled={remove.isPending} onClick={async () => {
                      if (await confirm({
                        title: tx('"{0}" vardiyası silinsin mi?', [s.name]),
                        note: tx('Bu vardiyaya atama yapılmışsa silme reddedilir.'),
                        action: tx('Sil'),
                      })) remove.mutate(s.id)
                    }}><Trash2 className="size-4" /></Button>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}
