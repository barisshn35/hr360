/**
 * Performans grafik kiti (Recharts).
 *
 * Geçici noktalar (az değerlendirmeye dayanan) her grafikte aynı dille
 * çizilir: kesik çizgi + içi boş nokta. Renkler yalnızca `--chart-1..5` ve
 * tema token'larından.
 *
 * Kesik/düz ayrımı: aynı veri iki seride çizilir. Düz seri yalnızca iki ucu
 * da kesin olan parçaları, kesik seri geçici noktaya değen parçaları taşır;
 * düz seri üstte kaldığı için çakışan parçalar düz görünür.
 */

import type { ReactNode } from 'react'
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import { formatScore } from '@/api/performance'
import { tx } from '@/lib/i18n'

export const CHART = {
  c1: 'hsl(var(--chart-1))',
  c2: 'hsl(var(--chart-2))',
  c3: 'hsl(var(--chart-3))',
  c4: 'hsl(var(--chart-4))',
  c5: 'hsl(var(--chart-5))',
  grid: 'hsl(var(--border))',
  axis: 'hsl(var(--muted-foreground))',
  card: 'hsl(var(--card))',
}

const axisProps = {
  tick: { fill: CHART.axis, fontSize: 11 },
  tickLine: false,
  axisLine: false,
}

/** Verinin çevresine nefes payı bırakan 0–100 aralığı. */
export function scoreDomain(values: (number | null | undefined)[]): [number, number] {
  const v = values.filter((x): x is number => typeof x === 'number')
  if (!v.length) return [0, 100]
  const lo = Math.max(0, Math.floor((Math.min(...v) - 10) / 10) * 10)
  const hi = Math.min(100, Math.ceil((Math.max(...v) + 8) / 10) * 10)
  return [lo, hi === lo ? Math.min(100, lo + 10) : hi]
}

/* ------------------------------------ Tooltip ----------------------------------- */

function TipBox({ title, children }: { title: ReactNode; children: ReactNode }) {
  return (
    <div className="min-w-36 rounded-lg border border-border bg-popover px-3 py-2 text-[12px] shadow-lg">
      <p className="mb-1 font-semibold text-foreground">{title}</p>
      {children}
    </div>
  )
}

function TipRow({ color, label, value, note }: { color: string; label: string; value: string; note?: string | null }) {
  return (
    <p className="flex items-center justify-between gap-4">
      <span className="flex items-center gap-1.5 text-muted-foreground">
        <span className="size-2 rounded-full" style={{ background: color }} />
        {label}
      </span>
      <span className="tabular font-semibold text-foreground">
        {value}
        {note && <span className="ml-1 font-normal text-[hsl(var(--warning))]">{note}</span>}
      </span>
    </p>
  )
}

/* ---------------------------------- Nokta çizimi --------------------------------- */

function makeDot(color: string) {
  // Recharts dot fonksiyonu; geçici noktalar içi boş.
  return function Dot(props: { cx?: number; cy?: number; payload?: { isProvisional?: boolean }; value?: number | null; index?: number }) {
    const { cx, cy, payload, value, index } = props
    if (cx === undefined || cy === undefined || value === null || value === undefined) return <g key={`d-${index}`} />
    const prov = payload?.isProvisional
    return (
      <circle
        key={`d-${index}`}
        cx={cx}
        cy={cy}
        r={prov ? 4.5 : 4}
        fill={prov ? CHART.card : color}
        stroke={color}
        strokeWidth={prov ? 2 : 1.5}
      />
    )
  }
}

/* ----------------------------------- Trend ------------------------------------ */

export interface TrendPoint {
  bucket: string
  score: number | null
  isProvisional: boolean
  reviewCount?: number
}

function splitSeries<T extends { isProvisional: boolean }>(data: T[], value: (d: T) => number | null) {
  return data.map((d, i) => {
    const v = value(d)
    const prev = data[i - 1]
    const next = data[i + 1]
    const touchesProv = d.isProvisional || Boolean(prev?.isProvisional && value(prev) !== null) || Boolean(next?.isProvisional && value(next) !== null)
    const solidOk = !d.isProvisional
    return { solid: solidOk ? v : null, dashed: touchesProv ? v : null }
  })
}

