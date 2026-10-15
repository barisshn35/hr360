/**
 * Şablon seçici — hiç metrik yokken boş durumun yerini alır.
 * Kullanıcı sıfırdan kurmak zorunda kalmaz: tek tıkla 5 metrik gelir,
 * sonra istediği gibi düzenler.
 */

import { useState } from 'react'
import { motion } from 'motion/react'
import { ArrowRight, Code2, Layers, LoaderCircle, Plus, TrendingUp } from 'lucide-react'
import { TEMPLATES, templateInfo, type MetricTemplate } from '@/api/performance'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { tx } from '@/lib/i18n'

const ICONS: Record<MetricTemplate, React.ElementType> = {
  genel: Layers,
  yazilim: Code2,
  satis: TrendingUp,
}

const ACCENT: Record<MetricTemplate, string> = {
  genel: 'hsl(var(--chart-1))',
  yazilim: 'hsl(var(--chart-2))',
  satis: 'hsl(var(--chart-3))',
}

export function TemplatePicker({
  onApply,
  pending,
  onStartBlank,
  compact = false,
}: {
  onApply: (template: MetricTemplate) => void
  /** Uygulanmakta olan şablon. */
  pending: MetricTemplate | null
  onStartBlank?: () => void
  compact?: boolean
}) {
  const [hovered, setHovered] = useState<MetricTemplate | null>(null)

  return (
    <div>
      <div className={cn('grid gap-3', compact ? 'sm:grid-cols-3' : 'md:grid-cols-3')}>
        {TEMPLATES.map((t, i) => {
          const Icon = ICONS[t]
          const info = templateInfo[t]
          const busy = pending === t
          const disabled = pending !== null && !busy
          return (
            <motion.button
              key={t}
              type="button"
              disabled={pending !== null}
              onClick={() => onApply(t)}
              onMouseEnter={() => setHovered(t)}
              onMouseLeave={() => setHovered(null)}
              initial={{ opacity: 0, y: 18 }}
              animate={{ opacity: disabled ? 0.5 : 1, y: 0 }}
              transition={{ duration: 0.55, ease: EASE, delay: 0.08 * i }}
              whileHover={pending ? undefined : { y: -4 }}
              className={cn(
                'group relative flex flex-col overflow-hidden rounded-xl border bg-card p-5 text-left transition-[border-color,box-shadow] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
                busy ? 'border-primary shadow-lg' : 'border-border hover:border-primary/40 hover:shadow-md',
              )}
            >
              <span
                aria-hidden
                className="pointer-events-none absolute -top-16 -right-16 size-40 rounded-full opacity-0 blur-2xl transition-opacity duration-500 group-hover:opacity-100"
                style={{ background: `color-mix(in oklab, ${ACCENT[t]} 22%, transparent)` }}
              />
              <span
                className="relative flex size-10 items-center justify-center rounded-lg"
                style={{ background: `color-mix(in oklab, ${ACCENT[t]} 14%, transparent)`, color: ACCENT[t] }}
              >
                <Icon className="size-5" strokeWidth={1.75} aria-hidden />
              </span>
              <span className="relative mt-4 text-[16px] font-semibold">{info.title}</span>
              <span className="relative mt-1 text-[13px] leading-relaxed text-muted-foreground">{info.detail}</span>

              <ul className="relative mt-4 flex flex-wrap gap-1.5">
                {info.metrics.map((m, mi) => (
                  <motion.li
                    key={m}
                    initial={false}
                    animate={{ y: hovered === t ? -1 : 0 }}
                    transition={{ delay: mi * 0.03 }}
                    className="rounded-md border border-border bg-background px-2 py-0.5 text-[11px] font-medium text-muted-foreground"
                  >
                    {m}
                  </motion.li>
                ))}
              </ul>

              <span className="relative mt-5 inline-flex items-center gap-1.5 text-[13px] font-semibold" style={{ color: ACCENT[t] }}>
                {busy ? (
                  <>
                    <LoaderCircle className="size-4 animate-spin" aria-hidden />
                    {tx('Metrikler ekleniyor…')}
                  </>
                ) : (
                  <>
                    {tx('Bu şablonla başla')}
                    <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" aria-hidden />
                  </>
                )}
              </span>
            </motion.button>
          )
        })}
      </div>

      {onStartBlank && (
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          transition={{ delay: 0.4 }}
          className="mt-5 flex flex-col items-center gap-2 text-center"
        >
          <p className="text-[13px] text-muted-foreground">{tx('Şablonlar yalnızca başlangıç; eklenen her metriği sonradan düzenleyebilir ya da arşivleyebilirsiniz.')}</p>
          <Button variant="ghost" size="sm" onClick={onStartBlank} disabled={pending !== null}>
            <Plus aria-hidden />
            {tx('Sıfırdan kendi metriğimi oluşturacağım')}
          </Button>
        </motion.div>
      )}
    </div>
  )
}
