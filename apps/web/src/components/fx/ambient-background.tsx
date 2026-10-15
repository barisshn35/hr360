/**
 * Panelin canlı zemini: süzülen zümrüt aurora lekeleri, sönümlenen ızgara,
 * ara sıra kayan meteorlar ve imleci izleyen yumuşak ışık.
 *
 * Kaynaklar: 21st.dev "Aurora Background" (aceternity, id 1120) fikri — tam
 * ekran blur filtresi pahalı olduğu için bulanık lekelerin transform
 * animasyonuyla yeniden kuruldu; "Meteors" (id 1407); imleç ışığı "Spotlight
 * Background" (ruixen.ui, id 7645) deseni.
 */
import { useEffect } from 'react'
import { motion, useMotionValue, useReducedMotion, useSpring } from 'motion/react'
import { Meteors } from './meteors'
import { cn } from '@/lib/utils'

export function AmbientBackground({ className, meteors = true }: { className?: string; meteors?: boolean }) {
  const reduced = useReducedMotion()
  const mx = useMotionValue(-1000)
  const my = useMotionValue(-1000)
  const x = useSpring(mx, { stiffness: 120, damping: 30, mass: 0.6 })
  const y = useSpring(my, { stiffness: 120, damping: 30, mass: 0.6 })

  useEffect(() => {
    if (reduced) return
    const move = (e: PointerEvent) => {
      mx.set(e.clientX - 300)
      my.set(e.clientY - 300)
    }
    window.addEventListener('pointermove', move, { passive: true })
    return () => window.removeEventListener('pointermove', move)
  }, [mx, my, reduced])

  return (
    <div aria-hidden="true" className={cn('pointer-events-none fixed inset-0 z-0 overflow-hidden', className)}>
      {/* Aurora lekeleri */}
      <div className="animate-blob absolute -top-[18%] left-[8%] size-[46rem] rounded-full bg-primary/[0.055] blur-[130px]" />
      <div
        className="animate-blob absolute top-[20%] -right-[12%] size-[38rem] rounded-full bg-white/[0.025] blur-[120px]"
        style={{ animationDelay: '-7s', animationDuration: '28s' }}
      />
      <div
        className="animate-blob absolute -bottom-[25%] left-[30%] size-[42rem] rounded-full bg-white/[0.02] blur-[130px]"
        style={{ animationDelay: '-14s', animationDuration: '34s' }}
      />

      {/* Izgara */}
      <div
        className="absolute inset-0 opacity-[0.55]"
        style={{
          backgroundImage:
            'linear-gradient(to right, hsl(var(--foreground) / 0.035) 1px, transparent 1px), linear-gradient(to bottom, hsl(var(--foreground) / 0.035) 1px, transparent 1px)',
          backgroundSize: '64px 64px',
          maskImage: 'radial-gradient(ellipse 80% 60% at 50% 0%, black 30%, transparent 75%)',
          WebkitMaskImage: 'radial-gradient(ellipse 80% 60% at 50% 0%, black 30%, transparent 75%)',
        }}
      />

      {meteors && !reduced && (
        <div className="absolute inset-0 opacity-30">
          <Meteors number={8} />
        </div>
      )}

      {/* İmleç ışığı */}
      {!reduced && (
        <motion.div
          className="absolute size-[600px] rounded-full bg-[radial-gradient(circle,hsl(var(--foreground)/0.035),transparent_60%)]"
          style={{ x, y }}
        />
      )}

      {/* Üst ve alt kenarı zemine eritir */}
      <div className="absolute inset-x-0 bottom-0 h-40 bg-gradient-to-t from-background to-transparent" />
    </div>
  )
}
