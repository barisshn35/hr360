/**
 * Kartın içinde imleci izleyen yumuşak ışık.
 * Kaynak: 21st.dev "Spotlight" (ibelick, id 1652). Uyarlama: renk zümrüt,
 * ebeveyn stillerini değiştirmek yerine kendi tam boy katmanında çalışır.
 */
import { useEffect, useRef, useState } from 'react'
import { motion, useReducedMotion, useSpring, useTransform } from 'motion/react'
import { cn } from '@/lib/utils'

export function Spotlight({ className, size = 260 }: { className?: string; size?: number }) {
  const layer = useRef<HTMLDivElement>(null)
  const [hovered, setHovered] = useState(false)
  const reduced = useReducedMotion()
  const x = useSpring(0, { bounce: 0, stiffness: 300, damping: 40 })
  const y = useSpring(0, { bounce: 0, stiffness: 300, damping: 40 })
  const left = useTransform(x, (v) => `${v - size / 2}px`)
  const top = useTransform(y, (v) => `${v - size / 2}px`)

  useEffect(() => {
    const parent = layer.current?.parentElement
    if (!parent || reduced) return
    const move = (e: PointerEvent) => {
      const r = parent.getBoundingClientRect()
      x.set(e.clientX - r.left)
      y.set(e.clientY - r.top)
    }
    const enter = () => setHovered(true)
    const leave = () => setHovered(false)
    parent.addEventListener('pointermove', move)
    parent.addEventListener('pointerenter', enter)
    parent.addEventListener('pointerleave', leave)
    return () => {
      parent.removeEventListener('pointermove', move)
      parent.removeEventListener('pointerenter', enter)
      parent.removeEventListener('pointerleave', leave)
    }
  }, [x, y, reduced])

  return (
    <div ref={layer} aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden rounded-[inherit]">
      <motion.div
        className={cn(
          'absolute rounded-full bg-[radial-gradient(circle_at_center,hsl(var(--foreground)/0.06),transparent_70%)] blur-2xl transition-opacity duration-300',
          hovered ? 'opacity-100' : 'opacity-0',
          className,
        )}
        style={{ width: size, height: size, left, top }}
      />
    </div>
  )
}
