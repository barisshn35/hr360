/**
 * Puan gösterim parçaları: halka, rozet, geçici puan bandı.
 *
 * Puan her yerde 0–100, iki ondalık, `tabular-nums`. Geçici puan kesik
 * çerçeve ve uyarı rengiyle ayırt edilir — "kesin" sanılmasın.
 */

import { motion, useReducedMotion } from 'motion/react'
import { AlertTriangle, Hourglass } from 'lucide-react'
import { bandLabels, bandOf, bandTone, formatScore, type Thresholds } from '@/api/performance'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { AnimatedNumber } from './AnimatedNumber'
import { tx } from '@/lib/i18n'

const TONE_COLOR = {
  danger: 'hsl(var(--destructive))',
  warning: 'hsl(var(--warning))',
  // Beklenen aralık: nötr ama canlı — gri halka 'veri yok' gibi okunuyordu.
  neutral: 'hsl(var(--chart-2))',
  success: 'hsl(var(--success))',
  info: 'hsl(var(--primary))',
}

export function scoreColor(score: number | null, t?: Thresholds | null): string {
  if (score === null || !t) return 'hsl(var(--primary))'
  return TONE_COLOR[bandTone[bandOf(score, t)]]
}

export function ScoreRing({
  score,
  thresholds,
  provisional,
  size = 168,
  label = tx('Nihai puan'),
}: {
  score: number | null
  thresholds?: Thresholds | null
  provisional?: boolean
  size?: number
  label?: string
}) {
  const reduced = useReducedMotion()
  const stroke = 12
  const r = (size - stroke) / 2 - 6
  const c = 2 * Math.PI * r
  const color = scoreColor(score, thresholds)
  const pct = score === null ? 0 : Math.max(0, Math.min(100, score)) / 100

  return (
    <div className="relative" style={{ width: size, height: size }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className="-rotate-90">
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="hsl(var(--muted))" strokeWidth={stroke} />
        <motion.circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          stroke={color}
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={c}
          initial={{ strokeDashoffset: c }}
          animate={{ strokeDashoffset: c * (1 - pct) }}
          transition={{ duration: reduced ? 0 : 1.4, ease: EASE }}
          style={{ opacity: provisional ? 0.55 : 1 }}
        />
        {provisional && (
          <circle cx={size / 2} cy={size / 2} r={r + stroke / 2 + 4} fill="none" stroke="hsl(var(--warning))" strokeWidth={1.5} strokeDasharray="3 5" />
        )}
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">
        <span className="text-[11px] font-medium text-muted-foreground">{label}</span>
        {score === null ? (
          <span className="text-[30px] leading-none font-semibold text-muted-foreground">—</span>
        ) : (
          <AnimatedNumber value={score} format={(v) => formatScore(v)} duration={1.2} className="text-[32px] leading-tight font-semibold tracking-tight" />
        )}
        {provisional && (
          <span className="mt-0.5 inline-flex items-center gap-1 text-[11px] font-medium text-[hsl(var(--warning))]">
            <Hourglass className="size-3" aria-hidden />
            {tx('geçici')}
          </span>
        )}
      </div>
    </div>
  )
}

export function ScoreBadge({
  score,
  thresholds,
  provisional,
  className,
}: {
  score: number | null
  thresholds?: Thresholds | null
  provisional?: boolean
  className?: string
}) {
  return (
    <span
      className={cn(
        'tabular inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[12px] font-semibold',
        provisional ? 'border border-dashed border-[hsl(var(--warning))]/60' : 'border border-transparent',
        className,
      )}
      style={{ color: scoreColor(score, thresholds), background: `color-mix(in oklab, ${scoreColor(score, thresholds)} 10%, transparent)` }}
      title={provisional ? tx('Geçici puan — az değerlendirmeye dayanıyor') : undefined}
    >
      {formatScore(score)}
      {provisional && <Hourglass className="size-3 text-[hsl(var(--warning))]" aria-label={tx('geçici')} />}
    </span>
  )
}

export function BandBadge({ score, thresholds }: { score: number | null; thresholds: Thresholds | null | undefined }) {
  if (score === null || !thresholds) return null
  const band = bandOf(score, thresholds)
  return <StatusBadge tone={bandTone[band]}>{bandLabels[band]}</StatusBadge>
}

export function ProvisionalBanner({ reason, className }: { reason: string | null; className?: string }) {
  return (
    <motion.div
      role="status"
      initial={{ opacity: 0, y: -8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.45, ease: EASE }}
      className={cn(
        'relative flex items-start gap-3 overflow-hidden rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 px-4 py-3',
        className,
      )}
    >
      <span aria-hidden className="absolute inset-y-0 left-0 w-1 bg-[hsl(var(--warning))]" />
      <AlertTriangle className="mt-0.5 size-5 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
      <div className="min-w-0">
        <p className="text-[14px] font-semibold">{tx('Bu puan geçici')}</p>
        <p className="mt-0.5 text-[13px] leading-relaxed text-foreground/80">
          {tx('{0} Yeni değerlendirmeler geldikçe değişebilir.', [reason ?? tx('Puan henüz yeterli değerlendirmeye dayanmıyor.')])}</p>
      </div>
    </motion.div>
  )
}
