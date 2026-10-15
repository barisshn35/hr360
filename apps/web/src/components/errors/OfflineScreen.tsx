import { useEffect } from 'react'
import { RotateCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ErrorScene } from './ErrorScene'
import { OfflineArt } from './art'
import { tx } from '@/lib/i18n'

/**
 * Bağlantı yokken kimlik sunucusuna ulaşılamadığında gösterilir. Bağlantı
 * geri geldiğinde sayfa kendiliğinden yenilenir.
 */
export function OfflineScreen() {
  useEffect(() => {
    const back = () => window.location.reload()
    window.addEventListener('online', back)
    return () => window.removeEventListener('online', back)
  }, [])

  return (
    <ErrorScene
      fullPage
      testId="offline"
      art={<OfflineArt />}
      title={tx('İnternet bağlantısı yok')}
      detail={tx('Cihazınız çevrimdışı görünüyor. Bağlantı geri geldiğinde sayfa kendiliğinden yenilenir.')}
      actions={
        <Button variant="outline" onClick={() => window.location.reload()}>
          <RotateCw className="size-4" />
          {tx('Yeniden dene')}
        </Button>
      }
    />
  )
}
