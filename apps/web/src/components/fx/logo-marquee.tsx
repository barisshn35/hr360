/**
 * Sonsuz logo şeridi.
 *
 * 21st.dev kaynakları:
 *  - "Logo Marquee" (ddoemonn, id 23537): fizik tabanlı akış; fare üstündeyken ya da
 *    klavye odağında yumuşakça durur, odaklanan öğeyi görünür alana çeker,
 *    hareket azaltma tercihinde kaydırılabilir düz liste olur.
 *  - "Blurred Marquee" (grootstudio, id 20147): kenarlarda kademeli bulanıklık.
 *
 * Uyarlama: renkler proje token'larına bağlandı, kart çerçevesi kaldırıldı, öğe
 * görünümü dışarıdan (`renderItem`) verilir; SPA olduğu için useLayoutEffect.
 */
import { memo, useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

const RAMP = 0.19
const SETTLE = 0.16
const MAX_COPIES = 14

export type MarqueeDirection = 'left' | 'right'

function fold(x: number, loop: number) {
  const m = x % loop
  return m > 0 ? m - loop : m
}

function clamp(x: number, min: number, max: number) {
  return x < min ? min : x > max ? max : x
}

function useLogoMarquee({
  speed = 44,
  direction = 'left' as MarqueeDirection,
  gap = 40,
  paused = false,
}) {
  const viewportRef = useRef<HTMLDivElement>(null)
  const trackRef = useRef<HTMLDivElement>(null)
  const groupRef = useRef<HTMLUListElement>(null)

  const [copies, setCopies] = useState(4)
  const [held, setHeld] = useState(false)
  const [near, setNear] = useState(false)

  const reduced = useReducedMotion() === true
  const stopped = held || paused

  const reducedRef = useRef(reduced)
  const movingRef = useRef(false)
  useEffect(() => {
    reducedRef.current = reduced
    movingRef.current = !stopped && !reduced
  })

  const offset = useRef(0)
  const nudge = useRef(0)
  const rate = useRef(0)
  const span = useRef(0)

  const paint = useCallback(() => {
    const track = trackRef.current
    if (!track) return
    const x = reducedRef.current ? 0 : offset.current - span.current
    track.style.transform = `translate3d(${x.toFixed(2)}px, 0, 0)`
  }, [])

  useLayoutEffect(() => {
    const viewport = viewportRef.current
    const group = groupRef.current
    if (!viewport || !group) return

    const measure = () => {
      const width = group.getBoundingClientRect().width
      const loop = width > 0 ? width + gap : 0
      const room = viewport.getBoundingClientRect().width
      span.current = loop
      offset.current = loop > 0 ? clamp(offset.current, -loop, loop) : 0
      paint()
      const next = reduced || loop <= 0 ? 4 : clamp(Math.ceil(room / loop) + 3, 4, MAX_COPIES)
      setCopies((prev) => (prev === next ? prev : next))
    }

    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(viewport)
    observer.observe(group)
    return () => observer.disconnect()
  }, [gap, paint, reduced])

  useEffect(() => {
    const viewport = viewportRef.current
    if (!viewport) return
    if (typeof IntersectionObserver === 'undefined') {
      setNear(true)
      return
    }
    const observer = new IntersectionObserver(
      (entries) => {
        const entry = entries[entries.length - 1]
        if (entry) setNear(entry.isIntersecting)
      },
      { rootMargin: '96px' },
    )
    observer.observe(viewport)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    if (reduced || !near) return
    let frame = 0
    let last = 0
    const sign = direction === 'right' ? 1 : -1

    const tick = (now: number) => {
      frame = requestAnimationFrame(tick)
      const dt = last ? Math.min((now - last) / 1000, 0.05) : 0
      last = now
      const loop = span.current
      if (loop <= 0) return

      rate.current += ((movingRef.current ? 1 : 0) - rate.current) * (1 - Math.exp(-dt / RAMP))
      const pull = nudge.current * (1 - Math.exp(-dt / SETTLE))
      nudge.current -= pull

      let x = offset.current + sign * speed * rate.current * dt + pull
      if (rate.current > 0.002 && Math.abs(nudge.current) < 0.25) {
        nudge.current = 0
        x = fold(x, loop)
      } else {
        x = clamp(x, -loop, loop)
      }
      offset.current = x
      paint()
    }

    frame = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(frame)
  }, [reduced, near, speed, direction, paint])

  useEffect(() => {
    const viewport = viewportRef.current
    if (!viewport) return
    const pin = () => {
      if (reducedRef.current) return
      if (viewport.scrollLeft !== 0) viewport.scrollLeft = 0
      if (viewport.scrollTop !== 0) viewport.scrollTop = 0
    }
    viewport.addEventListener('scroll', pin, { passive: true })
    return () => viewport.removeEventListener('scroll', pin)
  }, [])

  useEffect(() => {
    const release = () => setHeld(false)
    window.addEventListener('blur', release)
    return () => window.removeEventListener('blur', release)
  }, [])

  const reveal = useCallback((node: HTMLElement) => {
    const viewport = viewportRef.current
    const loop = span.current
    if (!viewport || reducedRef.current || loop <= 0 || node === viewport) return
    const view = viewport.getBoundingClientRect()
    const box = node.getBoundingClientRect()
    const pad = 12
    let delta = 0
    if (box.left < view.left + pad) delta = view.left + pad - box.left
    else if (box.right > view.right - pad) delta = view.right - pad - box.right
    if (delta === 0) return
    const target = clamp(offset.current + nudge.current + delta, -loop, loop)
    nudge.current = target - offset.current
  }, [])

  const bind = {
    onPointerEnter: (e: React.PointerEvent) => {
      if (e.pointerType !== 'touch') setHeld(true)
    },
    onPointerDown: () => setHeld(true),
    onPointerUp: (e: React.PointerEvent) => {
      if (e.pointerType === 'touch') setHeld(false)
    },
    onPointerCancel: () => setHeld(false),
    onPointerLeave: () => setHeld(false),
    onFocus: (e: React.FocusEvent) => {
      setHeld(true)
      reveal(e.target as HTMLElement)
    },
    onBlur: () => setHeld(false),
  }

  return { viewportRef, trackRef, groupRef, copies, reduced, bind }
}

const GRADIENT_ANGLES = { right: 90, left: 270 } as const

/** Kenarda katman katman artan bulanıklık (Blurred Marquee'den). */
const ProgressiveBlur = memo(function ProgressiveBlur({
  direction,
  layers = 6,
  intensity = 0.6,
  className,
}: {
  direction: keyof typeof GRADIENT_ANGLES
  layers?: number
  intensity?: number
  className?: string
}) {
  const count = Math.max(layers, 2)
  const segment = 1 / (layers + 1)
  const angle = GRADIENT_ANGLES[direction]
  return (
    <div aria-hidden="true" className={cn('pointer-events-none', className)}>
      {Array.from({ length: count }, (_, index) => {
        const stops = [index, index + 1, index + 2, index + 3]
          .map((step, i) => `rgba(255,255,255,${i === 1 || i === 2 ? 1 : 0}) ${step * segment * 100}%`)
          .join(', ')
        const gradient = `linear-gradient(${angle}deg, ${stops})`
        return (
          <div
            key={index}
            className="absolute inset-0"
            style={{ maskImage: gradient, WebkitMaskImage: gradient, backdropFilter: `blur(${index * intensity}px)` }}
          />
        )
      })}
    </div>
  )
})

export interface LogoMarqueeItem {
  id: string
  label: string
}

export function LogoMarquee<T extends LogoMarqueeItem>({
  items,
  label,
  renderItem,
  speed = 36,
  direction = 'left',
  gap = 28,
  className,
}: {
  items: T[]
  /** Bölümün erişilebilir adı. */
  label: string
  renderItem: (item: T) => ReactNode
  speed?: number
  direction?: MarqueeDirection
  gap?: number
  className?: string
}) {
  const { viewportRef, trackRef, groupRef, copies, reduced, bind } = useLogoMarquee({ speed, direction, gap })
  const groups = reduced ? 1 : copies
  const live = reduced ? 0 : 1

  return (
    <section aria-label={label} className={cn('relative isolate w-full max-w-full min-w-0 overflow-hidden', className)} {...bind}>
      <div
        ref={viewportRef}
        tabIndex={0}
        style={{ overflowX: reduced ? 'auto' : 'hidden' }}
        className="overflow-y-hidden py-1.5 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
      >
        <div ref={trackRef} style={{ gap, willChange: 'transform' }} className="flex w-max items-center">
          {Array.from({ length: groups }, (_, copy) => (
            <ul
              key={copy}
              ref={copy === live ? groupRef : undefined}
              aria-hidden={copy === live ? undefined : true}
              style={{ gap }}
              className="flex w-max items-center"
            >
              {items.map((item) => (
                <li key={item.id} className="shrink-0">
                  {renderItem(item)}
                </li>
              ))}
            </ul>
          ))}
        </div>
      </div>
      <ProgressiveBlur direction="left" className="absolute inset-y-0 left-0 w-16 sm:w-24" />
      <ProgressiveBlur direction="right" className="absolute inset-y-0 right-0 w-16 sm:w-24" />
      {/* Bulanıklığın üstünde zemine erime: koyu/açık temada kenar sert kalmasın. */}
      <div aria-hidden="true" className="pointer-events-none absolute inset-y-0 left-0 w-10 bg-gradient-to-r from-background to-transparent" />
      <div aria-hidden="true" className="pointer-events-none absolute inset-y-0 right-0 w-10 bg-gradient-to-l from-background to-transparent" />
    </section>
  )
}
