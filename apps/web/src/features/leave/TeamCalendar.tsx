import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CalendarRange, ChevronLeft, ChevronRight, Palmtree, Users } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { SelectField } from '@/components/ui/Field'
import { RowsSkeleton } from '@/components/ui/States'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { leaveApi, leaveTypeLabels, type TeamCalendar as TeamCalendarData } from '@/api/leave'
import { useMyEmployeeId } from '@/api/queries'
import { useAuth } from '@/auth/useAuth'
import { useDepartments } from '@/features/performance/hooks'
import { localISODate } from '@/lib/dates'
import { formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'

const iso = (y: number, m: number, d: number) => localISODate(new Date(y, m, d))

/**
 * Ekip izin takvimi (yenilik haritası #1): ay görünümünde ekibin izinleri, hafta sonu ve resmi tatiller.
 * Gizlilik sunucuda uygulanır: çalışan ekip arkadaşlarını yalnızca İK açtıysa adıyla (türsüz) görür,
 * aksi hâlde gün başına izinli sayısı gösterilir. İK ve departman başı departman seçebilir.
 */
export function TeamCalendar() {
  const { can, roles } = useAuth()
  const isHr = roles.some((r) => r === 'hr-admin' || r === 'tenant-admin')
  const me = useMyEmployeeId()
  const depts = useDepartments()
  const [month, setMonth] = useState(() => { const d = new Date(); return { y: d.getFullYear(), m: d.getMonth() } })
  const [dept, setDept] = useState('')
  const days = new Date(month.y, month.m + 1, 0).getDate()
  const from = iso(month.y, month.m, 1)
  const to = iso(month.y, month.m, days)

  const pickable = can('organization:view')
    ? depts.list.filter((d) => isHr || (me.employeeId && (d as { headEmployeeId?: string | null }).headEmployeeId === me.employeeId))
    : []
  const q = useQuery({
    queryKey: ['leave', 'team-calendar', from, to, dept],
    queryFn: ({ signal }) => leaveApi.teamCalendar(from, to, dept || undefined, signal),
    enabled: !!dept || !!me.employeeId,
    retry: false,
  })
  const data = q.data
  const today = localISODate()
  const holidayMap = useMemo(() => new Map((data?.holidays ?? []).map((h) => [h.date, h])), [data])
  const othersMap = useMemo(() => new Map((data?.others ?? []).map((o) => [o.date, o.count])), [data])
  const cols = Array.from({ length: days }, (_, i) => {
    const d = new Date(month.y, month.m, i + 1)
    const key = localISODate(d)
    return { day: i + 1, key, weekend: d.getDay() === 0 || d.getDay() === 6, holiday: holidayMap.get(key), today: key === today, dow: d.toLocaleDateString(appLocale, { weekday: 'narrow' }) }
  })
  const shift = (n: number) => setMonth(({ y, m }) => { const d = new Date(y, m + n, 1); return { y: d.getFullYear(), m: d.getMonth() } })
  const title = new Date(month.y, month.m, 1).toLocaleDateString(appLocale, { month: 'long', year: 'numeric' })

  function cellOf(personId: string, key: string): TeamCalendarData['entries'][number] | undefined {
    return data?.entries.find((e) => e.employeeId === personId && e.startDate <= key && e.endDate >= key)
  }

  return (
    <Panel>
      <PanelHead
        title={<span className="flex items-center gap-2"><CalendarRange className="size-4 text-primary" />{tx('Ekip izin takvimi')}</span>}
        note={data ? tx('{0} kişilik ekip', [data.teamSize]) : undefined}
        action={
          <div className="flex items-center gap-1">
            <Button size="icon" variant="ghost" aria-label={tx('Önceki ay')} onClick={() => shift(-1)}><ChevronLeft className="size-4" /></Button>
            <span className="min-w-28 text-center text-[13px] font-medium first-letter:uppercase">{title}</span>
            <Button size="icon" variant="ghost" aria-label={tx('Sonraki ay')} onClick={() => shift(1)}><ChevronRight className="size-4" /></Button>
          </div>
        } />
      <PanelBody className="space-y-3">
        {pickable.length > 0 && (
          <div className="max-w-xs">
            <SelectField label={tx('Departman')} value={dept || '__mine'} onChange={(v) => setDept(v === '__mine' ? '' : v)}
              options={[{ value: '__mine', label: tx('Kendi ekibim') }, ...pickable.map((d) => ({ value: d.id, label: d.path }))]} />
          </div>
        )}
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? (
          <p className="text-[13px] text-muted-foreground">{q.error instanceof Error ? q.error.message : tx('Takvim yüklenemedi.')}</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full border-separate border-spacing-0 text-[11px]">
              <thead>
                <tr>
                  <th className="sticky left-0 z-10 min-w-36 bg-card" />
                  {cols.map((c) => (
                    <th key={c.key} title={c.holiday?.name} className={cn('min-w-6 px-0 pb-1 text-center font-normal text-muted-foreground', (c.weekend || c.holiday) && 'text-muted-foreground/50', c.today && 'text-primary font-semibold')}>
                      <span className="block">{c.dow}</span>{c.day}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(data?.people ?? []).map((p) => (
                  <tr key={p.id}>
                    <td className="sticky left-0 z-10 bg-card py-1 pr-2">
                      <span className="flex items-center gap-1.5 text-[12.5px]">
                        <EmployeeAvatar id={p.id} name={p.name} size={20} />
                        <span className={cn('truncate', p.self && 'font-semibold')}>{p.self ? tx('{0} (siz)', [p.name]) : p.name}</span>
                      </span>
                    </td>
                    {cols.map((c) => {
                      const e = cellOf(p.id, c.key)
                      const off = c.weekend || (c.holiday && !c.holiday.isHalfDay)
                      return (
                        <td key={c.key} className={cn('h-7 border-t border-border/60 p-0.5', off && 'bg-muted/50')}>
                          {e && !off && (
                            <span title={`${e.type ? leaveTypeLabels[e.type] : tx('İzinli')} · ${e.status === 'Submitted' ? tx('onay bekliyor') : tx('onaylı')}`}
                              className={cn('block h-full rounded-[4px]', e.status === 'Submitted' ? 'border border-dashed border-primary/70 bg-primary/15' : 'bg-primary/70')} />
                          )}
                        </td>
                      )
                    })}
                  </tr>
                ))}
                {(data?.others.length ?? 0) > 0 && (
                  <tr>
                    <td className="sticky left-0 z-10 bg-card py-1 pr-2 text-[12px] text-muted-foreground"><span className="flex items-center gap-1.5"><Users className="size-4" />{tx('Diğer ekip arkadaşları')}</span></td>
                    {cols.map((c) => {
                      const n = othersMap.get(c.key)
                      return (
                        <td key={c.key} className="h-7 border-t border-border/60 text-center tabular-nums text-muted-foreground">
                          {othersMap.has(c.key) ? (n == null ? '•' : formatNumber(n)) : ''}
                        </td>
                      )
                    })}
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11.5px] text-muted-foreground">
          <span className="flex items-center gap-1.5"><span className="size-3 rounded-[3px] bg-primary/70" />{tx('Onaylı izin')}</span>
          <span className="flex items-center gap-1.5"><span className="size-3 rounded-[3px] border border-dashed border-primary/70 bg-primary/15" />{tx('Onay bekliyor')}</span>
          <span className="flex items-center gap-1.5"><span className="size-3 rounded-[3px] bg-muted" />{tx('Hafta sonu ve resmi tatil')}</span>
          {(data?.holidays.length ?? 0) > 0 && (
            <span className="flex items-center gap-1.5"><Palmtree className="size-3.5" />{data!.holidays.map((h) => `${Number(h.date.slice(8))} ${h.name}`).join(' · ')}</span>
          )}
        </div>
      </PanelBody>
    </Panel>
  )
}
