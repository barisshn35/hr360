/**
 * Katmanlı puan ağacı:
 *
 *   Nihai puan 84,70
 *   ├─ Hedefler %40 → 84,00        katkı +33,60
 *   │   ├─ Q3 lansmanı (ağırlık 60) → %90
 *   └─ Metrikler %60 → 85,20       katkı +51,12
 *       ├─ Teknik (ağırlık 2,5) → 85,00
 *       │   ├─ Kod kalitesi → 91,67 (2 değerlendirme)
 *
 * Her satırda puan çubuğu ve nihai puana katkısı. Dallar açılır/kapanır.
 */

import { useState, type ReactNode } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { ChevronRight, Crosshair, Ruler } from 'lucide-react'
import { categoryColor, categoryLabels, formatScore, formatShare, formatWeight, type ScoreResult, type Thresholds } from '@/api/performance'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { Chip } from '../components/controls'
import { scoreColor } from '../components/score'
import type { Explained } from './explain'
import { tx, pct } from '@/lib/i18n'

const signed = (v: number) => `+${formatScore(v)}`

function Bar({ value, color }: { value: number | null; color: string }) {
  return (
    <span className="relative hidden h-1.5 w-24 shrink-0 overflow-hidden rounded-full bg-muted sm:block">
      {value !== null && (
        <motion.span
          className="absolute inset-y-0 left-0 rounded-full"
          style={{ background: color }}
          initial={{ width: 0 }}
          animate={{ width: `${Math.max(0, Math.min(100, value))}%` }}
          transition={{ duration: 0.9, ease: EASE }}
        />
      )}
    </span>
  )
}

function Row({
  depth,
  icon,
  label,
  meta,
  score,
  scoreText,
  color,
  contribution,
  dim,
  open,
  onToggle,
  children,
}: {
  depth: number
  icon?: ReactNode
  label: ReactNode
  meta?: ReactNode
  score: number | null
  scoreText?: string
  color: string
  contribution: number | null
  dim?: boolean
  open?: boolean
  onToggle?: () => void
  children?: ReactNode
}) {
  const expandable = Boolean(onToggle)
  return (
    <li className={cn('relative', depth > 0 && 'pl-5 sm:pl-7')}>
      {depth > 0 && (
        <>
          <span aria-hidden className="absolute top-0 left-2 h-[22px] w-3 rounded-bl-md border-b border-l border-border sm:left-3" />
          <span aria-hidden className="absolute top-0 bottom-0 left-2 border-l border-border sm:left-3 [li:last-child>&]:hidden" />
        </>
      )}
      <div
        role={expandable ? 'button' : undefined}
        tabIndex={expandable ? 0 : undefined}
        aria-expanded={expandable ? open : undefined}
        onClick={onToggle}
        onKeyDown={(e) => {
          if (expandable && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault()
            onToggle?.()
          }
        }}
        className={cn(
          'group flex items-center gap-2 rounded-lg px-2 py-2 transition-colors',
          expandable && 'cursor-pointer hover:bg-muted/60 focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none',
          dim && 'opacity-55',
        )}
      >
        <span className="flex size-4 shrink-0 items-center justify-center">
          {expandable && <ChevronRight className={cn('size-4 text-muted-foreground transition-transform', open && 'rotate-90')} aria-hidden />}
        </span>
        {icon}
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[13px] font-medium">{label}</span>
          {meta && <span className="block truncate text-[11px] text-muted-foreground">{meta}</span>}
        </span>
        <Bar value={score} color={color} />
        <span className="tabular w-14 shrink-0 text-right text-[14px] font-semibold" style={{ color: score === null ? undefined : color }}>
          {scoreText ?? formatScore(score)}
        </span>
        <span className="tabular hidden w-20 shrink-0 text-right text-[12px] md:block">
          {contribution !== null ? <span className="rounded-md bg-primary/8 px-1.5 py-0.5 font-medium text-primary">{signed(contribution)}</span> : <span className="text-muted-foreground">—</span>}
        </span>
      </div>
      <AnimatePresence initial={false}>
        {open && children && (
          <motion.ul initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: 'auto' }} exit={{ opacity: 0, height: 0 }} transition={{ duration: 0.3, ease: EASE }} className="overflow-hidden">
            {children}
          </motion.ul>
        )}
      </AnimatePresence>
    </li>
  )
}

