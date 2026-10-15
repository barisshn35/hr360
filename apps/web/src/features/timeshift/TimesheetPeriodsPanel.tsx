/**
 * Madde 72: puantaj dönemi kilidi (İK). Kapalı ayda giriş-çıkış, puantaj düzeltmesi ve fazla mesai talebi
 * yapılamaz; bordro hesaplaması kapanmamış puantaj dönemini uyarı olarak bildirir. Onay bekleyen fazla
 * mesai varken dönem kapatılamaz; yeniden açma gerekçe ister ve denetim kaydına yazılır.
 */
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Lock, LockOpen } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { apiFetch } from '@/api/client'
import { formatDateTime, formatNumber } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx, appLocale } from '@/lib/i18n'

export interface TimesheetPeriod {
  year: number
  month: number
  status: 'Open' | 'Closed'
  closedAt: string | null
  closedBy: string | null
  reopenedAt: string | null
  reopenReason: string | null
  entries: number
  missingOut: number
  approvedOvertimeHours: number
  pendingOvertime: number
}

const BASE = '/api/timeshift/timesheet-periods'
export const timesheetPeriodsApi = {
  list: (year: number, signal?: AbortSignal) => apiFetch<TimesheetPeriod[]>(`${BASE}?year=${year}`, { signal }),
  close: (year: number, month: number) => apiFetch<{ missingOut: number }>(`${BASE}/${year}/${month}/close`, { method: 'POST' }),
  reopen: (year: number, month: number, reason: string) => apiFetch<unknown>(`${BASE}/${year}/${month}/reopen`, { method: 'POST', body: { reason } }),
}

const monthName = (m: number) => new Date(2000, m - 1, 1).toLocaleDateString(appLocale, { month: 'long' })

export function TimesheetPeriodsPanel() {
  const now = new Date()
  const [year, setYear] = useState(String(now.getFullYear()))
  const [reopen, setReopen] = useState<TimesheetPeriod | null>(null)
  const [reason, setReason] = useState('')
  const q = useQuery({ queryKey: ['timeshift', 'periods', year], queryFn: ({ signal }) => timesheetPeriodsApi.list(Number(year), signal) })
  const confirm = useConfirm()
  const close = useAction((p: TimesheetPeriod) => timesheetPeriodsApi.close(p.year, p.month), {
    success: (r) => r.missingOut ? tx('Dönem kapatıldı ({0} kayıtta çıkış eksik)', [r.missingOut]) : tx('Dönem kapatıldı'),
    invalidate: [['timeshift', 'periods']],
  })
  const doReopen = useAction(() => timesheetPeriodsApi.reopen(reopen!.year, reopen!.month, reason.trim()), {
    success: tx('Dönem yeniden açıldı'), invalidate: [['timeshift', 'periods']], onDone: () => { setReopen(null); setReason('') },
  })
  const thisMonth = now.getFullYear() * 12 + now.getMonth() + 1
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Lock className="size-4 text-primary" />{' '}{tx('Puantaj dönemleri')}</span>}
        note={tx('Kapatılan ayda giriş-çıkış ve fazla mesai değiştirilemez; bordro bu dönemin onaylı fazla mesaisini okur.')} />
      <PanelBody className="space-y-3">
        <div className="w-32">
          <SelectField label={tx('Yıl')} value={year} onChange={setYear} options={[now.getFullYear() - 1, now.getFullYear()].map((y) => ({ value: String(y), label: String(y) }))} />
        </div>
        {q.isPending ? <RowsSkeleton rows={3} /> : (
          <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
            {(q.data ?? []).filter((p) => p.year * 12 + p.month <= thisMonth).map((p) => (
              <li key={p.month} className="flex flex-wrap items-center gap-3 px-4 py-2">
                <span className="w-28 font-medium capitalize">{monthName(p.month)}</span>
                {p.status === 'Closed' ? <StatusBadge tone="neutral">{tx('Kapalı')}</StatusBadge> : <StatusBadge tone="success">{tx('Açık')}</StatusBadge>}
                <span className="flex-1 text-muted-foreground">
                  {tx('{0} kayıt · onaylı fazla mesai {1} sa', [p.entries, formatNumber(p.approvedOvertimeHours)])}
                  {p.pendingOvertime > 0 && ` · ${tx('{0} bekleyen fazla mesai', [p.pendingOvertime])}`}
                  {p.missingOut > 0 && ` · ${tx('{0} çıkış eksik', [p.missingOut])}`}
                  {p.status === 'Closed' && p.closedAt && ` · ${tx('{0} kapattı: {1}', [p.closedBy ?? '—', formatDateTime(p.closedAt)])}`}
                </span>
                {p.status === 'Open' ? (
                  <Button size="sm" variant="outline" disabled={close.isPending || p.pendingOvertime > 0}
                    title={p.pendingOvertime > 0 ? tx('Önce bekleyen fazla mesai taleplerini sonuçlandırın') : undefined}
                    onClick={async () => {
                      if (await confirm({ title: tx('{0} {1} dönemi kapatılsın mı?', [monthName(p.month), p.year]), note: tx('Bu ayın giriş-çıkış ve fazla mesai kayıtları kilitlenir. Gerekirse gerekçeyle yeniden açabilirsiniz.'), action: tx('Kapat') }))
                        close.mutate(p)
                    }}><Lock className="size-4" /> {tx('Kapat')}</Button>
                ) : (
                  <Button size="sm" variant="ghost" onClick={() => setReopen(p)}><LockOpen className="size-4" /> {tx('Yeniden aç')}</Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {reopen && (
        <Modal open onClose={() => setReopen(null)} title={tx('{0} {1} dönemini yeniden aç', [monthName(reopen.month), reopen.year])}
          footer={<><Button variant="outline" onClick={() => setReopen(null)}>{tx('Vazgeç')}</Button><Button disabled={reason.trim().length < 5 || doReopen.isPending} onClick={() => doReopen.mutate(undefined)}>{tx('Yeniden aç')}</Button></>}>
          <TextAreaField label={tx('Gerekçe (denetim kaydına yazılır)')} rows={3} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Modal>
      )}
    </Panel>
  )
}