export function TrendChart({
  data,
  color = CHART.c1,
  height = 240,
  label = 'Puan',
  thresholds,
}: {
  data: TrendPoint[]
  color?: string
  height?: number
  label?: string
  /** Hafif referans çizgileri (takdir/gelişim). */
  thresholds?: { value: number; label: string }[]
}) {
  const parts = splitSeries(data, (d) => d.score)
  const rows = data.map((d, i) => ({ ...d, solid: parts[i].solid, dashed: parts[i].dashed }))
  const domain = scoreDomain(data.map((d) => d.score))

  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={rows} margin={{ top: 10, right: 12, bottom: 0, left: -18 }}>
        <CartesianGrid stroke={CHART.grid} strokeDasharray="3 4" vertical={false} />
        <XAxis dataKey="bucket" {...axisProps} interval="preserveStartEnd" minTickGap={16} />
        <YAxis domain={domain} {...axisProps} width={44} />
        {thresholds?.map((t) =>
          t.value >= domain[0] && t.value <= domain[1] ? (
            <ReferenceLine key={t.label} y={t.value} stroke={CHART.axis} strokeOpacity={0.35} strokeDasharray="2 4" label={{ value: t.label, position: 'insideTopRight', fill: CHART.axis, fontSize: 10 }} />
          ) : null,
        )}
        <Tooltip
          cursor={{ stroke: CHART.grid }}
          content={(p) => {
            const d = p.payload?.[0]?.payload as (typeof rows)[number] | undefined
            if (!p.active || !d) return null
            return (
              <TipBox title={d.bucket}>
                <TipRow color={color} label={label} value={formatScore(d.score)} note={d.isProvisional ? tx('geçici') : null} />
                {d.reviewCount !== undefined && <p className="mt-1 text-[11px] text-muted-foreground">{tx('{0} değerlendirme', [d.reviewCount])}</p>}
              </TipBox>
            )
          }}
        />
        <Line type="monotone" dataKey="dashed" stroke={color} strokeWidth={2} strokeDasharray="5 5" dot={false} activeDot={false} isAnimationActive animationDuration={900} connectNulls={false} />
        <Line type="monotone" dataKey="solid" stroke={color} strokeWidth={2.5} dot={false} activeDot={false} isAnimationActive animationDuration={900} connectNulls={false} />
        <Line type="monotone" dataKey="score" stroke="transparent" dot={makeDot(color)} activeDot={{ r: 6, fill: color, stroke: CHART.card, strokeWidth: 2 }} isAnimationActive={false} />
      </LineChart>
    </ResponsiveContainer>
  )
}

/* ----------------------------- İki seri (çalışan + ekip) ----------------------------- */

export interface DualPoint {
  bucket: string
  a: number | null
  b: number | null
  isProvisional: boolean
}

export function DualTrendChart({
  data,
  aLabel,
  bLabel,
  aColor = CHART.c1,
  bColor = CHART.c2,
  height = 260,
}: {
  data: DualPoint[]
  aLabel: string
  bLabel: string
  aColor?: string
  bColor?: string
  height?: number
}) {
  const parts = splitSeries(data, (d) => d.a)
  const rows = data.map((d, i) => ({ ...d, aSolid: parts[i].solid, aDashed: parts[i].dashed }))
  const domain = scoreDomain(data.flatMap((d) => [d.a, d.b]))
  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={rows} margin={{ top: 10, right: 12, bottom: 0, left: -18 }}>
        <CartesianGrid stroke={CHART.grid} strokeDasharray="3 4" vertical={false} />
        <XAxis dataKey="bucket" {...axisProps} interval="preserveStartEnd" minTickGap={16} />
        <YAxis domain={domain} {...axisProps} width={44} />
        <Tooltip
          cursor={{ stroke: CHART.grid }}
          content={(p) => {
            const d = p.payload?.[0]?.payload as (typeof rows)[number] | undefined
            if (!p.active || !d) return null
            const diff = d.a !== null && d.b !== null ? d.a - d.b : null
            return (
              <TipBox title={d.bucket}>
                <TipRow color={aColor} label={aLabel} value={formatScore(d.a)} note={d.isProvisional ? tx('geçici') : null} />
                <TipRow color={bColor} label={bLabel} value={formatScore(d.b)} />
                {diff !== null && (
                  <p className="mt-1 text-[11px] text-muted-foreground">
                    {tx('Fark:')}{' '}<span className="tabular font-semibold text-foreground">{diff > 0 ? '+' : diff < 0 ? '−' : ''}{formatScore(Math.abs(diff))}</span>
                  </p>
                )}
              </TipBox>
            )
          }}
        />
        <Line type="monotone" dataKey="b" stroke={bColor} strokeWidth={2} strokeOpacity={0.85} dot={false} isAnimationActive animationDuration={900} connectNulls />
        <Line type="monotone" dataKey="aDashed" stroke={aColor} strokeWidth={2} strokeDasharray="5 5" dot={false} activeDot={false} isAnimationActive animationDuration={900} connectNulls={false} />
        <Line type="monotone" dataKey="aSolid" stroke={aColor} strokeWidth={2.5} dot={false} activeDot={false} isAnimationActive animationDuration={900} connectNulls={false} />
        <Line type="monotone" dataKey="a" stroke="transparent" dot={makeDot(aColor)} activeDot={{ r: 6, fill: aColor, stroke: CHART.card, strokeWidth: 2 }} isAnimationActive={false} />
      </LineChart>
    </ResponsiveContainer>
  )
}

