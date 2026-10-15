/**
 * Yıllık zaman çizelgesi: ay ekseni üzerinde dönem çubukları ve "bugün"
 * işareti. Çakışan dönemler (ör. yarıyıl ile çeyrek) ayrı şeritlere düşer.
 */

import { motion } from 'motion/react'
import type { ReviewCycle } from '@/api/performance'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { appLocale, tx } from '@/lib/i18n'

// Kısa ay adları arayüz dilinden (Intl): "Ara" (Aralık) sözlükte "Search" olarak çevriliyordu.
const MONTHS = Array.from({ length: 12 }, (_, m) =>
  new Intl.DateTimeFormat(appLocale, { month: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(2024, m, 1))).replace('.', ''))

function lanesOf(cycles: ReviewCycle[]) {
  const lanes: ReviewCycle[][] = []
  for (const c of [...cycles].sort((a, b) => a.startDate.localeCompare(b.startDate) || b.endDate.localeCompare(a.endDate))) {
    const lane = lanes.find((l) => l[l.length - 1].endDate < c.startDate)
    if (lane) lane.push(c)
    else lanes.push([c])
  }
  return lanes
}

export function CycleTimeline({
  year,
  cycles,
  selectedId,
  onSelect,
}: {
  year: number
  cycles: ReviewCycle[]
  selectedId?: string | null
  onSelect?: (c: ReviewCycle) => void
}) {
  const yStart = Date.UTC(year, 0, 1)
  const yEnd = Date.UTC(year + 1, 0, 1)
  const span = yEnd - yStart
  const pos = (d: string) => Math.max(0, Math.min(1, (Date.parse(d) - yStart) / span)) * 100
  const inYear = cycles.filter((c) => Date.parse(c.endDate) >= yStart && Date.parse(c.startDate) < yEnd)
  const lanes = lanesOf(inYear)
  const now = Date.now()
  const todayPct = now >= yStart && now < yEnd ? ((now - yStart) / span) * 100 : null

  return (
    <div className="relative">
      {/* ay ekseni */}
      <div className="relative mb-2 grid grid-cols-12 text-[10px] text-muted-foreground">
        {MONTHS.map((m) => (
          <span key={m} className="border-l border-border pl-1">
            {m}
          </span>
        ))}
      </div>

      <div className="relative flex flex-col gap-2">
        {/* ay çizgileri */}
        <div aria-hidden className="pointer-events-none absolute inset-0 grid grid-cols-12">
          {MONTHS.map((m) => (
            <span key={m} className="border-l border-dashed border-border/70" />
          ))}
        </div>

        {lanes.length === 0 && <p className="relative py-4 text-center text-[12px] text-muted-foreground">{tx('{0} için tanımlı dönem yok.', [year])}</p>}

        {lanes.map((lane, li) => (
          <div key={li} className="relative h-9">
            {lane.map((c, ci) => {
              const left = pos(c.startDate)
              const width = Math.max(2, pos(c.endDate) + 100 / 365 - left)
              const on = c.id === selectedId
              return (
                <motion.button
                  key={c.id}
                  type="button"
                  onClick={() => onSelect?.(c)}
                  initial={{ opacity: 0, scaleX: 0 }}
                  animate={{ opacity: 1, scaleX: 1 }}
                  transition={{ duration: 0.7, ease: EASE, delay: 0.1 + (li * 2 + ci) * 0.08 }}
                  style={{ left: `${left}%`, width: `${width}%`, originX: 0 }}
                  className={cn(
                    'absolute inset-y-0 flex items-center overflow-hidden rounded-lg px-2.5 text-left text-[12px] font-medium transition-shadow',
                    c.status === 'Open' && 'hr-sheen bg-primary text-primary-foreground shadow-md',
                    c.status === 'Closed' && 'border border-border bg-muted text-muted-foreground',
                    c.status === 'Planned' && 'border border-dashed border-primary/40 bg-primary/5 text-primary',
                    on && 'ring-2 ring-primary ring-offset-2 ring-offset-card',
                  )}
                  title={`${c.name} · ${c.startDate} – ${c.endDate}`}
                >
                  <span className="truncate">{c.name}</span>
                </motion.button>
              )
            })}
          </div>
        ))}

        {todayPct !== null && (
          <motion.div
            aria-hidden
            initial={{ opacity: 0, scaleY: 0 }}
            animate={{ opacity: 1, scaleY: 1 }}
            transition={{ delay: 0.6, duration: 0.5 }}
            className="pointer-events-none absolute -top-1 -bottom-1 w-0.5 origin-top rounded-full bg-destructive"
            style={{ left: `${todayPct}%` }}
          >
            <span className="absolute -top-5 left-1/2 -translate-x-1/2 rounded bg-destructive px-1 text-[9px] font-semibold whitespace-nowrap text-destructive-foreground">
              {tx('Bugün')}
            </span>
          </motion.div>
        )}
      </div>
    </div>
  )
}
