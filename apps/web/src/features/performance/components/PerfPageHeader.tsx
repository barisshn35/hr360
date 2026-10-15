/**
 * Performans ekranlarının üst bandı ve kurulum izi.
 *
 * Kurulum ekranları (metrik → puanlama ayarı → dönem → hedef) birbirine
 * bağlı; `SetupTrail` hangi adımın tamam, hangisinin eksik olduğunu canlı
 * veriden okur ve bir sonrakine tek tıkla götürür.
 */

import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { motion } from 'motion/react'
import { Check } from 'lucide-react'
import { useGoals, useMetrics, useScoringConfig } from '@/api/performance'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { useCurrentCycle } from '../hooks'
import { tx } from '@/lib/i18n'

export function PerfPageHeader({
  eyebrow,
  title,
  description,
  actions,
  children,
}: {
  eyebrow: string
  title: string
  description?: ReactNode
  actions?: ReactNode
  children?: ReactNode
}) {
  return (
    <header className="relative mb-6 overflow-hidden rounded-2xl border border-border bg-card">
      {/* Yumuşak ışık kütleleri — yalnızca token renkleri, düşük opaklık. */}
      <div aria-hidden className="pointer-events-none absolute inset-0">
        <div className="hr-aurora absolute -top-24 -left-16 h-56 w-72 rounded-full bg-primary/10 blur-3xl" />
        <div className="hr-aurora hr-aurora-slow hr-aurora-delay absolute -right-10 -bottom-28 h-64 w-80 rounded-full bg-[hsl(var(--chart-2))]/10 blur-3xl" />
        <div className="hr-dots absolute inset-0 opacity-40" />
      </div>

      <div className="relative flex flex-col gap-4 p-5 sm:p-6 lg:flex-row lg:items-end lg:justify-between">
        <div className="min-w-0">
          <motion.p
            initial={{ opacity: 0, y: 6 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.5, ease: EASE }}
            className="text-[12px] font-semibold tracking-wide text-primary"
          >
            {eyebrow}
          </motion.p>
          <motion.h1
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.6, ease: EASE, delay: 0.05 }}
            className="mt-1 text-[24px] leading-tight font-semibold tracking-tight sm:text-[26px]"
          >
            {title}
          </motion.h1>
          {description && (
            <motion.div
              initial={{ opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.6, ease: EASE, delay: 0.1 }}
              className="mt-1.5 max-w-2xl text-[13px] leading-relaxed text-muted-foreground"
            >
              {description}
            </motion.div>
          )}
        </div>
        {actions && (
          <motion.div
            initial={{ opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.6, ease: EASE, delay: 0.15 }}
            className="flex shrink-0 flex-wrap items-center gap-2"
          >
            {actions}
          </motion.div>
        )}
      </div>
      {children && <div className="relative border-t border-border/70 bg-background/40 px-5 py-4 backdrop-blur-sm sm:px-6">{children}</div>}
    </header>
  )
}

/* ---------------------------------- Kurulum izi --------------------------------- */

type StepKey = 'metrics' | 'scoring' | 'cycles' | 'goals'

const STEPS: { key: StepKey; label: string; to: string }[] = [
  { key: 'metrics', label: tx('Metrikler'), to: '/panel/performans/metrikler' },
  { key: 'scoring', label: tx('Puanlama ayarı'), to: '/panel/performans/ayarlar' },
  { key: 'cycles', label: tx('Dönem'), to: '/panel/performans/donemler' },
  { key: 'goals', label: tx('Hedefler'), to: '/panel/performans/hedefler' },
]

export function SetupTrail({ current }: { current: StepKey }) {
  const metrics = useMetrics()
  const scoring = useScoringConfig()
  const { current: cycle } = useCurrentCycle()
  const active = cycle?.status === 'Open' ? cycle : null
  const goals = useGoals({ cycleId: active?.id }, Boolean(active))

  const done: Record<StepKey, boolean> = {
    metrics: (metrics.data?.length ?? 0) > 0,
    scoring: Boolean(scoring.data?.version),
    cycles: Boolean(active),
    goals: (goals.data?.length ?? 0) > 0,
  }

  return (
    <nav aria-label={tx('Kurulum adımları')} className="no-scrollbar -mx-1 overflow-x-auto px-1 py-0.5">
      <ol className="flex min-w-max items-center gap-1.5">
        {STEPS.map((s, i) => {
          const isCurrent = s.key === current
          const ok = done[s.key]
          return (
            <li key={s.key} className="flex items-center gap-1.5">
              {i > 0 && (
                <span aria-hidden className="relative h-px w-6 overflow-hidden bg-border sm:w-10">
                  <motion.span
                    className="absolute inset-y-0 left-0 bg-primary"
                    initial={{ width: 0 }}
                    animate={{ width: done[STEPS[i - 1].key] ? '100%' : '0%' }}
                    transition={{ duration: 0.8, ease: EASE, delay: 0.2 + i * 0.12 }}
                  />
                </span>
              )}
              <Link
                to={s.to}
                aria-current={isCurrent ? 'step' : undefined}
                className={cn(
                  'group inline-flex items-center gap-2 rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors',
                  isCurrent
                    ? 'border-primary/40 bg-primary/10 text-foreground'
                    : 'border-border bg-background/70 text-muted-foreground hover:border-primary/30 hover:text-foreground',
                )}
              >
                <motion.span
                  initial={{ scale: 0.6, opacity: 0 }}
                  animate={{ scale: 1, opacity: 1 }}
                  transition={{ type: 'spring', stiffness: 500, damping: 26, delay: 0.1 + i * 0.1 }}
                  className={cn(
                    'flex size-4 items-center justify-center rounded-full text-[10px] font-bold',
                    ok ? 'bg-[hsl(var(--success))] text-background' : isCurrent ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground',
                  )}
                >
                  {ok ? <Check className="size-2.5" strokeWidth={3} /> : i + 1}
                </motion.span>
                {s.label}
              </Link>
            </li>
          )
        })}
      </ol>
    </nav>
  )
}
