import { useMemo } from 'react'
import { Sparkles } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { useLeaveHolidays } from '@/api/queries-modules'
import { bridgeSuggestions, type BridgeSuggestion } from '@/lib/bridgeDays'
import { localISODate } from '@/lib/dates'
import { formatNumber } from '@/lib/format'
import { tx, appLocale } from '@/lib/i18n'

const fmt = (s: string, opts: Intl.DateTimeFormatOptions) => {
  const [y, m, d] = s.split('-').map(Number)
  return new Date(y, m - 1, d).toLocaleDateString(appLocale, opts)
}

/**
 * Köprü günü önerileri (yenilik haritası #3): resmi tatilleri hafta sonuyla birleştiren, az izinle uzun
 * tatil sağlayan aralıklar. Hesap tarayıcıda, şirketin tatil takviminden yapılır.
 */
export function BridgeDays({ onPick }: { onPick?: (s: BridgeSuggestion) => void }) {
  const year = new Date().getFullYear()
  const a = useLeaveHolidays(year)
  const b = useLeaveHolidays(year + 1)
  const list = useMemo(
    () => bridgeSuggestions([...(a.data ?? []), ...(b.data ?? [])].map((h) => ({ date: h.date.slice(0, 10), name: h.name, isHalfDay: h.isHalfDay })), localISODate(), { maxLeave: 4, limit: 6 }),
    [a.data, b.data],
  )
  if (!list.length) return null
  const names = new Map([...(a.data ?? []), ...(b.data ?? [])].map((h) => [h.date.slice(0, 10), h.name]))
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Sparkles className="size-4 text-amber-400" />{tx('Köprü günleri')}</span>}
        note={tx('Resmi tatilleri hafta sonuyla birleştirin: az izinle uzun tatil.')} />
      <PanelBody className="p-0">
        <ul className="divide-y divide-border">
          {list.map((s) => (
            <li key={s.start} className="flex flex-wrap items-center gap-3 px-5 py-3">
              <div className="grid size-12 shrink-0 place-items-center rounded-xl bg-primary/10 text-center leading-none text-primary">
                <span className="text-[17px] font-bold tabular-nums">{s.totalDays}</span>
                <span className="text-[10px]">{tx('gün')}</span>
              </div>
              <div className="min-w-0 flex-1 text-[13px]">
                <p className="font-medium">{tx('{0} gün izinle {1} gün tatil', [formatNumber(s.leaveDays), s.totalDays])}</p>
                <p className="text-[12px] text-muted-foreground">
                  {fmt(s.start, { day: 'numeric', month: 'short' })} – {fmt(s.end, { day: 'numeric', month: 'short', weekday: 'short' })}
                  {' · '}{[...new Set(s.holidays.map((h) => names.get(h)?.replace(/\s*\(.*\)$/, '')))].filter(Boolean).join(', ')}
                </p>
                <p className="text-[12px] text-muted-foreground">{tx('İzin: {0}', [s.leaveDates.map((d) => fmt(d, { day: 'numeric', month: 'short', weekday: 'short' })).join(', ')])}</p>
              </div>
              {onPick && <Button size="sm" variant="outline" onClick={() => onPick(s)}>{tx('Talep oluştur')}</Button>}
            </li>
          ))}
        </ul>
      </PanelBody>
    </Panel>
  )
}
