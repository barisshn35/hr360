/**
 * Ölçeğe göre puan girişi — değerlendirme formunun kalbi.
 *
 *   OneToFive  → yıldızlar (1–5)
 *   OneToTen   → 1–10 segment
 *   Percentage → kaydırıcı + sayı
 *
 * Sınırlar her zaman metriğin `range` alanından gelir; ölçek yalnızca hangi
 * görsel girdinin kullanılacağını belirler. Klavye: ok tuşları değeri
 * değiştirir, Delete/Backspace temizler.
 */

import { useState, type KeyboardEvent } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { Percent, Star } from 'lucide-react'
import type { MetricScale, ScaleRange } from '@/api/performance'
import { normalizeToHundred, formatScore } from '@/api/performance'
import { cn } from '@/lib/utils'
import { Slider } from './controls'
import { tx } from '@/lib/i18n'

const FIVE_LABELS = [tx('Beklentinin çok altında'), tx('Beklentinin altında'), tx('Beklentiyi karşılıyor'), tx('Beklentinin üzerinde'), tx('Olağanüstü')]

function describe(scale: MetricScale, range: ScaleRange, value: number): string {
  if (scale === 'OneToFive' && range.max - range.min === 4) return FIVE_LABELS[value - range.min] ?? ''
  const n = normalizeToHundred(range, value)
  if (n < 20) return tx('Beklentinin çok altında')
  if (n < 45) return tx('Beklentinin altında')
  if (n < 70) return tx('Beklentiyi karşılıyor')
  if (n < 90) return tx('Beklentinin üzerinde')
  return tx('Olağanüstü')
}

export interface ScaleInputProps {
  scale: MetricScale
  range: ScaleRange
  value: number | null
  onChange: (value: number | null) => void
  disabled?: boolean
  invalid?: boolean
  /** Görünür etiketin kimliği — ekran okuyucu grubu adlandırır. */
  labelledBy?: string
  /** Seçilen değerin 0–100 karşılığını da göster. */
  showNormalized?: boolean
  size?: 'sm' | 'md'
}

export function ScaleInput(props: ScaleInputProps) {
  const { scale } = props
  if (scale === 'Percentage') return <PercentInput {...props} />
  if (scale === 'OneToTen') return <SegmentInput {...props} />
  return <StarInput {...props} />
}

function useKeys(range: ScaleRange, value: number | null, onChange: (v: number | null) => void, step = 1) {
  return (e: KeyboardEvent) => {
    const cur = value ?? range.min - step
    if (e.key === 'ArrowRight' || e.key === 'ArrowUp') {
      e.preventDefault()
      onChange(Math.min(range.max, cur + step))
    } else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') {
      e.preventDefault()
      onChange(Math.max(range.min, cur - step))
    } else if (e.key === 'Home') {
      e.preventDefault()
      onChange(range.min)
    } else if (e.key === 'End') {
      e.preventDefault()
      onChange(range.max)
    } else if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault()
      onChange(null)
    }
  }
}

function Caption({ scale, range, value, showNormalized }: Pick<ScaleInputProps, 'scale' | 'range' | 'value' | 'showNormalized'>) {
  return (
    <div className="flex min-h-5 items-center gap-2 text-[12px]" aria-live="polite">
      <AnimatePresence mode="wait" initial={false}>
        {value === null ? (
          <motion.span key="empty" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} className="text-muted-foreground">
            {tx('Henüz puanlanmadı')}
          </motion.span>
        ) : (
          <motion.span
            key={value}
            initial={{ opacity: 0, y: 4 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -4 }}
            transition={{ duration: 0.18 }}
            className="font-medium text-foreground"
          >
            {describe(scale, range, value)}
          </motion.span>
        )}
      </AnimatePresence>
      {showNormalized && value !== null && (
        <span className="tabular text-muted-foreground">≈ {formatScore(normalizeToHundred(range, value))} / 100</span>
      )}
    </div>
  )
}

/* ------------------------------------ Yıldız ------------------------------------ */

