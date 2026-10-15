/**
 * `progress-metric-card` (21st.dev, makviesainte) bu modülü içe aktarıyor ama
 * kayıt paketinde GELMİYOR — registry yalnızca üst dosyayı gönderiyor. Burası
 * o eksik parçanın HR360 karşılığı: aynı dışa aktarım adları, aynı prop'lar.
 *
 * Renkler `index.css`'teki chart token'larından geliyor; tema değişince
 * grafik de değişiyor.
 */

import { useMemo } from 'react'
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { appLocale } from '@/lib/i18n'

export type SeriesPoint = {
  /** ISO tarih ya da hazır etiket — dateFormatter ile biçimlenir. */
  date: string
  value: number
}

export type MetricAccent = 'violet' | 'sky' | 'emerald' | 'amber' | 'rose' | 'neutral'

export type MetricSeries = {
  name: string
  data: SeriesPoint[]
  accent?: MetricAccent
}

export type ChartSeries = {
  name: string
  data: SeriesPoint[]
  color: string
}

export type ChartView = 'curve' | 'bars'

/**
 * Vurgu paleti. `wash` alanı kartın arka plan gradyanı için — üst bileşen
 * eskiden `${stroke}1f` diye hex'e alfa ekliyordu, bu da token kullanmayı
 * imkânsız kılıyordu; ayrı alan olarak veriyoruz.
 */
export const ACCENTS: Record<MetricAccent, { stroke: string; text: string; wash: string }> = {
  violet: {
    stroke: 'hsl(var(--chart-1))',
    text: 'hsl(var(--chart-1))',
    wash: 'hsl(var(--chart-1) / 0.14)',
  },
  sky: {
    stroke: 'hsl(var(--chart-2))',
    text: 'hsl(var(--chart-2))',
    wash: 'hsl(var(--chart-2) / 0.14)',
  },
  emerald: {
    stroke: 'hsl(var(--chart-3))',
    text: 'hsl(var(--chart-3))',
    wash: 'hsl(var(--chart-3) / 0.14)',
  },
  amber: {
    stroke: 'hsl(var(--chart-4))',
    text: 'hsl(var(--chart-4))',
    wash: 'hsl(var(--chart-4) / 0.14)',
  },
  rose: {
    stroke: 'hsl(var(--chart-5))',
    text: 'hsl(var(--chart-5))',
    wash: 'hsl(var(--chart-5) / 0.14)',
  },
  neutral: {
    stroke: 'hsl(var(--muted-foreground))',
    text: 'hsl(var(--muted-foreground))',
    wash: 'hsl(var(--muted-foreground) / 0.12)',
  },
}

/** Çok serili grafiklerde sıradaki serinin rengi. */
export const SERIES_COLORS = [
  'hsl(var(--chart-1))',
  'hsl(var(--chart-2))',
  'hsl(var(--chart-3))',
  'hsl(var(--chart-4))',
  'hsl(var(--chart-5))',
]

const compact = new Intl.NumberFormat(appLocale, { notation: 'compact', maximumFractionDigits: 1 })

/** 12.400 → "12,4 B". Kart başlığındaki büyük rakam bunu kullanıyor. */
export function formatCompact(value: number): string {
  return compact.format(value)
}

type ChartRow = { date: string } & Record<string, string | number>

function TooltipCard({
  active,
  payload,
  label,
  valueFormatter,
  dateFormatter,
}: {
  active?: boolean
  payload?: Array<{ name?: string; value?: number | string; color?: string }>
  label?: string | number
  valueFormatter: (value: number) => string
  dateFormatter: (date: string) => string
}) {
  if (!active || !payload?.length) return null
  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-2 text-[12px] shadow-md">
      <p className="mb-1 font-medium text-popover-foreground">{dateFormatter(String(label ?? ''))}</p>
      {payload.map((entry) => (
        <p key={entry.name} className="flex items-center gap-1.5 text-muted-foreground">
          <span
            aria-hidden="true"
            className="size-1.5 rounded-full"
            style={{ background: entry.color }}
          />
          <span>{entry.name}</span>
          <span className="tabular ml-auto pl-3 font-medium text-popover-foreground">
            {valueFormatter(Number(entry.value ?? 0))}
          </span>
        </p>
      ))}
    </div>
  )
}

export function MetricChart({
  series,
  view,
  valueFormatter,
  dateFormatter,
}: {
  series: ChartSeries[]
  view: ChartView
  /** Üst bileşen gönderiyor; recharts kendi imlecini yönettiği için kullanılmıyor. */
  defaultIndex?: number
  valueFormatter: (value: number) => string
  dateFormatter: (date: string) => string
}) {
  // Recharts tek satır dizisi bekliyor: seriler sütun olarak birleştirilir.
  const rows = useMemo<ChartRow[]>(() => {
    const byDate = new Map<string, ChartRow>()
    for (const s of series) {
      for (const point of s.data) {
        const row: ChartRow = byDate.get(point.date) ?? { date: point.date }
        row[s.name] = point.value
        byDate.set(point.date, row)
      }
    }
    return [...byDate.values()]
  }, [series])

  const tooltip = (
    <Tooltip
      cursor={{ stroke: 'hsl(var(--border))', strokeWidth: 1 }}
      content={
        <TooltipCard valueFormatter={valueFormatter} dateFormatter={dateFormatter} />
      }
    />
  )

  const axes = (
    <>
      <XAxis dataKey="date" hide />
      <YAxis hide domain={['dataMin', 'dataMax']} />
    </>
  )

  return (
    <ResponsiveContainer width="100%" height="100%">
      {view === 'bars' ? (
        <BarChart data={rows} margin={{ top: 8, right: 8, bottom: 8, left: 8 }}>
          {axes}
          {tooltip}
          {series.map((s) => (
            <Bar key={s.name} dataKey={s.name} fill={s.color} radius={[3, 3, 0, 0]} />
          ))}
        </BarChart>
      ) : (
        <AreaChart data={rows} margin={{ top: 8, right: 0, bottom: 0, left: 0 }}>
          <defs>
            {series.map((s, i) => (
              <linearGradient key={s.name} id={`metric-fill-${i}`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={s.color} stopOpacity={0.28} />
                <stop offset="100%" stopColor={s.color} stopOpacity={0} />
              </linearGradient>
            ))}
          </defs>
          {axes}
          {tooltip}
          {series.map((s, i) => (
            <Area
              key={s.name}
              type="monotone"
              dataKey={s.name}
              stroke={s.color}
              strokeWidth={2}
              fill={`url(#metric-fill-${i})`}
              dot={false}
              activeDot={{ r: 3, strokeWidth: 0 }}
            />
          ))}
        </AreaChart>
      )}
    </ResponsiveContainer>
  )
}
