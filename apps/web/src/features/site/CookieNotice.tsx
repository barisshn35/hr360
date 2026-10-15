/**
 * Çerez bilgilendirmesi ve arama motoru meta etiketi.
 *
 * Uygulama yalnızca zorunlu çerez/depolama kullanır (giriş, güvenlik, tema/dil, taslaklar; reklam,
 * analiz ya da izleme yok). Bu nedenle rıza değil BİLGİLENDİRME bandı gösterilir: "Anladım" ile
 * kapanır, seçim yerel depolamada tutulur. Oturumlu ekranlarda (/panel) ve kiosk cihazında
 * gösterilmez; giriş sayfasında bir kez kapatıldıysa bir daha çıkmaz.
 */
import { useEffect, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { Cookie } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { tx } from '@/lib/i18n'
import { COOKIE_ANCHOR } from './constants'
import { usePublicSite } from './usePublicSite'

export const COOKIE_NOTICE_KEY = 'hr360.cookieNotice'

function acknowledged(): boolean {
  try {
    return localStorage.getItem(COOKIE_NOTICE_KEY) === '1'
  } catch {
    return false
  }
}

export function CookieNotice() {
  const { pathname } = useLocation()
  const reduced = useReducedMotion()
  const [done, setDone] = useState(acknowledged)
  const hiddenHere = pathname.startsWith('/panel') || pathname.startsWith('/kiosk')
  const visible = !done && !hiddenHere

  const accept = () => {
    try {
      localStorage.setItem(COOKIE_NOTICE_KEY, '1')
    } catch {
      /* gizli pencere: bu oturum boyunca kapalı kalır */
    }
    setDone(true)
  }

  return (
    <AnimatePresence>
      {visible && (
        <motion.aside
          key="cookie-notice"
          role="region"
          aria-label={tx('Çerez bilgilendirmesi')}
          data-testid="cookie-notice"
          initial={reduced ? { opacity: 0 } : { opacity: 0, y: 40, scale: 0.96 }}
          animate={{ opacity: 1, y: 0, scale: 1 }}
          exit={reduced ? { opacity: 0 } : { opacity: 0, y: 24, scale: 0.97 }}
          transition={reduced ? { duration: 0.15 } : { type: 'spring', stiffness: 320, damping: 28, delay: 0.8 }}
          className="fixed right-4 bottom-[max(1rem,env(safe-area-inset-bottom))] left-4 z-[60] sm:right-auto sm:max-w-[380px]"
        >
          <div className="flex gap-3.5 rounded-2xl border border-border bg-card/95 p-4 shadow-[0_18px_50px_-20px_rgba(0,0,0,0.55)] backdrop-blur-xl">
            <motion.span
              aria-hidden="true"
              className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-primary/12 text-primary"
              animate={reduced ? undefined : { rotate: [0, -14, 10, -6, 0] }}
              transition={{ duration: 1.1, delay: 1.3, ease: 'easeInOut' }}
            >
              <Cookie className="size-5" />
            </motion.span>
            <div className="min-w-0 flex-1">
              <p className="text-[13.5px] font-medium">{tx('Yalnızca zorunlu çerezler kullanılır')}</p>
              <p className="mt-1 text-[12.5px] leading-relaxed text-muted-foreground">
                {tx('Giriş, güvenlik ve tercihlerinizi hatırlamak için. Reklam ya da izleme çerezi yok.')}
              </p>
              <div className="mt-3 flex items-center gap-2">
                <Button size="sm" onClick={accept} data-testid="cookie-notice-ok">
                  {tx('Anladım')}
                </Button>
                <Button size="sm" variant="ghost" asChild>
                  <Link to={`/gizlilik#${COOKIE_ANCHOR}`}>{tx('Ayrıntılar')}</Link>
                </Button>
              </div>
            </div>
          </div>
        </motion.aside>
      )}
    </AnimatePresence>
  )
}

/**
 * Arama motorları kapalıyken (varsayılan) sayfalara noindex eklenir; robots.txt de her şeyi engeller.
 * Açıkken etiket kaldırılır.
 */
export function RobotsMeta() {
  const indexing = usePublicSite().data?.searchIndexing
  useEffect(() => {
    if (indexing === undefined) return
    let tag = document.querySelector<HTMLMetaElement>('meta[name="robots"]')
    if (indexing) {
      tag?.remove()
      return
    }
    if (!tag) {
      tag = document.createElement('meta')
      tag.name = 'robots'
      document.head.appendChild(tag)
    }
    tag.content = 'noindex, nofollow'
  }, [indexing])
  return null
}
