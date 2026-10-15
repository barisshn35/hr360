/**
 * Yukarıdan düşüp zemine çarpınca kıvılcım saçan ışık hüzmeleri.
 * Kaynak: 21st.dev "Background Beams With Collision" (aceternity, id 1497).
 * Uyarlama: arka plan katmanı olarak çalışır (içerik sarmalamaz); çarpışma
 * zemini kapsayıcının alt kenarı; renk zümrüt; hüzme konumları yüzde cinsinden.
 */
import { useEffect, useRef, useState } from 'react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

const BEAMS = [
  { x: 6, duration: 7, repeatDelay: 3, delay: 2 },
  { x: 18, duration: 5, repeatDelay: 6, delay: 0.5, className: 'h-6' },
  { x: 31, duration: 9, repeatDelay: 4, delay: 4 },
  { x: 47, duration: 4, repeatDelay: 7, delay: 1, className: 'h-20' },
  { x: 62, duration: 6, repeatDelay: 3, delay: 3, className: 'h-10' },
  { x: 76, duration: 8, repeatDelay: 2, delay: 0 },
  { x: 90, duration: 5, repeatDelay: 5, delay: 2.5, className: 'h-12' },
]

export function Beams({ className }: { className?: string }) {
  const parent = useRef<HTMLDivElement>(null)
  const reduced = useReducedMotion()
  if (reduced) return null
  return (
    <div ref={parent} aria-hidden="true" className={cn('pointer-events-none absolute inset-0 overflow-hidden', className)}>
      {BEAMS.map((b) => (
        <Beam key={b.x} parentRef={parent} {...b} />
      ))}
      <div className="absolute inset-x-0 bottom-0 h-px bg-gradient-to-r from-transparent via-primary/30 to-transparent" />
    </div>
  )
}

function Beam({
  parentRef,
  x,
  duration,
  repeatDelay,
  delay,
  className,
}: {
  parentRef: React.RefObject<HTMLDivElement | null>
  x: number
  duration: number
  repeatDelay: number
  delay: number
  className?: string
}) {
  const ref = useRef<HTMLDivElement>(null)
  const [hit, setHit] = useState<{ x: number; y: number } | null>(null)
  const [key, setKey] = useState(0)
  const done = useRef(false)

  useEffect(() => {
    const id = setInterval(() => {
      const beam = ref.current?.getBoundingClientRect()
      const box = parentRef.current?.getBoundingClientRect()
      if (!beam || !box || done.current) return
      if (beam.bottom >= box.bottom - 1) {
        done.current = true
        setHit({ x: beam.left - box.left + beam.width / 2, y: box.height - 2 })
        setTimeout(() => {
          setHit(null)
          done.current = false
          setKey((k) => k + 1)
        }, 1600)
      }
    }, 60)
    return () => clearInterval(id)
  }, [parentRef, key])

  return (
    <>
      <motion.div
        key={key}
        ref={ref}
        initial={{ y: '-120px' }}
        animate={{ y: '2400px' }}
        transition={{ duration, repeat: Infinity, repeatType: 'loop', ease: 'linear', delay, repeatDelay }}
        style={{ left: `${x}%` }}
        className={cn('absolute top-0 h-14 w-px rounded-full bg-gradient-to-t from-primary via-[hsl(170_85%_60%)] to-transparent', className)}
      />
      <AnimatePresence>{hit && <Explosion key={`${hit.x}-${key}`} style={{ left: hit.x, top: hit.y }} />}</AnimatePresence>
    </>
  )
}

function Explosion({ style }: { style: React.CSSProperties }) {
  const sparks = Array.from({ length: 16 }, (_, i) => ({
    id: i,
    dx: Math.floor(Math.random() * 80 - 40),
    dy: Math.floor(Math.random() * -50 - 10),
    d: Math.random() * 1.2 + 0.5,
  }))
  return (
    <div className="absolute z-10 size-2 -translate-x-1/2 -translate-y-1/2" style={style}>
      <motion.div
        initial={{ opacity: 0 }}
        animate={{ opacity: 1 }}
        exit={{ opacity: 0 }}
        transition={{ duration: 1.2, ease: 'easeOut' }}
        className="absolute -inset-x-10 top-0 m-auto h-2 w-10 rounded-full bg-gradient-to-r from-transparent via-primary to-transparent blur-sm"
      />
      {sparks.map((s) => (
        <motion.span
          key={s.id}
          initial={{ x: 0, y: 0, opacity: 1 }}
          animate={{ x: s.dx, y: s.dy, opacity: 0 }}
          transition={{ duration: s.d, ease: 'easeOut' }}
          className="absolute size-1 rounded-full bg-gradient-to-b from-primary to-[hsl(170_85%_60%)]"
        />
      ))}
    </div>
  )
}