export function ScoreTree({ score, ex, thresholds }: { score: ScoreResult; ex: Explained; thresholds: Thresholds | null }) {
  const [open, setOpen] = useState<Set<string>>(() => new Set(['goals', 'metrics', ...ex.categories.filter((c) => c.counted).slice(0, 1).map((c) => c.category)]))
  const toggle = (k: string) =>
    setOpen((s) => {
      const n = new Set(s)
      if (n.has(k)) n.delete(k)
      else n.add(k)
      return n
    })

  return (
    <div>
      <div className="mb-1 hidden items-center gap-2 px-2 text-[11px] font-medium text-muted-foreground md:flex">
        <span className="flex-1 pl-6">{tx('Katman')}</span>
        <span className="w-24 text-center">{tx('Puan')}</span>
        <span className="w-14 text-right">0–100</span>
        <span className="w-20 text-right">{tx('Katkı')}</span>
      </div>
      <ul className="tree">
        <Row
          depth={0}
          label={<span className="text-[15px] font-semibold">{tx('Nihai puan')}</span>}
          meta={tx('Sürüm {0} · {1} değerlendirme', [score.configVersion, score.reviewCount])}
          score={score.score}
          color={scoreColor(score.score, thresholds)}
          contribution={score.score}
          open
        >
          <Row
            depth={1}
            icon={<Crosshair className="size-4 shrink-0 text-muted-foreground" aria-hidden />}
            label={
              <>
                {tx('Hedefler')}{' '}<Chip className="ml-1">{ex.hasGoals ? pct(Math.round(ex.effGoalPct)) : tx('hedef yok')}</Chip>
              </>
            }
            meta={ex.hasGoals ? tx('{0} hedef · ilerlemelerin ağırlıklı ortalaması', [score.breakdown.goals.length]) : tx('Payı (%{0}) metriklere aktarıldı', [Math.round(ex.goalPct)])}
            score={ex.hasGoals ? score.goalScore : null}
            color="hsl(var(--foreground) / 0.75)"
            contribution={ex.hasGoals ? ex.goalContribution : null}
            dim={!ex.hasGoals}
            open={open.has('goals')}
            onToggle={ex.hasGoals ? () => toggle('goals') : undefined}
          >
            {ex.goals.map((g) => (
              <Row
                key={g.goalId}
                depth={2}
                label={g.title}
                meta={tx('ağırlık {0} · hedeflerin {1}', [formatWeight(g.weight), formatShare(g.share)])}
                score={g.achievement}
                scoreText={g.achievement === null ? '—' : pct(Math.round(g.achievement))}
                color="hsl(var(--foreground) / 0.6)"
                contribution={g.contribution}
              />
            ))}
          </Row>

          <Row
            depth={1}
            icon={<Ruler className="size-4 shrink-0 text-muted-foreground" aria-hidden />}
            label={
              <>
                {tx('Metrikler')}{' '}<Chip className="ml-1">{ex.hasMetrics ? pct(Math.round(ex.effMetricPct)) : tx('puan yok')}</Chip>
              </>
            }
            meta={ex.hasMetrics ? tx('kategori puanlarının ağırlıklı ortalaması') : tx('Henüz metrik puanı yok')}
            score={ex.hasMetrics ? score.metricScore : null}
            color="hsl(var(--primary))"
            contribution={ex.hasMetrics ? ex.metricContribution : null}
            dim={!ex.hasMetrics}
            open={open.has('metrics')}
            onToggle={() => toggle('metrics')}
          >
            {ex.categories.map((c) => (
              <Row
                key={c.category}
                depth={2}
                label={
                  <>
                    <span className="mr-1.5 inline-block size-2 rounded-full align-middle" style={{ background: categoryColor[c.category] }} />
                    {categoryLabels[c.category]}
                  </>
                }
                meta={
                  c.weight === 0
                    ? tx('ağırlık 0 — hesaba girmez')
                    : c.score === null
                      ? tx('ağırlık {0} · puanlanmadı', [formatWeight(c.weight)])
                      : tx('ağırlık {0} · metrik ayağının {1}', [formatWeight(c.weight), formatShare(c.share)])
                }
                score={c.score}
                color={categoryColor[c.category]}
                contribution={c.contribution}
                dim={!c.counted}
                open={open.has(c.category)}
                onToggle={() => toggle(c.category)}
              >
                {c.metrics.map((m) => (
                  <Row
                    key={m.metricId}
                    depth={3}
                    label={m.name}
                    meta={m.normalizedScore === null ? tx('değerlendirilmedi') : tx('ağırlık {0} · {1} değerlendirme', [formatWeight(m.weight), m.reviewCount])}
                    score={m.normalizedScore}
                    color={categoryColor[c.category]}
                    contribution={m.contribution}
                    dim={m.normalizedScore === null || !c.counted}
                  />
                ))}
              </Row>
            ))}
          </Row>
        </Row>
      </ul>
    </div>
  )
}
