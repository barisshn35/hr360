/**
 * Açıklanabilir öneri kartı.
 *
 *   ad + öneri rozeti + mevcut puan
 *   güven çubuğu
 *   özet cümle
 *   Neden: etkenler — sıfır ekseninden sağa yeşil (destekler), sola kırmızı
 *          (zayıflatır); backend'in sırası korunur
 *   Dikkat: cautions[]
 *   ML sinyalleri (karar vermez) — görsel olarak ayrı, kesik çerçeve
 */

import { Link } from 'react-router-dom'
import { motion } from 'motion/react'
import { AlertTriangle, BrainCircuit, Gauge, MessageSquarePlus } from 'lucide-react'
import { actionLabelsFallback, formatSigned, toneOfAction, type Recommendation, type Thresholds } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { PersonAvatar } from '../components/people'
import { ScoreBadge } from '../components/score'
import { tx, appLocale } from '@/lib/i18n'

const TONE_COLOR = {
  danger: 'hsl(var(--destructive))',
  warning: 'hsl(var(--warning))',
  neutral: 'hsl(var(--muted-foreground))',
  success: 'hsl(var(--success))',
  info: 'hsl(var(--primary))',
}

export function ConfidenceMeter({ value }: { value: number }) {
  const label = value >= 0.75 ? tx('yüksek') : value >= 0.5 ? 'orta' : tx('düşük')
  const filled = Math.round(value * 10)
  return (
    <div className="flex items-center gap-2.5" aria-label={tx('Güven {0} ({1})', [value.toFixed(2), label])}>
      <span className="text-[11px] text-muted-foreground">{tx('Güven')}</span>
      <span className="flex gap-0.5" aria-hidden>
        {Array.from({ length: 10 }).map((_, i) => (
          <motion.span
            key={i}
            initial={{ scaleY: 0.3, opacity: 0.3 }}
            animate={{ scaleY: 1, opacity: i < filled ? 1 : 0.25 }}
            transition={{ delay: 0.15 + i * 0.04, duration: 0.3 }}
            className={cn('h-3 w-2 origin-bottom rounded-[2px]', i < filled ? 'bg-primary' : 'bg-muted-foreground/40')}
          />
        ))}
      </span>
      <span className="tabular text-[12px] font-semibold">{value.toLocaleString(appLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</span>
      <span className="text-[11px] text-muted-foreground">{label}</span>
    </div>
  )
}

export function FactorBars({ factors }: { factors: Recommendation['factors'] }) {
  const max = Math.max(1, ...factors.map((f) => Math.abs(f.contribution)))
  return (
    <ol className="flex flex-col gap-2">
      {factors.map((f, i) => {
        const pos = f.contribution >= 0
        const w = (Math.abs(f.contribution) / max) * 50
        const color = pos ? 'hsl(var(--success))' : 'hsl(var(--destructive))'
        return (
          <li key={`${f.code}-${i}`} className="grid grid-cols-[minmax(0,1fr)] gap-1 sm:grid-cols-[140px_minmax(0,1fr)_52px] sm:items-center sm:gap-3">
            <span className="truncate text-[12px] font-medium">{f.label}</span>
            <span className="relative h-3.5 rounded-sm bg-muted/60" aria-hidden>
              <span className="absolute inset-y-0 left-1/2 w-px bg-border" />
              <motion.span
                className="absolute inset-y-0.5 rounded-sm"
                style={{ background: color, [pos ? 'left' : 'right']: '50%' }}
                initial={{ width: 0 }}
                animate={{ width: `${w}%` }}
                transition={{ duration: 0.7, ease: EASE, delay: 0.1 + i * 0.07 }}
              />
            </span>
            <span className="tabular text-[12px] font-semibold sm:text-right" style={{ color }}>
              {formatSigned(f.contribution)}
            </span>
            <span className="text-[11px] leading-relaxed text-muted-foreground sm:col-span-3 sm:pl-[152px]">{f.explanation}</span>
          </li>
        )
      })}
    </ol>
  )
}

export function RecommendationCard({
  rec,
  name,
  thresholds,
  index,
  highlighted,
}: {
  rec: Recommendation
  name: string
  thresholds: Thresholds | null
  index: number
  highlighted?: boolean
}) {
  const tone = toneOfAction(rec.action)
  const color = TONE_COLOR[tone]
  const label = rec.actionLabel || (actionLabelsFallback as Record<string, string>)[rec.action] || tx('Öneri')

  return (
    <motion.article
      id={`rec-${rec.employeeId}`}
      layout
      initial={{ opacity: 0, y: 14 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.5, ease: EASE, delay: Math.min(index, 8) * 0.06 }}
      className={cn('relative flex flex-col overflow-hidden rounded-2xl border bg-card', highlighted ? 'border-primary ring-2 ring-primary/30' : 'border-border')}
    >
      <span aria-hidden className="absolute inset-x-0 top-0 h-1" style={{ background: color }} />
      <div className="flex flex-col gap-4 p-5">
        <div className="flex items-start justify-between gap-3">
          <div className="flex min-w-0 items-center gap-3">
            <PersonAvatar id={rec.employeeId} name={name} size="md" />
            <div className="min-w-0">
              <p className="truncate text-[15px] font-semibold">{name}</p>
              <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground">
                <Gauge className="size-3.5" aria-hidden />
                {tx('Mevcut puan')}{' '}<ScoreBadge score={rec.currentScore} thresholds={thresholds} />
              </p>
            </div>
          </div>
          <StatusBadge tone={tone} className="shrink-0 text-[12px]">
            {label}
          </StatusBadge>
        </div>

        <ConfidenceMeter value={rec.confidence} />
        <p className="text-[14px] leading-relaxed font-medium">{rec.summary}</p>

        {rec.factors.length > 0 && (
          <div>
            <p className="mb-2 text-[12px] font-semibold text-muted-foreground">{tx('Neden?')}</p>
            <FactorBars factors={rec.factors} />
          </div>
        )}

        {rec.cautions.length > 0 && (
          <div className="rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2.5">
            <p className="mb-1 flex items-center gap-1.5 text-[12px] font-semibold">
              <AlertTriangle className="size-3.5 text-[hsl(var(--warning))]" aria-hidden />
              {tx('Dikkat')}
            </p>
            <ul className="flex flex-col gap-0.5 text-[12px] leading-relaxed text-foreground/80">
              {rec.cautions.map((c) => (
                <li key={c}>• {c}</li>
              ))}
            </ul>
          </div>
        )}
      </div>

      {rec.mlLayer && (
        <div className="mt-auto border-t border-dashed border-border bg-muted/30 px-5 py-3.5">
          <p className="mb-1 flex items-center gap-1.5 text-[11px] font-semibold tracking-wide text-muted-foreground uppercase">
            <BrainCircuit className="size-3.5" aria-hidden />
            {tx('ML sinyalleri · karar vermez')}
          </p>
          <p className="text-[11px] text-muted-foreground">{rec.mlLayer.note}</p>
          {rec.mlLayer.skipReason ? (
            <p className="mt-1.5 text-[12px]">
              <span className="font-medium">{tx('Çalışmadı:')}</span> <span className="text-muted-foreground">{rec.mlLayer.skipReason}</span>
            </p>
          ) : rec.mlLayer.signals.length ? (
            <ul className="mt-1.5 flex flex-col gap-1">
              {rec.mlLayer.signals.map((s) => (
                <li key={s.code} className="flex flex-wrap items-baseline gap-x-2 text-[12px]">
                  <span className="font-medium">{s.label}:</span>
                  <span>{s.value}</span>
                  {s.confidenceLabel && <span className="text-[11px] text-muted-foreground">{tx('(güven: {0})', [s.confidenceLabel])}</span>}
                </li>
              ))}
            </ul>
          ) : (
            <p className="mt-1.5 text-[12px] text-muted-foreground">{tx('Bu çalışan için sinyal üretilmedi.')}</p>
          )}
        </div>
      )}

      <div className="flex flex-wrap gap-2 border-t border-border px-5 py-3">
        <Button asChild size="sm" variant="outline">
          <Link to={`/panel/performans/puan?calisan=${rec.employeeId}`}>{tx('Puan dökümü')}</Link>
        </Button>
        <Button asChild size="sm" variant="ghost">
          <Link to={`/panel/performans/geri-bildirim?sekme=calisan&calisan=${rec.employeeId}`}>
            <MessageSquarePlus aria-hidden />
            {tx('Geri bildirimler')}
          </Link>
        </Button>
      </div>
    </motion.article>
  )
}

