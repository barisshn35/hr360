import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Hourglass } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { useDirectory } from '@/api/directory'
import { overtimeApi, type OvertimeStatus } from '@/api/timeclock'
import { formatDate, parseDecimal } from '@/lib/format'
import { isoDate, useAction } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'

const STATUS: Record<OvertimeStatus, { label: string; tone: 'neutral' | 'success' | 'danger' | 'warning' }> = {
  Pending: { label: tx('Onay bekliyor'), tone: 'warning' },
  Approved: { label: tx('Onaylandı'), tone: 'success' },
  Rejected: { label: tx('Reddedildi'), tone: 'danger' },
  Cancelled: { label: tx('İptal'), tone: 'neutral' },
}
const h = (n: number) => n.toLocaleString(appLocale, { maximumFractionDigits: 1 })

/** Sunucudaki OvertimeController.DailyLimitHours ile aynı (günlük çalışma 11 saati aşamaz). */
export const OVERTIME_DAILY_MAX = 4

/** Fazla mesai saati alan doğrulaması: boş/0/negatif ve günlük üst sınır alan yanında gösterilir. */
export function overtimeHoursError(input: string): string | undefined {
  const n = parseDecimal(input)
  if (input.trim() === '' || n === null) return tx('Saat girin (ör. 2 ya da 1,5).')
  if (n <= 0) return tx('Saat sıfırdan büyük olmalı.')
  if (n > OVERTIME_DAILY_MAX) return tx('Günde en fazla {0} saat girilebilir.', [OVERTIME_DAILY_MAX])
  if (Math.round(n * 2) !== n * 2) return tx('Yarım saat adımıyla girin (ör. 1,5).')
  return undefined
}

/** Puantaj › Fazla mesai: talep, yıllık 270 saat sınırı ve (İK için) onay akışı olmayan talepler. */
export function OvertimePanel() {
  const { hasRole } = useAuth()
  const isHr = hasRole('hr-admin') || hasRole('tenant-admin')
  const year = new Date().getFullYear()
  const dir = useDirectory(isHr)
  const nameOf = (id: string) => dir.data?.find((d) => d.id === id)?.fullName ?? '—'
  const mine = useQuery({ queryKey: ['overtime', 'mine', year], queryFn: ({ signal }) => overtimeApi.list({ year, mine: true }, signal) })
  const summary = useQuery({ queryKey: ['overtime', 'summary', year], queryFn: ({ signal }) => overtimeApi.summary({ year }, signal) })
  const orphan = useQuery({
    queryKey: ['overtime', 'pending-all'], enabled: isHr,
    queryFn: ({ signal }) => overtimeApi.list({ status: 'Pending' }, signal).then((r) => r.filter((o) => !o.workflowRequestId)),
  })
  const [date, setDate] = useState(isoDate())
  const [hours, setHours] = useState('2')
  const [reason, setReason] = useState('')
  const inv = [['overtime']]
  const hoursErr = overtimeHoursError(hours)
  const create = useAction(() => overtimeApi.create({ date, hours: parseDecimal(hours) ?? 0, reason: reason.trim() || undefined }), {
    success: tx('Fazla mesai talebi gönderildi'), invalidate: inv, onDone: () => setReason(''),
  })
  const cancel = useAction((id: string) => overtimeApi.cancel(id), { success: tx('Talep iptal edildi'), invalidate: inv })
  const decide = useAction(({ id, ok }: { id: string; ok: boolean }) => overtimeApi.decide(id, ok), { success: tx('Karar kaydedildi'), invalidate: inv })
  const s = summary.data
  const used = s ? s.approvedHours + s.pendingHours : 0
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Hourglass className="size-4 text-primary" />{' '}{tx('Fazla mesai')}</span>}
        note={tx('Yasal sınır yılda 270 saat; günde en fazla 4 saat girilebilir. Yalnızca onaylanan saatler bordroya girer.')} />
      <PanelBody className="space-y-5">
        {s && (
          <div>
            <div className="mb-1.5 flex justify-between text-[12.5px]"><span>{tx('{0} yılı: {1} saat onaylı, {2} saat bekliyor', [year, h(s.approvedHours), h(s.pendingHours)])}</span><span className="text-muted-foreground">{tx('{0} saat kaldı', [h(s.remainingHours)])}</span></div>
            <div className="h-2 overflow-hidden rounded-full bg-muted" role="progressbar" aria-valuenow={used} aria-valuemin={0} aria-valuemax={s.limitHours}>
              <div className="h-full bg-primary" style={{ width: `${Math.min(100, (used / s.limitHours) * 100)}%` }} />
            </div>
          </div>
        )}
        <div className="grid gap-3 sm:grid-cols-[160px_110px_1fr_auto] sm:items-end">
          <TextField label={tx('Tarih')} type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          <TextField label={tx('Saat')} inputMode="decimal" value={hours} error={hours ? hoursErr : undefined} onChange={(e) => setHours(e.target.value)} />
          <TextField label={tx('Gerekçe')} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} hint={tx('İş gerekçesi yazın; sağlık bilgisi yazmayın.')} />
          <Button disabled={create.isPending || !date || !!hoursErr} onClick={() => create.mutate(undefined)}>{tx('Talep et')}</Button>
        </div>
        {mine.isPending ? <RowsSkeleton rows={2} /> : !mine.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Bu yıl fazla mesai talebiniz yok.')}</p> : (
          <ul className="divide-y divide-border text-[13px]">
            {mine.data.map((o) => (
              <li key={o.id} className="flex flex-wrap items-center gap-3 py-2">
                <span className="w-28">{formatDate(o.date)}</span>
                <span className="w-16 tabular-nums">{tx('{0} sa', [h(o.hours)])}</span>
                <span className="min-w-0 flex-1 truncate text-muted-foreground">{o.reason ?? '—'}</span>
                <StatusBadge tone={STATUS[o.status].tone}>{STATUS[o.status].label}</StatusBadge>
                {o.workflowRequestId && <Link className="text-[12px] text-primary hover:underline" to={`/panel/onaylar/${o.workflowRequestId}`}>{tx('Onay')}</Link>}
                {o.status === 'Pending' && <Button size="sm" variant="ghost" onClick={() => cancel.mutate(o.id)}>{tx('İptal et')}</Button>}
              </li>
            ))}
          </ul>
        )}
        {isHr && !!orphan.data?.length && (
          <div>
            <p className="mb-2 text-[13px] font-medium">{tx('Onaycısı bulunamayan talepler (bölüm başı atanmamış)')}</p>
            <ul className="divide-y divide-border text-[13px]">
              {orphan.data.map((o) => (
                <li key={o.id} className="flex flex-wrap items-center gap-3 py-2">
                  <span className="min-w-0 flex-1">{nameOf(o.employeeId)} · {formatDate(o.date)} · {tx('{0} sa', [h(o.hours)])}</span>
                  <Button size="sm" onClick={() => decide.mutate({ id: o.id, ok: true })}>{tx('Onayla')}</Button>
                  <Button size="sm" variant="outline" onClick={() => decide.mutate({ id: o.id, ok: false })}>{tx('Reddet')}</Button>
                </li>
              ))}
            </ul>
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}
