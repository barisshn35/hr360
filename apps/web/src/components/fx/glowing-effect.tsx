/**
 * İmlece doğru dönen parlak kenar (Cursor sitesindeki efekt).
 * Kaynak: 21st.dev "Glowing Effect" (aceternity, id 1567).
 * Uyarlama: gökkuşağı yerine zümrüt/camgöbeği geçiş; ekranda çok sayıda kart
 * olduğu için dinleyici tek bir paylaşılan `pointermove` aboneliğinde.
 */
import { memo, useCallback, useEffect, useRef } from 'react'
import { animate, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

type Listener = (p: { x: number; y: number }) => void
const listeners = new Set<Listener>()
let last = { x: -9999, y: -9999 }
let bound = false
function bind() {
  if (bound || typeof window === 'undefined') return
  bound = true
  window.addEventListener(
    'pointermove',
    (e) => {
      last = { x: e.clientX, y: e.clientY }
      listeners.forEach((l) => l(last))
    },
    { passive: true },
  )
  window.addEventListener('scroll', () => listeners.forEach((l) => l(last)), { passive: true })
}

interface GlowingEffectProps {
  blur?: number
  inactiveZone?: number
  proximity?: number
  spread?: number
  className?: string
  disabled?: boolean
  movementDuration?: number
  borderWidth?: number
}

export const GlowingEffect = memo(function GlowingEffect({
  blur = 0,
  inactiveZone = 0.6,
  proximity = 64,
  spread = 28,
  className,
  movementDuration = 1.6,
  borderWidth = 1.5,
  disabled = false,
}: GlowingEffectProps) {
  const ref = useRef<HTMLDivElement>(null)
  const frame = useRef(0)
  const reduced = useReducedMotion()

  const handleMove = useCallback(
    (p: { x: number; y: number }) => {
      const el = ref.current
      if (!el) return
      cancelAnimationFrame(frame.current)
      frame.current = requestAnimationFrame(() => {
        const { left, top, width, height } = el.getBoundingClientRect()
        const cx = left + width / 2
        const cy = top + height / 2
        const inactive = 0.5 * Math.min(width, height) * inactiveZone
        if (Math.hypot(p.x - cx, p.y - cy) < inactive) {
          el.style.setProperty('--active', '0')
          return
        }
        const isActive =
          p.x > left - proximity && p.x < left + width + proximity && p.y > top - proximity && p.y < top + height + proximity
        el.style.setProperty('--active', isActive ? '1' : '0')
        if (!isActive) return
        const current = parseFloat(el.style.getPropertyValue('--start')) || 0
        const target = (180 * Math.atan2(p.y - cy, p.x - cx)) / Math.PI + 90
        const diff = ((target - current + 180) % 360) - 180
        animate(current, current + diff, {
          duration: movementDuration,
          ease: [0.16, 1, 0.3, 1],
          onUpdate: (v) => el.style.setProperty('--start', String(v)),
        })
      })
    },
    [inactiveZone, proximity, movementDuration],
  )

  useEffect(() => {
    if (disabled || reduced) return
    bind()
    listeners.add(handleMove)
    return () => {
      listeners.delete(handleMove)
      cancelAnimationFrame(frame.current)
    }
  }, [handleMove, disabled, reduced])

  if (disabled || reduced) return null

  return (
    <div
      ref={ref}
      aria-hidden="true"
      style={
        {
          '--blur': `${blur}px`,
          '--spread': spread,
          '--start': '0',
          '--active': '0',
          '--ge-border': `${borderWidth}px`,
          // Çoğunlukla nötr beyaz ışık, içinde ince bir zümrüt damar.
          '--gradient': `radial-gradient(circle, hsl(0 0% 100% / 0.7) 10%, transparent 20%),
            radial-gradient(circle at 40% 40%, hsl(var(--primary)) 5%, transparent 15%),
            radial-gradient(circle at 60% 60%, hsl(0 0% 85% / 0.6) 10%, transparent 20%),
            repeating-conic-gradient(from 236.84deg at 50% 50%, hsl(0 0% 100% / 0.55) 0%, hsl(var(--primary) / 0.8) 6%, hsl(0 0% 70% / 0.5) 12%, hsl(0 0% 100% / 0.55) 20%)`,
        } as React.CSSProperties
      }
      className={cn('pointer-events-none absolute inset-0 rounded-[inherit]', blur > 0 && 'blur-[var(--blur)]', className)}
    >
      <div
        className={cn(
          'rounded-[inherit]',
          // Kenarlığın İÇİNE çizilir: overflow-hidden kartlarda da görünür.
          'after:absolute after:inset-0 after:rounded-[inherit] after:content-[""]',
          'after:[border:var(--ge-border)_solid_transparent]',
          'after:[background:var(--gradient)] after:[background-attachment:fixed]',
          'after:opacity-[var(--active)] after:transition-opacity after:duration-300',
          'after:[mask-clip:padding-box,border-box] after:[mask-composite:intersect]',
          'after:[mask-image:linear-gradient(#0000,#0000),conic-gradient(from_calc((var(--start)-var(--spread))*1deg),#00000000_0deg,#fff,#00000000_calc(var(--spread)*2deg))]',
        )}
      />
    </div>
  )
})
