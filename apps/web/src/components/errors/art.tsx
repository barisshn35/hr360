/**
 * Hata ekranlarının duruma özgü animasyonları (motion/react).
 *
 *  - LostOrbitArt (404): boş yörüngede dolaşan büyüteç; hedef bulunamıyor.
 *  - LockedShieldArt (403): kalkanın üzerinden geçen tarama ışığı, kilit sallanır.
 *  - UnpluggedArt (arayüz hatası): ayrılan fiş uçları ve aradaki kıvılcım.
 *  - OfflineArt (çevrimdışı): sırayla sönen Wi-Fi yayları ve üstüne çizilen çizgi.
 *  - HourglassArt (oturum sona erdi): dönen kum saati ve boşalan süre halkası.
 *
 * Hareket azaltma tercihinde hepsi durağan bir kareye iner.
 */
import { motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { Lock, Search, Shield, Hourglass, Zap } from 'lucide-react'

const tile =
  'relative grid place-items-center rounded-2xl border border-border bg-card shadow-[0_10px_40px_-12px_hsl(var(--foreground)/0.25)]'

/** 404: kesik çizgili yörüngede ilerleyen büyüteç; merkez boş kalır. */
export function LostOrbitArt() {
  const reduced = useReducedMotion()
  return (
    <div aria-hidden="true" className="relative size-48">
      <svg viewBox="0 0 192 192" className="absolute inset-0 size-full">
        <circle cx="96" cy="96" r="70" fill="none" className="stroke-foreground/15" strokeDasharray="4 7" />
        <circle cx="96" cy="96" r="40" fill="none" className="stroke-foreground/10" strokeDasharray="2 6" />
      </svg>
      {/* Merkezde aranan ama olmayan şey: nabız atan boş nokta */}
      <motion.span
        className="absolute top-1/2 left-1/2 size-3 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-dashed border-primary/60"
        animate={reduced ? undefined : { scale: [1, 1.8, 1], opacity: [0.9, 0.2, 0.9] }}
        transition={{ duration: 2.4, repeat: Infinity, ease: 'easeInOut' }}
      />
      <motion.div
        className="absolute inset-0"
        animate={reduced ? undefined : { rotate: 360 }}
        transition={{ duration: 9, repeat: Infinity, ease: 'linear' }}
      >
        <motion.span
          className={`${tile} absolute top-[26px] left-1/2 size-11 -translate-x-1/2 -translate-y-1/2 text-primary`}
          animate={reduced ? undefined : { rotate: -360 }}
          transition={{ duration: 9, repeat: Infinity, ease: 'linear' }}
        >
          <Search className="size-5" strokeWidth={1.75} />
        </motion.span>
      </motion.div>
      <motion.div
        className="absolute inset-0"
        animate={reduced ? undefined : { rotate: -360 }}
        transition={{ duration: 14, repeat: Infinity, ease: 'linear' }}
      >
        <span className="absolute bottom-[56px] left-[56px] size-2 rounded-full bg-foreground/30" />
        <span className="absolute top-[60px] right-[50px] size-1.5 rounded-full bg-primary/60" />
      </motion.div>
    </div>
  )
}

/** 403: kalkan, üzerinden geçen tarama ışığı ve arada bir sallanan kilit. */
export function LockedShieldArt() {
  const reduced = useReducedMotion()
  return (
    <div aria-hidden="true" className="relative grid size-48 place-items-center">
      <motion.span
        className="absolute size-36 rounded-full border border-destructive/25"
        animate={reduced ? undefined : { scale: [0.85, 1.15], opacity: [0.6, 0] }}
        transition={{ duration: 2.2, repeat: Infinity, ease: 'easeOut' }}
      />
      <div className={`${tile} size-24 overflow-hidden`}>
        <Shield className="absolute size-16 text-destructive/25" strokeWidth={1.25} />
        <motion.span
          className="relative text-destructive"
          animate={reduced ? undefined : { rotate: [0, -14, 12, -8, 6, 0] }}
          transition={{ duration: 0.7, repeat: Infinity, repeatDelay: 2.1, ease: 'easeInOut' }}
        >
          <Lock className="size-8" strokeWidth={1.75} />
        </motion.span>
        <motion.span
          className="absolute inset-x-0 h-8 bg-gradient-to-b from-transparent via-destructive/20 to-transparent"
          initial={{ y: -60 }}
          animate={reduced ? { opacity: 0 } : { y: [-60, 60] }}
          transition={{ duration: 1.8, repeat: Infinity, ease: 'easeInOut', repeatType: 'mirror' }}
        />
      </div>
    </div>
  )
}

/** Arayüz hatası: birbirinden uzaklaşıp yaklaşan fiş uçları, aralarında kıvılcım. */
export function UnpluggedArt() {
  const reduced = useReducedMotion()
  const swing = (dir: 1 | -1) =>
    reduced ? undefined : { x: [0, dir * 10, dir * 4, dir * 10, 0], rotate: [0, dir * 6, 0, dir * 4, 0] }
  return (
    <div aria-hidden="true" className="relative flex size-48 items-center justify-center">
      <motion.svg
        viewBox="0 0 64 40"
        className="h-14 w-20 text-foreground/70"
        animate={swing(-1)}
        transition={{ duration: 3, repeat: Infinity, ease: 'easeInOut' }}
      >
        <path d="M0 20h22" stroke="currentColor" strokeWidth="3" strokeLinecap="round" fill="none" />
        <rect x="22" y="8" width="24" height="24" rx="6" className="fill-card stroke-foreground/60" strokeWidth="2.5" />
        <path d="M46 14h12M46 26h12" stroke="currentColor" strokeWidth="3.5" strokeLinecap="round" />
      </motion.svg>
      <motion.span
        className="mx-1 text-[hsl(38_92%_50%)]"
        animate={reduced ? { opacity: 1 } : { opacity: [0, 1, 0.2, 1, 0], scale: [0.6, 1.2, 0.9, 1.1, 0.6] }}
        transition={{ duration: 1.6, repeat: Infinity, repeatDelay: 0.6 }}
      >
        <Zap className="size-6" fill="currentColor" strokeWidth={1.5} />
      </motion.span>
      <motion.svg
        viewBox="0 0 64 40"
        className="h-14 w-20 text-foreground/70"
        animate={swing(1)}
        transition={{ duration: 3, repeat: Infinity, ease: 'easeInOut' }}
      >
        <rect x="6" y="8" width="26" height="24" rx="6" className="fill-card stroke-foreground/60" strokeWidth="2.5" />
        <circle cx="13" cy="15" r="2" className="fill-destructive/70" />
        <circle cx="13" cy="25" r="2" className="fill-destructive/70" />
        <path d="M32 20h32" stroke="currentColor" strokeWidth="3" strokeLinecap="round" fill="none" />
      </motion.svg>
    </div>
  )
}

/** Çevrimdışı: Wi-Fi yayları dıştan içe sırayla söner, üstüne çapraz çizgi çizilir. */
export function OfflineArt() {
  const reduced = useReducedMotion()
  const arcs = ['M20 34a40 40 0 0 1 56 0', 'M29 44a27 27 0 0 1 38 0', 'M38 54a14 14 0 0 1 20 0']
  return (
    <div aria-hidden="true" className={`${tile} size-28`}>
      <svg viewBox="0 0 96 80" className="size-20 text-primary" fill="none" stroke="currentColor" strokeWidth="5" strokeLinecap="round">
        {arcs.map((d, i) => (
          <motion.path
            key={d}
            d={d}
            animate={reduced ? { opacity: 0.35 } : { opacity: [1, 0.15, 0.15, 1] }}
            transition={{ duration: 2.6, repeat: Infinity, delay: i * 0.25, times: [0, 0.3, 0.7, 1] }}
          />
        ))}
        <circle cx="48" cy="64" r="4" fill="currentColor" stroke="none" />
        <motion.path
          d="M18 12 78 72"
          className="text-destructive"
          stroke="currentColor"
          initial={{ pathLength: reduced ? 1 : 0 }}
          animate={{ pathLength: 1 }}
          transition={{ duration: 0.8, delay: 0.5, ease: 'easeOut' }}
        />
      </svg>
    </div>
  )
}

/** Oturum sona erdi: ters dönen kum saati, çevresinde boşalan süre halkası. */
export function HourglassArt() {
  const reduced = useReducedMotion()
  return (
    <div aria-hidden="true" className="relative grid size-48 place-items-center">
      <svg viewBox="0 0 120 120" className="absolute size-40 -rotate-90">
        <circle cx="60" cy="60" r="52" fill="none" className="stroke-foreground/10" strokeWidth="4" />
        <motion.circle
          cx="60"
          cy="60"
          r="52"
          fill="none"
          className="stroke-[hsl(38_92%_50%)]"
          strokeWidth="4"
          strokeLinecap="round"
          initial={{ pathLength: 1 }}
          animate={{ pathLength: reduced ? 0.02 : [1, 0.02] }}
          transition={{ duration: 2.4, ease: 'easeInOut' }}
        />
      </svg>
      <motion.span
        className={`${tile} size-20 text-[hsl(38_92%_50%)]`}
        animate={reduced ? undefined : { rotate: [0, 0, 180, 180] }}
        transition={{ duration: 3.2, repeat: Infinity, times: [0, 0.6, 0.85, 1], ease: 'easeInOut' }}
      >
        <Hourglass className="size-9" strokeWidth={1.5} />
      </motion.span>
    </div>
  )
}
