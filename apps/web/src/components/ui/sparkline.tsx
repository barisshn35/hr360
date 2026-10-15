/**
 * Küçük satır içi trend grafiği (KPI kartı, tablo satırı, panel başlığı).
 *
 * Kaynak: 21st.dev "Sparkline" (appica-dev, id 34512). HR360 uyarlamaları:
 *  - Bileşen ağacı (Sparkline/SparklineChart/SparklineValue bağlamı) tek bir
 *    bileşene indirildi; panelde yalnızca çizgi/alan varyantı kullanılıyor.
 *  - Renk varsayılanı tasarım token'ı (--primary); kiracının marka rengini izler.
 *  - Değerler tr-TR biçiminde, ipucu kutusu panel yüzey diline uyarlandı.
 *  - RTL ve sütun varyantı çıkarıldı (arayüz yalnızca Türkçe).
 */

import { useId, useMemo, useRef, useState } from 'react'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'

type PathPoint = { x: number; y: number; leftFrac: number; topFrac: number }

export interface SparklineProps {
  data: number[]
  labels?: string[]
  /** Alan dolgusu (varsayılan açık). */
  fill?: boolean
  /** CSS renk; verilmezse --primary. */
  color?: string
  height?: number
  strokeWidth?: number
  /** 0 düz, 1 tam yumuşak. */
  curve?: number
  /** Üzerine gelince nokta + değer ipucu. */
  interactive?: boolean
  format?: (value: number) => string
  className?: string
  'aria-label'?: string
}

const round = (n: number) => Math.round(n * 100) / 100
const clamp01 = (n: number) => Math.min(1, Math.max(0, n))

function buildLinePath(points: PathPoint[], smoothing: number): string {
  if (points.length === 0) return ''
  let d = `M ${points[0].x} ${points[0].y}`
  for (let i = 0; i < points.length - 1; i++) {
    const p1 = points[i]
    const p2 = points[i + 1]
    const p0 = points[i - 1] ?? p1
    const p3 = points[i + 2] ?? p2
    const cp1x = p1.x + ((p2.x - p0.x) / 6) * smoothing
    const cp1y = p1.y + ((p2.y - p0.y) / 6) * smoothing
    const cp2x = p2.x - ((p3.x - p1.x) / 6) * smoothing
    const cp2y = p2.y - ((p3.y - p1.y) / 6) * smoothing
    d += ` C ${round(cp1x)} ${round(cp1y)}, ${round(cp2x)} ${round(cp2y)}, ${p2.x} ${p2.y}`
  }
  return d
}

const defaultFormat = (v: number) => v.toLocaleString(appLocale)

export function Sparkline({
  data,
  labels,
  fill = true,
  color = 'hsl(var(--primary))',
  height = 44,
  strokeWidth = 1.75,
  curve = 0.5,
  interactive = true,
  format = defaultFormat,
  className,
  'aria-label': ariaLabel = tx('Trend grafiği'),
}: SparklineProps) {
  const gradientId = useId()
  const [active, setActive] = useState<number | null>(null)
  const rectRef = useRef<DOMRect | null>(null)
  const n = data.length

  const geom = useMemo(() => {
    if (n === 0) return null
    const min = Math.min(...data)
    const max = Math.max(...data)
    const span = max - min || 1
    const inset = strokeWidth + 1
    const plot = height - inset * 2
    const points = data.map<PathPoint>((value, i) => {
      const xFrac = n === 1 ? 0.5 : i / (n - 1)
      const y = inset + (1 - (value - min) / span) * plot
      return { x: round(xFrac * 100), y: round(y), leftFrac: xFrac, topFrac: y / height }
    })
    const d = buildLinePath(points, clamp01(curve))
    const fillPath = `${d} L ${points[n - 1].x} ${height} L ${points[0].x} ${height} Z`
    return { d, fillPath, points }
  }, [data, n, height, strokeWidth, curve])

  if (!geom) return null
  const marker = active !== null ? geom.points[active] : null

  const onMove = (e: React.PointerEvent<HTMLDivElement>, fresh: boolean) => {
    const rect = fresh || !rectRef.current ? (rectRef.current = e.currentTarget.getBoundingClientRect()) : rectRef.current
    if (rect.width === 0) return
    setActive(Math.round(clamp01((e.clientX - rect.left) / rect.width) * (n - 1)))
  }

  return (
    <div
      role="img"
      aria-label={ariaLabel}
      className={cn('relative w-full', className)}
      style={{ height, ['--spark' as string]: color }}
      onPointerEnter={interactive ? (e) => onMove(e, true) : undefined}
      onPointerMove={interactive ? (e) => onMove(e, false) : undefined}
      onPointerLeave={
        interactive
          ? () => {
              rectRef.current = null
              setActive(null)
            }
          : undefined
      }
    >
      <svg
        width="100%"
        height={height}
        viewBox={`0 0 100 ${height}`}
        preserveAspectRatio="none"
        aria-hidden="true"
        className="block overflow-visible"
      >
        {fill && (
          <>
            <defs>
              <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="var(--spark)" stopOpacity={0.32} />
                <stop offset="100%" stopColor="var(--spark)" stopOpacity={0} />
              </linearGradient>
            </defs>
            <path d={geom.fillPath} fill={`url(#${gradientId})`} />
          </>
        )}
        <path
          d={geom.d}
          fill="none"
          stroke="var(--spark)"
          strokeWidth={strokeWidth}
          strokeLinecap="round"
          strokeLinejoin="round"
          vectorEffect="non-scaling-stroke"
        />
      </svg>

      {marker && (
        <>
          <span
            aria-hidden="true"
            className="pointer-events-none absolute inset-y-0 w-px -translate-x-1/2 bg-[var(--spark)] opacity-25"
            style={{ left: `${marker.leftFrac * 100}%` }}
          />
          <span
            aria-hidden="true"
            className="pointer-events-none absolute size-2.5 -translate-x-1/2 -translate-y-1/2 rounded-full bg-[var(--spark)] ring-2 ring-card"
            style={{ left: `${marker.leftFrac * 100}%`, top: `${marker.topFrac * 100}%` }}
          />
          <div
            aria-hidden="true"
            className="pointer-events-none absolute z-10 w-max -translate-x-1/2 -translate-y-full pb-2"
            style={{ left: `${marker.leftFrac * 100}%`, top: `${marker.topFrac * 100}%` }}
          >
            <div className="flex items-center gap-1.5 rounded-md border border-border bg-popover px-2 py-1 text-[11px] whitespace-nowrap shadow-popover">
              <span className="size-2 shrink-0 rounded-[3px] bg-[var(--spark)]" />
              {labels?.[active!] && <span className="text-muted-foreground">{labels[active!]}</span>}
              <span className="tabular font-medium">{format(data[active!])}</span>
            </div>
          </div>
        </>
      )}
    </div>
  )
}
