/**
 * Kategori ağırlıkları — oransal. Sıfır verilen kategori hesaba hiç girmez.
 * Her satırda kategorinin metrik ayağındaki payı canlı hesaplanır.
 */

import { AnimatePresence, motion } from 'motion/react'
import {
  CATEGORIES,
  categoryColor,
  categoryHints,
  categoryLabels,
  categoryWeightKey,
  formatShare,
  shareOf,
  type Metric,
  type ScoringConfigInput,
} from '@/api/performance'
import { cn } from '@/lib/utils'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { Chip, Slider } from '../components/controls'
import { NumberStepper } from '../components/NumberStepper'
import { ShareBar } from '../components/WeightShare'
import { tx } from '@/lib/i18n'

export function CategoryWeights({
  draft,
  onChange,
  metrics,
}: {
  draft: ScoringConfigInput
  onChange: (key: keyof ScoringConfigInput, value: number) => void
  metrics: Metric[] | undefined
}) {
  const weights = CATEGORIES.map((c) => ({ category: c, key: categoryWeightKey[c], weight: draft[categoryWeightKey[c]] as number }))
  const total = weights.reduce((a, w) => a + Math.max(0, w.weight), 0)
  const allZero = total === 0

  return (
    <div>
      <ShareBar
        items={weights.map((w) => ({ id: w.category, label: categoryLabels[w.category], weight: w.weight, color: categoryColor[w.category] }))}
        height={12}
        showLabels
      />

      <AnimatePresence>
        {allZero && (
          <motion.p
            role="alert"
            initial={{ opacity: 0, height: 0 }}
            animate={{ opacity: 1, height: 'auto' }}
            exit={{ opacity: 0, height: 0 }}
            className="mt-3 overflow-hidden rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2 text-[12px] text-destructive"
          >
            {tx('En az bir kategorinin ağırlığı sıfırdan büyük olmalı; aksi hâlde metrik ayağı hesaplanamaz.')}
          </motion.p>
        )}
      </AnimatePresence>

      <ul className="mt-4 divide-y divide-border">
        {weights.map((w) => {
          const count = metrics?.filter((m) => m.isActive && m.category === w.category).length
          const excluded = w.weight === 0
          const share = shareOf(w.weight, total)
          return (
            <li key={w.category} className={cn('grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-2 py-3 sm:grid-cols-[minmax(0,1fr)_160px_auto_64px]', excluded && 'opacity-70')}>
              <div className="min-w-0">
                <p className="flex flex-wrap items-center gap-1.5 text-[13px] font-medium">
                  <span className="size-2.5 rounded-full" style={{ background: categoryColor[w.category] }} />
                  {categoryLabels[w.category]}
                  {count !== undefined && <span className="text-[12px] font-normal text-muted-foreground">{tx('· {0} metrik', [count])}</span>}
                  {excluded && <Chip tone="warning">{tx('Hesaba girmez')}</Chip>}
                  {!excluded && count === 0 && <Chip tone="warning">{tx('Metriği yok — katkı vermez')}</Chip>}
                </p>
                <p className="mt-0.5 text-[12px] text-muted-foreground">{categoryHints[w.category]}</p>
              </div>
              <Slider
                className="order-last col-span-2 sm:order-none sm:col-span-1"
                value={[Math.min(5, w.weight)]}
                onChange={([v]) => onChange(w.key, v)}
                min={0}
                max={5}
                step={0.5}
                ariaLabel={tx('{0} ağırlığı', [categoryLabels[w.category]])}
                rangeClassName="opacity-80"
              />
              <NumberStepper
                value={w.weight}
                onChange={(v) => onChange(w.key, v)}
                min={0}
                max={10}
                step={0.5}
                decimals={2}
                ariaLabel={tx('{0} ağırlığı', [categoryLabels[w.category]])}
              />
              <p className="hidden text-right sm:block">
                <span className="block text-[11px] text-muted-foreground">{tx('Pay')}</span>
                <span className="text-[14px] font-semibold" style={{ color: excluded ? undefined : categoryColor[w.category] }}>
                  {excluded ? '—' : <>%<AnimatedNumber value={Math.round(share)} /></>}
                </span>
              </p>
            </li>
          )
        })}
      </ul>
      <p className="mt-2 text-[12px] text-muted-foreground sm:hidden">{tx('Paylar: {0}', [weights.filter((w) => w.weight > 0).map((w) => `${categoryLabels[w.category]} ${formatShare(shareOf(w.weight, total))}`).join(' · ')])}
      </p>
    </div>
  )
}
