/**
 * Küçük sayı girişi: [−] değer [+]. Virgüllü ondalık kabul eder ("2,5"),
 * sınırları uygular, klavyede ok tuşlarıyla adım atar.
 */

import { useEffect, useState } from 'react'
import { Minus, Plus } from 'lucide-react'
import { cn } from '@/lib/utils'
import { tx, appLocale } from '@/lib/i18n'

const fmt = (v: number, decimals: number) =>
  new Intl.NumberFormat(appLocale, { maximumFractionDigits: decimals, useGrouping: false }).format(v)

export function NumberStepper({
  value,
  onChange,
  min = 0,
  max = 100,
  step = 1,
  decimals = 0,
  suffix,
  ariaLabel,
  invalid,
  className,
}: {
  value: number
  onChange: (value: number) => void
  min?: number
  max?: number
  step?: number
  decimals?: number
  suffix?: string
  ariaLabel: string
  invalid?: boolean
  className?: string
}) {
  const [text, setText] = useState(fmt(value, decimals))
  const [focused, setFocused] = useState(false)

  useEffect(() => {
    if (!focused) setText(fmt(value, decimals))
  }, [value, decimals, focused])

  const clamp = (v: number) => Math.min(max, Math.max(min, Math.round(v / step) * step))
  const bump = (dir: 1 | -1) => onChange(Number(clamp(value + dir * step).toFixed(decimals)))

  const commit = (raw: string) => {
    const n = Number(raw.replace(',', '.'))
    if (raw.trim() !== '' && Number.isFinite(n)) onChange(Number(Math.min(max, Math.max(min, n)).toFixed(decimals)))
    else setText(fmt(value, decimals))
  }

  return (
    <div
      className={cn(
        'inline-flex h-8 items-center rounded-md border bg-background shadow-xs transition-colors focus-within:ring-[3px] focus-within:ring-ring/50',
        invalid ? 'border-destructive/60' : 'border-input',
        className,
      )}
    >
      <button
        type="button"
        tabIndex={-1}
        onClick={() => bump(-1)}
        disabled={value <= min}
        aria-label={tx('{0} azalt', [ariaLabel])}
        className="flex h-full w-7 items-center justify-center text-muted-foreground transition-colors hover:text-foreground disabled:opacity-30"
      >
        <Minus className="size-3.5" />
      </button>
      <span className="relative flex items-center">
        <input
          value={text}
          inputMode="decimal"
          aria-label={ariaLabel}
          aria-invalid={invalid || undefined}
          onFocus={(e) => {
            setFocused(true)
            e.target.select()
          }}
          onChange={(e) => {
            setText(e.target.value)
            const n = Number(e.target.value.replace(',', '.'))
            if (e.target.value.trim() !== '' && Number.isFinite(n) && n >= min && n <= max) onChange(n)
          }}
          onBlur={(e) => {
            setFocused(false)
            commit(e.target.value)
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowUp') {
              e.preventDefault()
              bump(1)
            } else if (e.key === 'ArrowDown') {
              e.preventDefault()
              bump(-1)
            } else if (e.key === 'Enter') {
              commit((e.target as HTMLInputElement).value)
            }
          }}
          className={cn('tabular h-full w-11 bg-transparent text-center text-[13px] font-semibold outline-none', suffix && 'w-12 pr-3')}
        />
        {suffix && <span className="pointer-events-none absolute right-1 text-[11px] text-muted-foreground">{suffix}</span>}
      </span>
      <button
        type="button"
        tabIndex={-1}
        onClick={() => bump(1)}
        disabled={value >= max}
        aria-label={tx('{0} artır', [ariaLabel])}
        className="flex h-full w-7 items-center justify-center text-muted-foreground transition-colors hover:text-foreground disabled:opacity-30"
      >
        <Plus className="size-3.5" />
      </button>
    </div>
  )
}
