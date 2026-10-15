import { type ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { AppShellSkeleton } from '@/components/ui/AppShellSkeleton'
import { useAuth } from './useAuth'
import { isSessionExpired } from './sessionExpiry'
import { tx } from '@/lib/i18n'

/**
 * Panel rotalarını korur.
 *
 * Oturum yoksa kullanıcı /giris sayfasına gönderilir; istenen adres
 * "devam" parametresinde taşınır, giriş sonrası oraya dönülür. Böylece
 * derin bağlantı korunur ve tüm girişler tek bir kapıdan geçer.
 */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useAuth()
  const location = useLocation()

  if (status === 'authenticated') return <>{children}</>

  if (status === 'anonymous' || status === 'error') {
    const next = encodeURIComponent(location.pathname + location.search)
    if (status === 'anonymous' && isSessionExpired()) return <Navigate to={`/oturum-sona-erdi?devam=${next}`} replace />
    return <Navigate to={`/giris?devam=${next}`} replace />
  }

  return <AppShellSkeleton label={tx('Oturum doğrulanıyor')} />
}
