/**
 * Hata ve durum ekranları ailesi: 404, 403, arayüz hatası, çevrimdışı, oturum sona erdi.
 *
 * Hepsi aynı iskeleti paylaşır (sahne + kod + başlık + açıklama + eylemler); her
 * durumun kendi animasyonu `art` ile verilir (bkz. ./art). Girişte öğeler sırayla
 * belirir; hareket azaltma tercihinde animasyonlar durur, içerik hemen görünür.
 */
import type { ReactNode } from 'react'
import { motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { EASE } from '@/motion/primitives'
import { cn } from '@/lib/utils'

export function ErrorScene({
  art,
  code,
  title,
  detail,
  actions,
  children,
  fullPage = false,
  tone = 'primary',
  testId,
}: {
  art: ReactNode
  /** Büyük, soluk durum kodu (ör. 404). */
  code?: string
  title: string
  detail?: ReactNode
  actions?: ReactNode
  /** Eylemlerin altındaki ek içerik (ör. katlanır ayrıntılar). */
  children?: ReactNode
  /** Uygulama kabuğu dışında: tam ekran, kendi zemini. */
  fullPage?: boolean
  tone?: 'primary' | 'destructive' | 'warning'
  testId?: string
}) {
  const reduced = useReducedMotion()
  const up = (delay: number) =>
    reduced
      ? {}
      : { initial: { opacity: 0, y: 14 }, animate: { opacity: 1, y: 0 }, transition: { duration: 0.6, delay, ease: EASE } }

  const glow = {
    primary: 'bg-primary/15',
    destructive: 'bg-destructive/15',
    warning: 'bg-[hsl(38_92%_50%)]/15',
  }[tone]

  const body = (
    <div role="alert" data-testid={testId} className="relative mx-auto flex w-full max-w-lg flex-col items-center px-5 text-center">
      <motion.div {...up(0)} className="relative flex size-52 items-center justify-center">
        <span aria-hidden="true" className={cn('absolute size-28 rounded-full blur-3xl', glow)} />
        {art}
      </motion.div>
      {code && (
        <motion.p
          {...up(0.08)}
          aria-hidden="true"
          className="text-gradient -mt-2 text-[56px] leading-none font-semibold tracking-[-0.06em] opacity-80 select-none sm:text-[72px]"
        >
          {code}
        </motion.p>
      )}
      <motion.h1 {...up(0.14)} className="mt-3 text-[24px] leading-tight font-semibold tracking-[-0.02em]">
        {title}
      </motion.h1>
      {detail && (
        <motion.div {...up(0.2)} className="mt-2.5 max-w-md text-[14px] leading-relaxed text-muted-foreground">
          {detail}
        </motion.div>
      )}
      {actions && (
        <motion.div {...up(0.28)} className="mt-7 flex flex-wrap items-center justify-center gap-2.5">
          {actions}
        </motion.div>
      )}
      {children && (
        <motion.div {...up(0.34)} className="mt-6 w-full">
          {children}
        </motion.div>
      )}
    </div>
  )

  if (!fullPage) return <div className="py-12 sm:py-16">{body}</div>
  return (
    <main className="relative flex min-h-dvh items-center justify-center overflow-hidden bg-background py-12 text-foreground">
      <AmbientBackground meteors={false} />
      <div className="relative z-10 w-full">{body}</div>
    </main>
  )
}
