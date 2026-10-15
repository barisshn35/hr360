import { motion, useReducedMotion } from 'motion/react'
import type { StatusTone } from '@/components/ui/StatusBadge'
import { cn } from '@/lib/utils'

/** Rozetlerle aynı ton sözlüğü — ilerleme ve durum aynı dili konuşsun. */
const FILL: Record<StatusTone, string> = {
  info: 'bg-primary',
  success: 'bg-[hsl(var(--success))]',
  warning: 'bg-[hsl(var(--warning))]',
  danger: 'bg-destructive',
  neutral: 'bg-muted-foreground',
}

const STROKE: Record<StatusTone, string> = {
  info: 'hsl(var(--primary))',
  success: 'hsl(var(--success))',
  warning: 'hsl(var(--warning))',
  danger: 'hsl(var(--destructive))',
  neutral: 'hsl(var(--muted-foreground))',
}

/**
 * İlerleme çubuğu. Değer değiştiğinde genişlik animasyonla gider —
 * hareket burada bilgi taşır: neyin ne kadar arttığını gösterir.
 */
export function ProgressBar({
  value,
  max = 100,
  tone = 'info',
  label,
  className,
  thick,
}: {
  value: number
  max?: number
  tone?: StatusTone
  /** Ekran okuyucu için; görsel etiket ayrıca yazılır. */
  label: string
  className?: string
  thick?: boolean
}) {
  const reduced = useReducedMotion()
  const percent = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0

  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuenow={Math.round(percent)}
      aria-valuemin={0}
      aria-valuemax={100}
      className={cn(
        'w-full overflow-hidden rounded-full bg-muted',
        thick ? 'h-2' : 'h-1.5',
        className,
      )}
    >
      <motion.div
        className={cn('h-full rounded-full', FILL[tone])}
        initial={reduced ? false : { width: 0 }}
        animate={{ width: `${percent}%` }}
        transition={{ duration: 0.55, ease: 'easeOut' }}
      />
    </div>
  )
}

/** Halka gösterge — bakiye gibi "kalan" değerlerde çubuktan daha okunur. */
export function ProgressRing({
  value,
  max,
  tone = 'info',
  label,
  size = 72,
  children,
}: {
  value: number
  max: number
  tone?: StatusTone
  label: string
  size?: number
  children?: React.ReactNode
}) {
  const reduced = useReducedMotion()
  const percent = max > 0 ? Math.min(1, Math.max(0, value / max)) : 0
  const stroke = 6
  const radius = (size - stroke) / 2
  const circumference = 2 * Math.PI * radius

  return (
    <div
      className="relative shrink-0"
      style={{ width: size, height: size }}
      role="img"
      aria-label={`${label}: %${Math.round(percent * 100)}`}
    >
      <svg width={size} height={size} className="-rotate-90">
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke="hsl(var(--muted))"
          strokeWidth={stroke}
        />
        <motion.circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={STROKE[tone]}
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={circumference}
          initial={reduced ? false : { strokeDashoffset: circumference }}
          animate={{ strokeDashoffset: circumference * (1 - percent) }}
          transition={{ duration: 0.7, ease: 'easeOut' }}
        />
      </svg>
      {children && (
        <div className="absolute inset-0 flex flex-col items-center justify-center">{children}</div>
      )}
    </div>
  )
}
