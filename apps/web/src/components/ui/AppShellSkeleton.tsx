/**
 * İlk açılış ekranı — oturum doğrulanırken tam sayfa.
 *
 * Önceki sürümdeki kabuk iskeleti (21st.dev "Sidebar Dashboard Skeleton")
 * yeni üst menülü düzende anlamını yitirdi; yerine marka işaretinin etrafında
 * dönen yörüngeler (21st.dev "Orbiting Circles", id 1411) geldi.
 */
import { motion, useReducedMotion } from 'motion/react'
import { OrbitingCircles } from '@/components/fx/orbiting-circles'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { tx } from '@/lib/i18n'

export function AppShellSkeleton({ label = tx('Yükleniyor') }: { label?: string }) {
  const reduced = useReducedMotion()
  return (
    <div aria-busy="true" className="relative flex min-h-dvh flex-col items-center justify-center bg-background">
      <AmbientBackground meteors={false} />
      <div className="relative flex size-56 items-center justify-center">
        <span className="absolute size-24 rounded-full bg-primary/20 blur-2xl" />
        <span className="relative flex size-14 items-center justify-center rounded-2xl bg-gradient-to-br from-primary to-[hsl(170_80%_32%)] shadow-[0_0_40px_-6px_hsl(var(--primary))]">
          <img src="/icon-white.svg" alt="" aria-hidden="true" className="size-8" />
        </span>
        <OrbitingCircles radius={62} duration={8} className="size-2.5 bg-primary shadow-[0_0_12px_hsl(var(--primary))]" />
        <OrbitingCircles radius={62} duration={8} angle={180} path={false} className="size-1.5 bg-primary/60" />
        <OrbitingCircles radius={98} duration={14} reverse className="size-2 bg-[hsl(170_85%_60%)]" />
        <OrbitingCircles radius={98} duration={14} angle={120} reverse path={false} className="size-1.5 bg-[hsl(170_85%_60%)]/60" />
      </div>
      <motion.p
        initial={reduced ? false : { opacity: 0, y: 6 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ delay: 0.2 }}
        className="relative mt-2 text-[13px] tracking-wide text-muted-foreground"
      >
        {label}
        <span className="hr-caret ml-0.5">_</span>
      </motion.p>
    </div>
  )
}
