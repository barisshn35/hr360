/**
 * macOS tarzı büyüyen ikon rıhtımı.
 * Kaynak: 21st.dev "Floating Dock" (aceternity, id 18014). Uyarlama:
 * react-router bağlantıları, etkin sayfa noktası, okunmamış rozeti, ayraç,
 * düğme öğeleri (komut paleti gibi) ve zümrüt cam yüzey.
 */
import { useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  AnimatePresence,
  type MotionValue,
  motion,
  useMotionValue,
  useReducedMotion,
  useSpring,
  useTransform,
} from 'motion/react'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

export type DockItem =
  | {
      kind?: 'link'
      id: string
      title: string
      icon: React.ElementType
      href: string
      active?: boolean
      badge?: number
    }
  | { kind: 'action'; id: string; title: string; icon: React.ElementType; onClick: () => void; active?: boolean; badge?: number }
  | { kind: 'separator'; id: string }

export function FloatingDock({ items, className }: { items: DockItem[]; className?: string }) {
  const mouseX = useMotionValue(Infinity)
  return (
    <motion.nav
      aria-label={tx('Hızlı erişim')}
      onMouseMove={(e) => mouseX.set(e.pageX)}
      onMouseLeave={() => mouseX.set(Infinity)}
      className={cn(
        'flex h-[66px] items-end gap-2.5 rounded-[22px] border border-border/70 px-3 pb-2.5',
        'bg-background/60 shadow-[0_20px_60px_-20px_rgb(0_0_0/0.8),inset_0_1px_0_0_hsl(var(--edge-light))] backdrop-blur-xl',
        className,
      )}
    >
      {items.map((item) =>
        item.kind === 'separator' ? (
          <span key={item.id} aria-hidden="true" className="mb-2 h-8 w-px self-end bg-border" />
        ) : (
          <IconContainer key={item.id} mouseX={mouseX} item={item} />
        ),
      )}
    </motion.nav>
  )
}

function IconContainer({ mouseX, item }: { mouseX: MotionValue<number>; item: Exclude<DockItem, { kind: 'separator' }> }) {
  const ref = useRef<HTMLDivElement>(null)
  const reduced = useReducedMotion()
  const distance = useTransform(mouseX, (val) => {
    const b = ref.current?.getBoundingClientRect() ?? { x: 0, width: 0 }
    return val - b.x - b.width / 2
  })
  const range = reduced ? [40, 40, 40] : [40, 68, 40]
  const iconRange = reduced ? [18, 18, 18] : [18, 30, 18]
  const spring = { mass: 0.1, stiffness: 150, damping: 12 }
  const size = useSpring(useTransform(distance, [-140, 0, 140], range), spring)
  const iconSize = useSpring(useTransform(distance, [-140, 0, 140], iconRange), spring)
  const [hovered, setHovered] = useState(false)
  const Icon = item.icon

  const body = (
    <motion.div
      ref={ref}
      style={{ width: size, height: size }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      className={cn(
        'relative flex aspect-square items-center justify-center rounded-2xl border transition-colors',
        item.active
          ? 'border-primary/35 bg-accent text-primary'
          : 'border-border/60 bg-card/80 text-muted-foreground hover:text-foreground',
      )}
    >
      <AnimatePresence>
        {hovered && (
          <motion.span
            initial={{ opacity: 0, y: 10, x: '-50%' }}
            animate={{ opacity: 1, y: 0, x: '-50%' }}
            exit={{ opacity: 0, y: 2, x: '-50%' }}
            className="absolute -top-9 left-1/2 w-fit rounded-lg border border-border bg-popover px-2 py-1 text-[11.5px] font-medium whitespace-pre text-foreground shadow-popover"
          >
            {item.title}
          </motion.span>
        )}
      </AnimatePresence>
      <motion.span style={{ width: iconSize, height: iconSize }} className="flex items-center justify-center">
        <Icon className="size-full" strokeWidth={1.6} />
      </motion.span>
      {item.badge ? (
        <span className="tabular absolute -top-1.5 -right-1.5 flex h-[18px] min-w-[18px] items-center justify-center rounded-full bg-primary px-1 text-[10px] font-bold text-primary-foreground shadow-[0_0_12px_hsl(var(--primary)/0.8)]">
          {item.badge > 99 ? '99+' : item.badge}
        </span>
      ) : null}
      {item.active && (
        <motion.span layoutId="dock-dot" className="absolute -bottom-2 size-1 rounded-full bg-primary shadow-[0_0_8px_hsl(var(--primary))]" />
      )}
    </motion.div>
  )

  if (item.kind === 'action')
    return (
      <button type="button" aria-label={item.title} onClick={item.onClick} className="cursor-pointer rounded-2xl outline-none focus-visible:ring-2 focus-visible:ring-primary/50">
        {body}
      </button>
    )
  return (
    <Link to={item.href} aria-label={item.title} aria-current={item.active ? 'page' : undefined} className="rounded-2xl outline-none focus-visible:ring-2 focus-visible:ring-primary/50">
      {body}
    </Link>
  )
}
