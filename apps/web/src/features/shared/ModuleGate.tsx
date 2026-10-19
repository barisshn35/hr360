import type { ReactNode } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { Home } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ErrorScene } from '@/components/errors/ErrorScene'
import { UnpluggedArt } from '@/components/errors/art'
import { CenteredSpinner, FullPageSpinner } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { useModules, type ModuleId } from '@/lib/modules'
import { tx } from '@/lib/i18n'

export const moduleLabels = (): Record<ModuleId, string> => ({
  recruitment: tx('İşe alım'),
  onboarding: tx('Onboarding ve zimmet'),
  timeshift: tx('Puantaj ve vardiya'),
  performance: tx('Performans'),
  learning: tx('Eğitim'),
  compensation: tx('Ücret ve bordro'),
  ml: tx('Yapay zekâ'),
  expense: tx('Masraf ve seyahat'),
})

/** Kurulumda kapalı modülün ekranı açılınca: ne olduğunu ve kimin açabileceğini söyler. */
export function ModuleNotInstalled({ module, standalone = false }: { module: ModuleId; standalone?: boolean }) {
  const { can, status } = useAuth()
  const { tenancy } = useModules()
  // Herkese açık sayfalarda (kariyer, kiosk) oturum yok: yönetici satırı gösterilmez.
  const admin = !standalone && status === 'authenticated' && (can('platform:manage') || can('tenant:manage'))
  return (
    <ErrorScene
      testId="module-not-installed"
      fullPage={standalone}
      tone="warning"
      art={<UnpluggedArt />}
      title={tx('Bu modül kurulu değil')}
      detail={
        <>
          {tx('{0} modülü bu kurulumda açık değil.', [moduleLabels()[module]])}{' '}
          {standalone ? null : admin ? (tenancy === 'single' ? tx('Yönetim › Bakım › Modüller\'den açabilirsiniz.') : tx('Platform › Bakım › Modüller\'den açabilirsiniz.')) : tx('Yöneticinize başvurun.')}
        </>
      }
      actions={
        standalone ? undefined : (
          <Button asChild>
            <Link to="/panel">
              <Home className="size-4" />
              {tx('Genel bakışa dön')}
            </Link>
          </Button>
        )
      }
    />
  )
}

/** Rota sarmalayıcısı: modül kapalıysa sayfa yerine bilgi ekranı (sayfanın 404'lü istekleri hiç atılmaz). */
export function RequireModule({ module, standalone = false, children }: { module: ModuleId; standalone?: boolean; children: ReactNode }) {
  const { isEnabled, loading } = useModules()
  if (loading) return standalone ? <FullPageSpinner /> : <CenteredSpinner />
  if (!isEnabled(module)) return <ModuleNotInstalled module={module} standalone={standalone} />
  return <>{children}</>
}

/** Rota sarmalayıcısı: tek şirket kipinde olmayan ekranlar (şirket kaydı, kiracı listesi) `to` adresine yönlenir. */
export function MultiTenantOnly({ to, children }: { to: string; children: ReactNode }) {
  const { tenancy } = useModules()
  if (tenancy === 'single') return <Navigate to={to} replace />
  return <>{children}</>
}
