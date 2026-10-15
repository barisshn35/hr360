import { useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { tx } from '@/lib/i18n'

/**
 * Sayfa değişimini ekran okuyucuya duyurur ve odağı yeni sayfanın ana başlığına taşır (G23).
 *
 * SPA'da rota değişince tarayıcı bir şey okumaz ve odak eski bağlantıda kalır. Burada:
 *  - aria-live bölgesine "<sayfa başlığı> sayfası açıldı" yazılır (WCAG 4.1.3),
 *  - odak `main h1`'e (yoksa `main`e) taşınır (WCAG 2.4.3); kullanıcı o arada sayfa içinde
 *    bir şeye odaklandıysa (ör. yazmaya başladıysa) odak çalınmaz.
 * Sayfa geçiş animasyonu yeni sayfayı biraz geç bastığı için başlık kısa süre beklenir.
 * Yalnızca yol (pathname) değişiminde çalışır; sekme/filtre (?query) değişimi duyurulmaz.
 */
export function RouteAnnouncer() {
  const { pathname } = useLocation()
  const [message, setMessage] = useState('')
  const first = useRef(true)

  useEffect(() => {
    if (first.current) {
      first.current = false
      return
    }
    const main = document.getElementById('main-content')
    const before = main?.querySelector('h1') ?? null
    const started = Date.now()
    let timer = 0

    const settle = () => {
      const h1 = main?.querySelector<HTMLElement>('h1') ?? null
      const fresh = h1 && h1 !== before && h1.isConnected
      if (!fresh && Date.now() - started < 2500) {
        timer = window.setTimeout(settle, 120)
        return
      }
      const title = document.title.replace(/\s*·\s*HR360.*$/, '').trim() || h1?.textContent?.trim() || 'HR360'
      setMessage(tx('{0} sayfası açıldı', [title]))

      // Odak, kullanıcının sayfa içinde kendi seçtiği bir öğede değilse taşınır.
      const active = document.activeElement as HTMLElement | null
      const userInside = active && active !== document.body && main?.contains(active) && active !== main
      if (userInside) return
      const target = (fresh ? h1 : main) as HTMLElement | null
      if (!target) return
      if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1')
      target.focus({ preventScroll: true })
    }
    timer = window.setTimeout(settle, 60)
    return () => window.clearTimeout(timer)
  }, [pathname])

  return (
    <div role="status" aria-live="polite" aria-atomic="true" className="sr-only" data-testid="route-announcer">
      {message}
    </div>
  )
}
