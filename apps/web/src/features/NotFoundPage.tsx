import { Link, useLocation, useNavigate } from 'react-router-dom'
import { ArrowLeft, Home } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ErrorScene } from '@/components/errors/ErrorScene'
import { LostOrbitArt } from '@/components/errors/art'
import { tx } from '@/lib/i18n'

/**
 * 404. Panel içinde kabuğun içinde, panel dışındaki bilinmeyen adreslerde
 * (`standalone`) tam ekran gösterilir; adres girişe yönlendirilmez ki yanlış
 * bağlantı fark edilsin.
 */
export function NotFoundPage({ standalone = false }: { standalone?: boolean }) {
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const canGoBack = typeof window !== 'undefined' && window.history.length > 1
  return (
    <ErrorScene
      testId="not-found"
      fullPage={standalone}
      art={<LostOrbitArt />}
      code="404"
      title={tx('Sayfa bulunamadı')}
      detail={
        <>
          {tx('Aradığınız adres taşınmış ya da hiç var olmamış olabilir.')}
          {standalone && pathname !== '/' && (
            <code className="mt-2 block truncate rounded-md bg-muted px-2 py-1 font-mono text-[12px] text-muted-foreground">{pathname}</code>
          )}
        </>
      }
      actions={
        <>
          <Button asChild>
            <Link to={standalone ? '/' : '/panel'}>
              <Home className="size-4" />
              {standalone ? tx('Ana sayfaya dön') : tx('Genel bakışa dön')}
            </Link>
          </Button>
          {canGoBack && (
            <Button variant="outline" onClick={() => navigate(-1)}>
              <ArrowLeft className="size-4" />
              {tx('Geri git')}
            </Button>
          )}
        </>
      }
    />
  )
}