function StarInput({ scale, range, value, onChange, disabled, invalid, labelledBy, showNormalized, size = 'md' }: ScaleInputProps) {
  const [hover, setHover] = useState<number | null>(null)
  const steps = Array.from({ length: range.max - range.min + 1 }, (_, i) => range.min + i)
  const shown = hover ?? value
  const onKey = useKeys(range, value, onChange)
  const px = size === 'sm' ? 'size-6' : 'size-8'

  return (
    <div className="flex flex-col gap-1">
      <div
        role="radiogroup"
        aria-labelledby={labelledBy}
        aria-invalid={invalid || undefined}
        tabIndex={disabled ? -1 : 0}
        onKeyDown={disabled ? undefined : onKey}
        onMouseLeave={() => setHover(null)}
        className={cn(
          'inline-flex w-fit items-center gap-1 rounded-lg p-1 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
          invalid && 'ring-2 ring-destructive/40',
        )}
      >
        {steps.map((s) => {
          const filled = shown !== null && s <= shown
          return (
            <motion.button
              key={s}
              type="button"
              role="radio"
              aria-checked={value === s}
              aria-label={`${s} — ${describe(scale, range, s)}`}
              tabIndex={-1}
              disabled={disabled}
              onMouseEnter={() => !disabled && setHover(s)}
              onClick={() => onChange(value === s ? null : s)}
              whileTap={disabled ? undefined : { scale: 0.85 }}
              className="rounded-md p-0.5 disabled:cursor-not-allowed"
            >
              <motion.span
                animate={{ scale: filled && value === s ? [1, 1.25, 1] : 1 }}
                transition={{ duration: 0.3 }}
                className="block"
              >
                <Star
                  aria-hidden
                  strokeWidth={1.5}
                  className={cn(
                    px,
                    'transition-colors duration-150',
                    filled ? 'fill-[hsl(var(--warning))] text-[hsl(var(--warning))]' : 'text-muted-foreground/40',
                  )}
                />
              </motion.span>
            </motion.button>
          )
        })}
        <span className="tabular ml-2 w-10 text-[13px] font-semibold text-muted-foreground">
          {value !== null ? `${value}/${range.max}` : `–/${range.max}`}
        </span>
      </div>
      <Caption scale={scale} range={range} value={hover ?? value} showNormalized={showNormalized} />
    </div>
  )
}

/* ----------------------------------- Segment ------------------------------------ */

function SegmentInput({ scale, range, value, onChange, disabled, invalid, labelledBy, showNormalized, size = 'md' }: ScaleInputProps) {
  const [hover, setHover] = useState<number | null>(null)
  const steps = Array.from({ length: range.max - range.min + 1 }, (_, i) => range.min + i)
  const shown = hover ?? value
  const onKey = useKeys(range, value, onChange)

  return (
    <div className="flex flex-col gap-1">
      <div
        role="radiogroup"
        aria-labelledby={labelledBy}
        aria-invalid={invalid || undefined}
        tabIndex={disabled ? -1 : 0}
        onKeyDown={disabled ? undefined : onKey}
        onMouseLeave={() => setHover(null)}
        className={cn(
          'grid w-full max-w-md gap-1 rounded-lg p-1 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
          invalid && 'ring-2 ring-destructive/40',
        )}
        style={{ gridTemplateColumns: `repeat(${steps.length}, minmax(0, 1fr))` }}
      >
        {steps.map((s) => {
          const filled = shown !== null && s <= shown
          const t = (s - range.min) / (range.max - range.min || 1)
          return (
            <motion.button
              key={s}
              type="button"
              role="radio"
              aria-checked={value === s}
              aria-label={`${s} — ${describe(scale, range, s)}`}
              tabIndex={-1}
              disabled={disabled}
              onMouseEnter={() => !disabled && setHover(s)}
              onClick={() => onChange(value === s ? null : s)}
              whileTap={disabled ? undefined : { scale: 0.9 }}
              className={cn(
                'tabular relative flex items-center justify-center overflow-hidden rounded-md border text-[12px] font-semibold transition-colors disabled:cursor-not-allowed',
                size === 'sm' ? 'h-7' : 'h-9',
                filled ? 'border-primary/40 text-primary-foreground' : 'border-border bg-background text-muted-foreground hover:border-primary/40',
              )}
            >
              <motion.span
                aria-hidden
                initial={false}
                animate={{ opacity: filled ? 0.45 + t * 0.55 : 0 }}
                transition={{ duration: 0.18 }}
                className="absolute inset-0 bg-primary"
              />
              <span className="relative">{s}</span>
            </motion.button>
          )
        })}
      </div>
      <Caption scale={scale} range={range} value={hover ?? value} showNormalized={showNormalized} />
    </div>
  )
}

