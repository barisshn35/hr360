/**
 * Yukarıdan vuran ışık konisi (Linear sitesindeki lamba).
 * Kaynak: 21st.dev "Lamp" (aceternity, id 900). Uyarlama: tam sayfa kapsayıcı
 * yerine arka plan katmanı; renk zümrüt, zemin token'ı.
 */
import { motion, useReducedMotion } from 'motion/react'
import { cn } from '@/lib/utils'

export function Lamp({ className }: { className?: string }) {
  const reduced = useReducedMotion()
  const grow = (from: string, to: string) =>
    reduced
      ? { initial: false as const, animate: { width: to, opacity: 1 } }
      : {
          initial: { opacity: 0.4, width: from },
          animate: { opacity: 0.85, width: to },
          transition: { delay: 0.2, duration: 1, ease: 'easeInOut' as const },
        }
  return (
    <div
      aria-hidden="true"
      className={cn(
        'pointer-events-none absolute inset-x-0 top-0 h-[34rem] overflow-hidden opacity-40',
        // Kenarları zemine eritir: arka plan düz renk olmadığı için (aurora,
        // ızgara) orijinaldeki zemin renginde örtüler yerine maske kullanılır.
        '[mask-image:radial-gradient(ellipse_55%_75%_at_50%_0%,black_35%,transparent_100%)]',
        className,
      )}
    >
      <div className="relative isolate flex h-full w-full scale-y-125 items-start justify-center pt-20">
        <motion.div
          {...grow('12rem', '32rem')}
          style={{ backgroundImage: 'conic-gradient(from 70deg at center top, hsl(var(--foreground) / 0.55), transparent, transparent)' }}
          className="absolute top-0 right-1/2 h-64 [mask-image:linear-gradient(to_bottom,black_15%,transparent_95%),linear-gradient(to_right,transparent,black_45%)] [mask-composite:intersect]"
        >
        </motion.div>
        <motion.div
          {...grow('12rem', '32rem')}
          style={{ backgroundImage: 'conic-gradient(from 290deg at center top, transparent, transparent, hsl(var(--foreground) / 0.55))' }}
          className="absolute top-0 left-1/2 h-64 [mask-image:linear-gradient(to_bottom,black_15%,transparent_95%),linear-gradient(to_left,transparent,black_45%)] [mask-composite:intersect]"
        >
        </motion.div>
        <div className="absolute top-0 z-40 h-36 w-[28rem] -translate-y-1/2 rounded-full bg-primary/15 blur-3xl" />
        <motion.div
          {...grow('6rem', '14rem')}
          className="absolute top-0 z-30 h-28 -translate-y-1/3 rounded-full bg-foreground/20 blur-2xl"
        />
        <motion.div {...grow('12rem', '32rem')} className="absolute top-0 z-50 h-px bg-[hsl(160_90%_70%)]" />
      </div>
    </div>
  )
}
