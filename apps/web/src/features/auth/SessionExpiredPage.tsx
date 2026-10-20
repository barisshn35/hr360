import { Navigate, useSearchParams } from 'react-router-dom'
import { KeyRound } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { AppShellSkeleton } from '@/components/ui/app-shell-skeleton'
import { ErrorScene } from '@/components/errors/ErrorScene'
import { HourglassArt } from '@/components/errors/art'
import { useAuth } from '@/auth/useAuth'
import { clearSessionExpired } from '@/auth/sessionExpiry'
import { tx } from '@/lib/i18n'

/** Açık oturum kendiliğinden düştüğünde: ne olduğunu söyler, kaldığı sayfaya geri götürür. */
export function SessionExpiredPage() {
  const { status, login } = useAuth()
  const [params] = useSearchParams()
  const raw = params.get('devam') || '/panel'
  // Yalnızca uygulama içi adres: açık yönlendirme olmasın.
  const next = raw.startsWith('/') && !raw.startsWith('//') ? raw : '/panel'

  if (status === 'loading') return <AppShellSkeleton label={tx('Oturum doğrulanıyor')} />
  if (status === 'authenticated') return <Navigate to={next} replace />

  return (
    <ErrorScene
      fullPage
      testId="session-expired"
      tone="warning"
      art={<HourglassArt />}
      title={tx('Oturumunuz sona erdi')}
      detail={tx('Güvenliğiniz için uzun süre işlem yapılmayan oturumlar kapatılır. Yeniden giriş yaptığınızda kaldığınız sayfaya dönersiniz.')}
      actions={
        <>
          <Button
            size="lg"
            onClick={() => {
              clearSessionExpired()
              login(next)
            }}
          >
            <KeyRound className="size-4" />
            {tx('Yeniden giriş yap')}
          </Button>
          <Button size="lg" variant="outline" asChild>
            <a href="/giris" onClick={() => clearSessionExpired()}>
              {tx('Giriş sayfası')}
            </a>
          </Button>
        </>
      }
    />
  )
}
