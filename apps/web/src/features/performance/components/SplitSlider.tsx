/**
 * İki paylı tek kaydırıcı — toplam her zaman 100.
 *
 * Kullanıcı iki ayrı sayı girip "toplam 100 değil" hatası almasın diye tek
 * tutamak var: sol taraf A'nın payı, kalan B'nin. Hazır oranlar tek tıkla.
 */

import { motion } from 'motion/react'
import { Slider as SliderPrimitive } from 'radix-ui'
import { cn } from '@/lib/utils'
import { AnimatedNumber } from './AnimatedNumber'
import { tx } from '@/lib/i18n'

export function SplitSlider({
  value,
  onChange,
  left,
  right,
  leftColor = 'hsl(var(--chart-1))',
  rightColor = 'hsl(var(--chart-2))',
  presets = [30, 40, 50, 60],
  step = 5,
  ariaLabel,
}: {
  /** Sol tarafın payı (0–100). Sağ = 100 − value. */
  value: number
  onChange: (value: number) => void
  left: string
  right: string
  leftColor?: string
  rightColor?: string
  presets?: number[]
  step?: number
  ariaLabel: string
}) {
  return (
    <div>
      <div className="mb-3 flex items-end justify-between gap-4">
        <div>
          <p className="flex items-center gap-1.5 text-[12px] font-medium text-muted-foreground">
            <span className="size-2 rounded-full" style={{ background: leftColor }} />
            {left}
          </p>
          <p className="text-[32px] leading-none font-semibold tracking-tight" style={{ color: leftColor }}>
            %<AnimatedNumber value={value} />
          </p>
        </div>
        <div className="text-right">
          <p className="flex items-center justify-end gap-1.5 text-[12px] font-medium text-muted-foreground">
            {right}
            <span className="size-2 rounded-full" style={{ background: rightColor }} />
          </p>
          <p className="text-[32px] leading-none font-semibold tracking-tight" style={{ color: rightColor }}>
            %<AnimatedNumber value={100 - value} />
          </p>
        </div>
      </div>

      <SliderPrimitive.Root
        value={[value]}
        onValueChange={([v]) => onChange(v)}
        min={0}
        max={100}
        step={step}
        aria-label={ariaLabel}
        className="relative flex h-8 w-full touch-none items-center select-none"
      >
        <SliderPrimitive.Track className="relative h-4 w-full grow overflow-hidden rounded-full" style={{ background: rightColor }}>
          <SliderPrimitive.Range className="absolute h-full" style={{ background: leftColor }} />
          {/* Ölçek çizgileri */}
          <div aria-hidden className="pointer-events-none absolute inset-0 flex justify-between px-[1px]">
            {Array.from({ length: 11 }).map((_, i) => (
              <span key={i} className={cn('h-full w-px bg-background/30', (i === 0 || i === 10) && 'opacity-0')} />
            ))}
          </div>
        </SliderPrimitive.Track>
        <SliderPrimitive.Thumb
          aria-label={tx('{0} payı', [left])}
          className="block size-7 cursor-grab rounded-full border-[3px] border-background bg-card shadow-lg ring-1 ring-foreground/15 transition-transform hover:scale-110 focus-visible:ring-[4px] focus-visible:ring-ring/40 focus-visible:outline-none active:cursor-grabbing active:scale-110"
        />
      </SliderPrimitive.Root>

      <div className="mt-3 flex flex-wrap items-center gap-1.5">
        <span className="mr-1 text-[12px] text-muted-foreground">{tx('Hazır oranlar:')}</span>
        {presets.map((p) => (
          <motion.button
            key={p}
            type="button"
            whileTap={{ scale: 0.94 }}
            onClick={() => onChange(p)}
            className={cn(
              'tabular rounded-full border px-2.5 py-0.5 text-[12px] font-medium transition-colors',
              p === value ? 'border-primary bg-primary text-primary-foreground' : 'border-border hover:border-primary/40',
            )}
          >
            {p}/{100 - p}
          </motion.button>
        ))}
      </div>
    </div>
  )
}