/* ------------------------------------ Yüzde ------------------------------------- */

function PercentInput({ scale, range, value, onChange, disabled, invalid, labelledBy, showNormalized }: ScaleInputProps) {
  return (
    <div className="flex flex-col gap-1">
      <div className={cn('flex w-full max-w-md items-center gap-3 rounded-lg p-1', invalid && 'ring-2 ring-destructive/40')}>
        <Slider
          value={[value ?? range.min]}
          onChange={([v]) => onChange(v)}
          min={range.min}
          max={range.max}
          ariaLabel={tx('Yüzde değeri')}
          disabled={disabled}
          rangeClassName={value === null ? 'bg-muted-foreground/30' : undefined}
        />
        <label className="relative shrink-0">
          <span className="sr-only">{tx('Yüzde değeri')}</span>
          <input
            type="number"
            inputMode="numeric"
            min={range.min}
            max={range.max}
            disabled={disabled}
            aria-labelledby={labelledBy}
            aria-invalid={invalid || undefined}
            value={value ?? ''}
            placeholder="–"
            onChange={(e) => {
              if (e.target.value === '') return onChange(null)
              const n = Math.round(Number(e.target.value))
              if (Number.isFinite(n)) onChange(Math.min(range.max, Math.max(range.min, n)))
            }}
            className="tabular h-9 w-20 rounded-md border border-input bg-background pr-7 pl-2.5 text-right text-[13px] font-semibold outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-50"
          />
          <Percent aria-hidden className="pointer-events-none absolute top-1/2 right-2 size-3.5 -translate-y-1/2 text-muted-foreground" />
        </label>
      </div>
      <Caption scale={scale} range={range} value={value} showNormalized={showNormalized} />
    </div>
  )
}

/* ------------------------------------ Rozet ------------------------------------- */

/** Listelerde ölçeğin küçük görsel özeti. */
export function ScaleBadge({ scale, range }: { scale: MetricScale; range?: ScaleRange }) {
  const r = range ?? (scale === 'Percentage' ? { min: 0, max: 100 } : scale === 'OneToTen' ? { min: 1, max: 10 } : { min: 1, max: 5 })
  const label = `${r.min}–${r.max}`
  return (
    <span className="inline-flex items-center gap-1.5 rounded-md border border-border bg-background px-1.5 py-0.5 text-[11px] font-medium whitespace-nowrap text-muted-foreground" title={tx('Ölçek {0}', [label])}>
      {scale === 'OneToFive' && (
        <span aria-hidden className="flex gap-px">
          {Array.from({ length: 5 }).map((_, i) => (
            <Star key={i} className="size-2.5 fill-[hsl(var(--warning))]/70 text-[hsl(var(--warning))]/70" strokeWidth={0} />
          ))}
        </span>
      )}
      {scale === 'OneToTen' && (
        <span aria-hidden className="flex items-end gap-px">
          {Array.from({ length: 10 }).map((_, i) => (
            <span key={i} className="w-[2px] rounded-full bg-primary/60" style={{ height: 4 + i * 0.6 }} />
          ))}
        </span>
      )}
      {scale === 'Percentage' && <Percent aria-hidden className="size-3 text-primary/70" />}
      <span className="tabular">{scale === 'Percentage' ? tx('Yüzde') : label}</span>
    </span>
  )
}
