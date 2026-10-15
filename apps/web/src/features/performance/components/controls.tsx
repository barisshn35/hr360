/**
 * Performans ekranlarının ortak kontrolleri: anahtar, bölümlü seçici, kaydırıcı.
 *
 * Radix ilkelleri (erişilebilirlik, klavye, dokunmatik) + `motion` ile
 * yumuşak geçişler. Renkler yalnızca token'lardan.
 */

import { useId, type ReactNode } from 'react'
import { motion } from 'motion/react'
import { Slider as SliderPrimitive, Switch as SwitchPrimitive } from 'radix-ui'
import { cn } from '@/lib/utils'
import { ApiError } from '@/api/client'
import { tx } from '@/lib/i18n'

/* ------------------------------------ Anahtar ----------------------------------- */

export function Switch({
  checked,
  onChange,
  label,
  hint,
  disabled,
  id: idProp,
  className,
}: {
  checked: boolean
  onChange: (next: boolean) => void
  label: ReactNode
  hint?: ReactNode
  disabled?: boolean
  id?: string
  className?: string
}) {
  const auto = useId()
  const id = idProp ?? auto
  return (
    <div className={cn('flex items-start justify-between gap-4', className)}>
      <label htmlFor={id} className="min-w-0 cursor-pointer select-none">
        <span className="block text-[13px] font-medium">{label}</span>
        {hint && <span className="mt-0.5 block text-[12px] leading-relaxed text-muted-foreground">{hint}</span>}
      </label>
      <SwitchPrimitive.Root
        id={id}
        checked={checked}
        onCheckedChange={onChange}
        disabled={disabled}
        className={cn(
          'relative inline-flex h-5 w-9 shrink-0 cursor-pointer items-center rounded-full border border-transparent transition-colors',
          'focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-50',
          checked ? 'bg-primary' : 'bg-input',
        )}
      >
        <SwitchPrimitive.Thumb asChild>
          <motion.span
            layout
            transition={{ type: 'spring', stiffness: 700, damping: 35 }}
            className={cn(
              'pointer-events-none block size-4 rounded-full bg-background shadow-sm ring-0',
              checked ? 'ml-[18px]' : 'ml-0.5',
            )}
          />
        </SwitchPrimitive.Thumb>
      </SwitchPrimitive.Root>
    </div>
  )
}

/* -------------------------------- Bölümlü seçici -------------------------------- */

export interface SegmentOption<T extends string> {
  value: T
  label: ReactNode
  /** Ekran okuyucu ve ipucu için. */
  title?: string
}

/**
 * Seçili bölümün arkasındaki "hap" bir seçenekten ötekine kayar
 * (`layoutId`). Dönem, kapsam ve ton seçicilerinin hepsi bunu kullanır.
 */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  size = 'md',
  className,
  ariaLabel,
}: {
  value: T
  onChange: (value: T) => void
  options: SegmentOption<T>[]
  size?: 'sm' | 'md'
  className?: string
  ariaLabel: string
}) {
  const group = useId()
  return (
    <div
      role="radiogroup"
      aria-label={ariaLabel}
      className={cn('inline-flex max-w-full items-center gap-0.5 overflow-x-auto rounded-lg bg-muted p-0.5', className)}
    >
      {options.map((o) => {
        const active = o.value === value
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            title={o.title}
            onClick={() => onChange(o.value)}
            className={cn(
              'relative shrink-0 rounded-md font-medium whitespace-nowrap transition-colors outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
              size === 'sm' ? 'px-2.5 py-1 text-[12px]' : 'px-3 py-1.5 text-[13px]',
              active ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {active && (
              <motion.span
                layoutId={`seg-${group}`}
                className="absolute inset-0 rounded-md bg-background shadow-sm"
                transition={{ type: 'spring', stiffness: 520, damping: 38 }}
              />
            )}
            <span className="relative">{o.label}</span>
          </button>
        )
      })}
    </div>
  )
}

/* ----------------------------------- Kaydırıcı ---------------------------------- */

export function Slider({
  value,
  onChange,
  min,
  max,
  step = 1,
  ariaLabel,
  className,
  trackClassName,
  rangeClassName,
  thumbLabels,
  minStepsBetweenThumbs,
  disabled,
}: {
  value: number[]
  onChange: (value: number[]) => void
  min: number
  max: number
  step?: number
  ariaLabel: string
  className?: string
  trackClassName?: string
  rangeClassName?: string
  thumbLabels?: string[]
  minStepsBetweenThumbs?: number
  disabled?: boolean
}) {
  return (
    <SliderPrimitive.Root
      value={value}
      onValueChange={onChange}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      minStepsBetweenThumbs={minStepsBetweenThumbs}
      aria-label={ariaLabel}
      className={cn('relative flex h-6 w-full touch-none items-center select-none data-[disabled]:opacity-50', className)}
    >
      <SliderPrimitive.Track className={cn('relative h-2 w-full grow overflow-hidden rounded-full bg-muted', trackClassName)}>
        <SliderPrimitive.Range className={cn('absolute h-full bg-primary', rangeClassName)} />
      </SliderPrimitive.Track>
      {value.map((_, i) => (
        <SliderPrimitive.Thumb
          key={i}
          aria-label={thumbLabels?.[i] ?? ariaLabel}
          className="block size-5 cursor-grab rounded-full border-2 border-primary bg-background shadow-md transition-[transform,box-shadow] hover:scale-110 focus-visible:ring-[4px] focus-visible:ring-ring/40 focus-visible:outline-none active:cursor-grabbing active:scale-110"
        />
      ))}
    </SliderPrimitive.Root>
  )
}

/* ---------------------------------- Yardımcılar -------------------------------- */

/** Backend'in `message` alanı; yoksa genel metin. Ham HTTP kodu asla gösterilmez. */
export function errorText(error: unknown, fallback = tx('İşlem tamamlanamadı. Lütfen tekrar deneyin.')): string {
  if (error instanceof ApiError) return error.message || fallback
  if (error instanceof Error && error.message && !/fetch|network/i.test(error.message)) return error.message
  if (error instanceof Error) return tx('Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.')
  return fallback
}

/** Küçük, sakin etiket — rozetten hafif. */
export function Chip({ children, className, tone = 'muted' }: { children: ReactNode; className?: string; tone?: 'muted' | 'primary' | 'warning' | 'success' | 'danger' }) {
  const tones = {
    muted: 'bg-muted text-muted-foreground',
    primary: 'bg-primary/10 text-primary',
    warning: 'bg-[hsl(var(--warning))]/12 text-[hsl(var(--warning))]',
    success: 'bg-[hsl(var(--success))]/12 text-[hsl(var(--success))]',
    danger: 'bg-destructive/10 text-destructive',
  }
  return (
    <span className={cn('inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] font-medium whitespace-nowrap', tones[tone], className)}>
      {children}
    </span>
  )
}
