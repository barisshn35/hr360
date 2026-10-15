/**
 * Oransal ağırlığın görselleştirmesi.
 *
 * Ağırlıklar yüzde değil: "3 ve 1" yazan kullanıcı "%75 ve %25" görmeli.
 * `ShareBar` bir grubun (kategori, hedefler) tüm üyelerini tek şeritte
 * gösterir; `ShareMeter` tek bir öğenin payını satır içinde.
 */

import { motion } from 'motion/react'
import { formatShare, shareOf } from '@/api/performance'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'

/** Aynı rengin sıraya göre açılan tonu — segmentler ayırt edilsin, palet tek renk kalsın. */
export function tint(color: string, idx: number, count: number): string {
  const pct = count <= 1 ? 100 : Math.round(100 - (idx / (count - 1)) * 55)
  return `color-mix(in oklab, ${color} ${pct}%, hsl(var(--background)))`
}

export interface ShareItem {
  id: string
  label: string
  weight: number
  /** Varsayılan: gruba verilen renk, sıraya göre açılan tonlarla. */
  color?: string
  /** Önizlemede yeni eklenecek öğe — kesik kenarlı çizilir. */
  pending?: boolean
}

export function ShareBar({
  items,
  color = 'hsl(var(--primary))',
  highlightId,
  onHover,
  height = 8,
  className,
  showLabels = false,
}: {
  items: ShareItem[]
  color?: string
  highlightId?: string | null
  onHover?: (id: string | null) => void
  height?: number
  className?: string
  showLabels?: boolean
}) {
  const total = items.reduce((a, i) => a + Math.max(0, i.weight), 0)
  const visible = items.filter((i) => i.weight > 0)

  return (
    <div className={cn('w-full', className)}>
      <div
        className="flex w-full gap-[2px] overflow-hidden rounded-full bg-muted"
        style={{ height }}
        role="img"
        aria-label={visible.map((i) => `${i.label} ${formatShare(shareOf(i.weight, total))}`).join(', ')}
        onMouseLeave={() => onHover?.(null)}
      >
        {visible.map((item, idx) => {
          const share = shareOf(item.weight, total)
          const dim = highlightId && highlightId !== item.id
          return (
            <motion.div
              key={item.id}
              layout
              initial={{ width: 0 }}
              animate={{ width: `${share}%`, opacity: dim ? 0.35 : 1 }}
              transition={{ duration: 0.7, ease: EASE, delay: idx * 0.04 }}
              onMouseEnter={() => onHover?.(item.id)}
              className="h-full shrink-0 first:rounded-l-full last:rounded-r-full"
              style={{
                background: item.pending
                  ? `repeating-linear-gradient(45deg, ${item.color ?? color}, ${item.color ?? color} 4px, transparent 4px, transparent 7px)`
                  : (item.color ?? tint(color, idx, visible.length)),
              }}
              title={`${item.label}: ${formatShare(share)}`}
            />
          )
        })}
      </div>
      {showLabels && (
        <div className="mt-2 flex flex-wrap gap-x-3 gap-y-1">
          {visible.map((item, idx) => (
            <span key={item.id} className="inline-flex items-center gap-1.5 text-[11px] text-muted-foreground">
              <span
                className="size-2 rounded-full"
                style={{ background: item.color ?? tint(color, idx, visible.length) }}
              />
              {item.label}
              <span className="tabular font-medium text-foreground">{formatShare(shareOf(item.weight, total))}</span>
            </span>
          ))}
        </div>
      )}
    </div>
  )
}

/** Tek öğenin payı: ince çubuk + yüzde. */
export function ShareMeter({ share, color = 'hsl(var(--primary))', className }: { share: number; color?: string; className?: string }) {
  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <span className="relative h-1.5 w-14 overflow-hidden rounded-full bg-muted">
        <motion.span
          className="absolute inset-y-0 left-0 rounded-full"
          style={{ background: color }}
          initial={{ width: 0 }}
          animate={{ width: `${Math.min(100, share)}%` }}
          transition={{ duration: 0.7, ease: EASE }}
        />
      </span>
      <span className="tabular w-9 text-right text-[12px] font-semibold">{formatShare(share)}</span>
    </span>
  )
}
