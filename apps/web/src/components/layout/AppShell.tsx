import { Suspense, lazy, useCallback, useLayoutEffect, useState } from 'react'
import { Outlet, useLocation } from 'react-router-dom'
import { motion } from 'motion/react'
import { useReducedMotion } from '@/lib/motion'
import { TriangleAlert } from 'lucide-react'
import { OfflineQueueWatcher } from '@/features/profile/DevicePanel'
import { useMyEmployeeId, useUnreadCount } from '@/api/queries'
import { useDirectory } from '@/api/directory'
import { useAuth } from '@/auth/useAuth'
import { AmbientBackground } from '@/components/fx/ambient-background'
import { TopNav } from './TopNav'
import { AppDock } from './AppDock'
import { CommandPalette } from './CommandPalette'
import { KeyboardShortcuts } from './KeyboardShortcuts'
import { RouteAnnouncer } from './RouteAnnouncer'
import { ConsentBanner } from './ConsentBanner'
import { NewVersionBanner } from './NewVersionBanner'
import { PageFeedback, usePageViews } from './PageFeedback'
import { PlatformAccessBanner } from './PlatformAccessBanner'
import { InstallPrompt } from './InstallPrompt'
import { locate } from './nav-config'
import { tx } from '@/lib/i18n'
import { useServerLanguage } from '@/lib/languageSync'

/**
 * Panelin dış kabuğu ("Zümrüt Yörünge" düzeni).
 *
 * Sol kenar çubuğu yok: modüllere üstte yüzen menüden (bölüm sekmeleri +
 * mega menü), en sık kullanılanlara alttaki rıhtımdan (dock) gidilir. Zemin
 * canlı (aurora, ızgara, meteorlar, imleç ışığı); sayfalar bulanıklıktan
 * netleşerek geçer.
 */
/** İK asistanı ayrı paket: ilk yükleme ağırlaşmasın. */
const AssistantWidget = lazy(async () => ({ default: (await import('./AssistantWidget')).AssistantWidget }))

export function AppShell() {
  usePageViews()
  const [paletteOpen, setPaletteOpen] = useState(false)
  const location = useLocation()
  const reduced = useReducedMotion()
  const { can, status, tenantSlug, roles } = useAuth()

  // NOT: `user.id` Keycloak `sub` claim'i, bildirimlerin yazıldığı Employee.Id
  // DEĞİL - rozet bu yüzden çalışan kimliğiyle sorgulanır (bkz. NotificationBell.tsx).
  const canSeeNotifications = can('notification:view')
  const { employeeId: myEmployeeId } = useMyEmployeeId(canSeeNotifications)
  const unread = useUnreadCount(canSeeNotifications ? myEmployeeId : undefined)
  // Çalışan dizini açılışta bir kez çekilir; tüm ekranlar kimlikten adı buradan çözer.
  useDirectory()
  // Bu tarayıcıda dil seçilmediyse kullanıcının sunucudaki dil tercihini uygula.
  useServerLanguage(status === 'authenticated')

  // Sayfa başlığı (WCAG 2.4.2): menüdeki konumdan. Yerleşim efekti olduğu için PageHeader'ın
  // (sonra çalışan) efekti daha özgül başlığı üzerine yazar.
  useLayoutEffect(() => {
    const here = locate(location.pathname)
    document.title = here.item ? `${here.item.title} · HR360` : 'HR360 Enterprise'
  }, [location.pathname])
  const openPalette = useCallback(() => setPaletteOpen(true), [])

  /**
   * `organization` claim'i gelmediyse backend hiçbir kaydı döndürmez ama HATA
   * DA VERMEZ; kullanıcı her ekranı boş görür. Sessiz kalmak yerine söylüyoruz.
   * Platform yöneticisi hiçbir kiracıya bağlı değildir; onun için yanlış alarm olurdu.
   */
  const tenantClaimMissing =
    status === 'authenticated' && !tenantSlug && !roles.includes('platform-admin')

  return (
    <div className="relative min-h-dvh bg-background">
      {/* WCAG 2.4.1: klavyeyle ilk sekmede görünen "içeriğe atla" bağlantısı. */}
      <a
        href="#main-content"
        onClick={(e) => {
          e.preventDefault()
          document.getElementById('main-content')?.focus()
        }}
        className="sr-only z-[300] rounded-xl bg-primary px-4 py-2 text-[13px] font-semibold text-primary-foreground shadow-lg focus:not-sr-only focus:fixed focus:top-3 focus:left-3"
      >
        {tx('İçeriğe atla')}
      </a>
      <AmbientBackground />

      <div className="relative z-10 flex min-h-dvh flex-col">
        <TopNav onOpenCommandPalette={openPalette} />

        {tenantClaimMissing && (
          <div className="mx-auto mt-3 w-full max-w-[1480px] px-3 sm:px-5">
            <div
              role="alert"
              className="flex items-start gap-2.5 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 px-4 py-3 text-[13px] leading-relaxed"
            >
              <TriangleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" />
              <p>
                <strong className="font-semibold">{tx('Oturumunuzda kiracı bilgisi yok.')}</strong>{' '}{tx('Kimlik doğrulama', [])}{' '}
                <code className="font-mono text-[12px]">{tx('organization')}</code>{' '}{tx('kapsamı olmadan tamamlandığı için listeler boş görünecek. Oturumu kapatıp yeniden girin; sorun sürerse sistem yöneticinize bildirin.')}
              </p>
            </div>
          </div>
        )}

        <NewVersionBanner />
        <PlatformAccessBanner />
        <ConsentBanner />
        <InstallPrompt />

        <main id="main-content" tabIndex={-1} className="mx-auto w-full outline-none max-w-[1480px] min-w-0 flex-1 px-4 pt-7 pb-32 sm:px-6 lg:px-8 lg:pt-9">
          {/* Yalnızca giriş animasyonu: çıkış animasyonu (AnimatePresence) çıkan sayfa yeni adresi okuyup
              içindeki "exit"li öğeleri (tablo satırları vb.) kaldırınca hiç tamamlanmıyor, yeni sayfa
              bağlanmıyor ya da görünmez eski sayfa üstte kalıyordu (?durum= sekmeli listeler). */}
          <motion.div
            key={location.pathname}
            initial={reduced ? false : { opacity: 0, y: 14, filter: 'blur(8px)', scale: 0.995 }}
            // Animasyon bitince filter/transform kaldırılır: "blur(0px)" ya da transform kalırsa
            // içerideki position:fixed öğeler (sunum modu, kaplamalar) bu kutuya hapsolur.
            animate={{ opacity: 1, y: 0, filter: 'blur(0px)', scale: 1, transitionEnd: { filter: 'none', transform: 'none' } }}
            transition={{ duration: 0.45, ease: [0.16, 1, 0.3, 1] }}
          >
            <Outlet />
          </motion.div>
          <PageFeedback />
        </main>
      </div>

      <AppDock unreadCount={unread.data?.unreadCount ?? 0} onOpenCommandPalette={() => setPaletteOpen(true)} />
      <Suspense fallback={null}><AssistantWidget /></Suspense>
      <OfflineQueueWatcher />
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <KeyboardShortcuts onOpenPalette={openPalette} />
      <RouteAnnouncer />
    </div>
  )
}
