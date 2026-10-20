/**
 * Herkese açık sayfaların ortak düzeni: üstte marka + sayfa bağlantıları (etkin sayfanın altında
 * kayan gösterge), ortada içerik, altta alt bilgi. Alt bilgi giriş ve kayıt sayfalarında da kullanılır.
 */
import { useEffect, type ReactNode } from 'react'
import { Link, NavLink } from 'react-router-dom'
import { motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { ArrowLeft, CodeXml } from 'lucide-react'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { EASE } from '@/motion/primitives'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { SOURCE_URL } from './constants'
import { usePublicSite } from './usePublicSite'

export const SITE_LINKS = [
  { to: '/sss', label: tx('SSS') },
  { to: '/gizlilik', label: tx('Gizlilik') },
  { to: '/kosullar', label: tx('Kullanım koşulları') },
  { to: '/iletisim', label: tx('İletişim') },
] as const

/** Giriş, kayıt ve herkese açık sayfaların alt bilgisi. */
export function SiteFooter({ className }: { className?: string }) {
  const site = usePublicSite().data
  const year = new Date().getFullYear()
  return (
    <footer data-testid="site-footer" className={cn('relative z-10 w-full text-[12.5px] text-muted-foreground', className)}>
      <div className="mx-auto flex w-full max-w-5xl flex-col items-center gap-3 border-t border-border/60 px-5 py-5 sm:flex-row sm:justify-between">
        <nav aria-label={tx('Site bağlantıları')} className="flex flex-wrap items-center justify-center gap-x-5 gap-y-1.5">
          {SITE_LINKS.map((l) => (
            <Link key={l.to} to={l.to} className="transition-colors hover:text-foreground focus-visible:text-foreground focus-visible:underline">
              {l.label}
            </Link>
          ))}
        </nav>
        <p className="flex flex-wrap items-center justify-center gap-x-2 text-center">
          <span>© {year} {site?.operatorName || 'HR360'}</span>
          <span aria-hidden="true">·</span>
          <a href={SOURCE_URL} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 transition-colors hover:text-foreground">
            <CodeXml className="size-3.5" aria-hidden="true" />
            {tx('Açık kaynak (AGPL-3.0)')}
          </a>
        </p>
      </div>
    </footer>
  )
}

export function PublicLayout({
  eyebrow,
  title,
  intro,
  children,
  testId,
  wide = false,
}: {
  eyebrow?: string
  title: string
  intro?: ReactNode
  children: ReactNode
  testId?: string
  wide?: boolean
}) {
  const reduced = useReducedMotion()
  const up = (delay: number) =>
    reduced ? {} : { initial: { opacity: 0, y: 16 }, animate: { opacity: 1, y: 0 }, transition: { duration: 0.6, delay, ease: EASE } }

  useEffect(() => {
    document.title = `${title} · HR360`
  }, [title])

  return (
    <div data-testid={testId} className="relative flex min-h-dvh flex-col overflow-x-clip bg-background text-foreground">
      <AmbientBackground meteors={false} />
      <header className="sticky top-0 z-20 border-b border-border/60 bg-background/70 backdrop-blur-xl">
        <div className="mx-auto flex h-16 w-full max-w-5xl items-center gap-4 px-5">
          <Link to="/giris" className="flex shrink-0 items-center gap-2.5 text-[16px] font-semibold tracking-tight" aria-label={tx('HR360 giriş sayfası')}>
            <span className="flex size-8 items-center justify-center rounded-[10px] bg-gradient-to-br from-primary to-[hsl(var(--brand-deep))] shadow-[0_0_24px_-6px_hsl(var(--primary))]">
              <img src="/icon-white.svg" alt="" aria-hidden="true" className="size-5" />
            </span>
            HR360
          </Link>
          <nav aria-label={tx('Sayfalar')} className="ml-auto hidden items-center gap-1 text-[13.5px] sm:flex">
            {SITE_LINKS.map((l) => (
              <NavLink
                key={l.to}
                to={l.to}
                className={({ isActive }) =>
                  cn('relative rounded-full px-3 py-1.5 transition-colors', isActive ? 'text-foreground' : 'text-muted-foreground hover:text-foreground')
                }
              >
                {({ isActive }) => (
                  <>
                    {isActive && (
                      <motion.span
                        layoutId="site-nav-pill"
                        className="absolute inset-0 -z-10 rounded-full bg-muted"
                        transition={reduced ? { duration: 0 } : { type: 'spring', stiffness: 420, damping: 34 }}
                      />
                    )}
                    {l.label}
                  </>
                )}
              </NavLink>
            ))}
          </nav>
          <Link
            to="/giris"
            className="ml-auto inline-flex items-center gap-1.5 rounded-full border border-border px-3 py-1.5 text-[13px] text-muted-foreground transition-colors hover:text-foreground sm:ml-2"
          >
            <ArrowLeft className="size-3.5" aria-hidden="true" />
            {tx('Giriş')}
          </Link>
        </div>
        {/* Telefon genişliğinde bağlantılar ikinci satırda, yatay kaydırılabilir */}
        <nav aria-label={tx('Sayfalar')} className="flex gap-1 overflow-x-auto px-4 pb-2 text-[13px] sm:hidden">
          {SITE_LINKS.map((l) => (
            <NavLink
              key={l.to}
              to={l.to}
              className={({ isActive }) =>
                cn('shrink-0 rounded-full px-3 py-1', isActive ? 'bg-muted text-foreground' : 'text-muted-foreground')
              }
            >
              {l.label}
            </NavLink>
          ))}
        </nav>
      </header>

      <main className={cn('relative z-10 mx-auto w-full flex-1 px-5 pt-12 pb-16 sm:pt-16', wide ? 'max-w-5xl' : 'max-w-3xl')}>
        {eyebrow && (
          <motion.p {...up(0)} className="text-[12px] font-medium tracking-[0.14em] text-primary uppercase">
            {eyebrow}
          </motion.p>
        )}
        <motion.h1 {...up(0.05)} className="mt-2 text-[34px] leading-[1.1] font-semibold tracking-[-0.035em] text-balance sm:text-[44px]">
          {title}
        </motion.h1>
        {intro && (
          <motion.div {...up(0.12)} className="mt-4 max-w-2xl text-[15.5px] leading-relaxed text-muted-foreground">
            {intro}
          </motion.div>
        )}
        <motion.div {...up(0.2)} className="mt-10">
          {children}
        </motion.div>
      </main>

      <SiteFooter />
    </div>
  )
}
