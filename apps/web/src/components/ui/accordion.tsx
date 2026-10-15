/**
 * Erişilebilir, yay animasyonlu akordeon — 21st.dev "Accordion" (ddoemonn, id 23530) uyarlaması:
 * tek/çoklu açık panel, ok tuşlarıyla başlıklar arası gezinme (↑ ↓ Home End), içerik yüksekliğinin
 * otomatik ölçülmesi, kapalı panelde `inert`, `prefers-reduced-motion` desteği. Renkler proje
 * tokenlarından; yükseklik sınırı isteğe bağlı (SSS cevapları tam gösterilir).
 */
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { motion, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

const EASE = [0.23, 1, 0.32, 1] as const
const EXIT_EASE = [0.4, 0, 1, 1] as const
const DISCLOSE = { type: 'spring', stiffness: 480, damping: 40, mass: 0.6 } as const
const CHEVRON = { type: 'spring', stiffness: 700, damping: 46, mass: 0.5 } as const
const NONE: readonly string[] = []

const useIsomorphicLayoutEffect = typeof window === 'undefined' ? useEffect : useLayoutEffect

function useAutoHeight() {
  const ref = useRef<HTMLDivElement>(null)
  const [height, setHeight] = useState(0)
  const [ready, setReady] = useState(false)
  useIsomorphicLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const read = () => {
      const next = el.getBoundingClientRect().height
      setHeight((prev) => (Math.abs(prev - next) < 0.5 ? prev : next))
    }
    read()
    setReady(true)
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(read)
    observer.observe(el)
    return () => observer.disconnect()
  }, [])
  return { ref, height, ready }
}

export type AccordionItem = { id: string; title: ReactNode; content: ReactNode; meta?: ReactNode }

export function Accordion({
  items,
  type = 'single',
  defaultOpen = NONE,
  collapsible = true,
  maxPanelHeight,
  headingLevel = 3,
  className,
}: {
  items: readonly AccordionItem[]
  type?: 'single' | 'multiple'
  defaultOpen?: readonly string[]
  collapsible?: boolean
  maxPanelHeight?: number
  headingLevel?: number
  className?: string
}) {
  const reduced = Boolean(useReducedMotion())
  const base = useId()
  const [open, setOpen] = useState<string[]>(() => (type === 'single' ? defaultOpen.slice(0, 1) : defaultOpen.slice()))
  const headers = useRef(new Map<string, HTMLButtonElement>())
  const order = useMemo(() => items.map((i) => i.id), [items])

  const toggle = useCallback(
    (id: string) => {
      setOpen((cur) => {
        const active = cur.includes(id)
        if (active && !collapsible && type === 'single') return cur
        if (type === 'single') return active ? [] : [id]
        return active ? cur.filter((x) => x !== id) : [...cur, id]
      })
    },
    [collapsible, type],
  )

  const move = (id: string, delta: number, edge: 'first' | 'last' | null) => {
    if (order.length === 0) return
    const at = order.indexOf(id)
    if (at < 0) return
    const next = edge === 'first' ? 0 : edge === 'last' ? order.length - 1 : (at + delta + order.length) % order.length
    headers.current.get(order[next])?.focus()
  }

  const onKeyDown = (id: string) => (e: KeyboardEvent) => {
    const map: Record<string, [number, 'first' | 'last' | null]> = { ArrowDown: [1, null], ArrowUp: [-1, null], Home: [0, 'first'], End: [0, 'last'] }
    const hit = map[e.key]
    if (!hit) return
    e.preventDefault()
    move(id, hit[0], hit[1])
  }

  return (
    <div className={cn('divide-y divide-border overflow-hidden rounded-2xl border border-border bg-card/70 shadow-sm backdrop-blur', className)}>
      {items.map((item) => (
        <AccordionRow
          key={item.id}
          item={item}
          open={open.includes(item.id)}
          reduced={reduced}
          maxPanelHeight={maxPanelHeight}
          headingLevel={headingLevel}
          headerId={`${base}-h-${item.id}`}
          panelId={`${base}-p-${item.id}`}
          bind={(node) => {
            if (node) headers.current.set(item.id, node)
            else headers.current.delete(item.id)
          }}
          onToggle={() => toggle(item.id)}
          onKeyDown={onKeyDown(item.id)}
        />
      ))}
    </div>
  )
}

function AccordionRow({
  item,
  open,
  reduced,
  maxPanelHeight,
  headingLevel,
  headerId,
  panelId,
  bind,
  onToggle,
  onKeyDown,
}: {
  item: AccordionItem
  open: boolean
  reduced: boolean
  maxPanelHeight?: number
  headingLevel: number
  headerId: string
  panelId: string
  bind: (node: HTMLButtonElement | null) => void
  onToggle: () => void
  onKeyDown: (e: KeyboardEvent) => void
}) {
  const { ref, height, ready } = useAutoHeight()

  useEffect(() => {
    const el = ref.current
    if (!el) return
    el.inert = !open
    return () => {
      el.inert = false
    }
  }, [ref, open])

  return (
    <div data-state={open ? 'open' : 'closed'}>
      <div role="heading" aria-level={headingLevel}>
        <button
          id={headerId}
          ref={bind}
          type="button"
          onClick={onToggle}
          onKeyDown={onKeyDown}
          aria-expanded={open}
          aria-controls={panelId}
          className={cn(
            'flex w-full cursor-pointer items-center gap-3 px-5 py-4 text-left outline-none transition-colors duration-150',
            'hover:bg-muted/60 focus-visible:bg-primary/[0.06] focus-visible:shadow-[inset_0_0_0_1.5px_hsl(var(--primary))]',
          )}
        >
          <span className={cn('min-w-0 flex-1 text-[15px] font-medium transition-colors duration-150', open ? 'text-foreground' : 'text-foreground/85')}>
            {item.title}
          </span>
          {item.meta ? <span className="shrink-0 text-[12px] tabular-nums text-muted-foreground">{item.meta}</span> : null}
          <motion.svg
            width="14"
            height="14"
            viewBox="0 0 256 256"
            fill="none"
            aria-hidden="true"
            className={cn('shrink-0', open ? 'text-primary' : 'text-muted-foreground')}
            initial={false}
            animate={{ rotate: open ? 180 : 0 }}
            transition={reduced ? { duration: 0 } : CHEVRON}
          >
            <path d="M208 96l-80 80-80-80" stroke="currentColor" strokeWidth="18" strokeLinecap="round" strokeLinejoin="round" />
          </motion.svg>
        </button>
      </div>
      <motion.div
        initial={false}
        animate={ready ? { height: open ? height : 0 } : {}}
        transition={reduced ? { duration: 0 } : DISCLOSE}
        style={{ overflow: 'hidden', height: ready ? undefined : open ? 'auto' : 0 }}
      >
        <div
          id={panelId}
          ref={ref}
          role="region"
          aria-labelledby={headerId}
          aria-hidden={open ? undefined : true}
          className="border-t border-border/70 bg-muted/30"
          style={maxPanelHeight ? { maxHeight: maxPanelHeight, overflowY: 'auto', overscrollBehavior: 'contain' } : undefined}
        >
          <motion.div
            initial={false}
            animate={{ opacity: open ? 1 : 0, y: open ? 0 : -4 }}
            transition={reduced ? { duration: 0 } : open ? { duration: 0.2, ease: EASE } : { duration: 0.14, ease: EXIT_EASE }}
            className="px-5 pt-3.5 pb-4"
          >
            {item.content}
          </motion.div>
        </div>
      </motion.div>
    </div>
  )
}