/* ---------------------------------- Histogram ---------------------------------- */

export function Histogram({
  data,
  height = 220,
  color = CHART.c2,
  colorOf,
}: {
  data: { label: string; count: number; from?: number; to?: number }[]
  height?: number
  color?: string
  /** Kovaya göre renk (ör. eşik bölgesi). */
  colorOf?: (bucket: { label: string; from?: number; to?: number }) => string
}) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart data={data} margin={{ top: 10, right: 8, bottom: 0, left: -24 }} barCategoryGap="18%">
        <CartesianGrid stroke={CHART.grid} strokeDasharray="3 4" vertical={false} />
        <XAxis dataKey="label" {...axisProps} />
        <YAxis allowDecimals={false} {...axisProps} width={40} />
        <Tooltip
          cursor={{ fill: 'hsl(var(--muted))', opacity: 0.5 }}
          content={(p) => {
            const d = p.payload?.[0]?.payload as { label: string; count: number } | undefined
            if (!p.active || !d) return null
            return (
              <TipBox title={tx('{0} puan aralığı', [d.label])}>
                <p className="tabular text-foreground">
                  <span className="font-semibold">{d.count}</span>{' '}{tx('kişi')}
                </p>
              </TipBox>
            )
          }}
        />
        <Bar dataKey="count" radius={[6, 6, 0, 0]} isAnimationActive animationDuration={800}>
          {data.map((d) => (
            <Cell key={d.label} fill={colorOf ? colorOf(d) : color} />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  )
}

/* ------------------------------- Gruplu çubuk ------------------------------- */

export function GroupedBars({
  rows,
  series,
  height = 300,
}: {
  rows: Record<string, string | number | null>[]
  series: { key: string; label: string; color: string }[]
  height?: number
}) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart data={rows} margin={{ top: 10, right: 8, bottom: 0, left: -18 }} barCategoryGap="22%" barGap={2}>
        <CartesianGrid stroke={CHART.grid} strokeDasharray="3 4" vertical={false} />
        <XAxis dataKey="name" {...axisProps} interval={0} tick={{ fill: CHART.axis, fontSize: 10 }} />
        <YAxis domain={[0, 100]} {...axisProps} width={44} />
        <Tooltip
          cursor={{ fill: 'hsl(var(--muted))', opacity: 0.5 }}
          content={(p) => {
            const d = p.payload?.[0]?.payload as Record<string, string | number | null> | undefined
            if (!p.active || !d) return null
            return (
              <TipBox title={String(d.fullName ?? d.name)}>
                {series.map((s) => (
                  <TipRow key={s.key} color={s.color} label={s.label} value={formatScore(d[s.key] as number | null)} />
                ))}
              </TipBox>
            )
          }}
        />
        {series.map((s, i) => (
          <Bar key={s.key} dataKey={s.key} name={s.label} fill={s.color} radius={[4, 4, 0, 0]} isAnimationActive animationDuration={700} animationBegin={i * 120} />
        ))}
      </BarChart>
    </ResponsiveContainer>
  )
}

export function ChartLegend({ items }: { items: { label: string; color: string; dashed?: boolean; hollow?: boolean }[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
      {items.map((i) => (
        <span key={i.label} className="inline-flex items-center gap-1.5">
          {i.hollow ? (
            <span className="size-2.5 rounded-full border-2 bg-card" style={{ borderColor: i.color }} />
          ) : i.dashed ? (
            <span className="h-0 w-4 border-t-2 border-dashed" style={{ borderColor: i.color }} />
          ) : (
            <span className="h-0.5 w-4 rounded-full" style={{ background: i.color }} />
          )}
          {i.label}
        </span>
      ))}
    </div>
  )
}
