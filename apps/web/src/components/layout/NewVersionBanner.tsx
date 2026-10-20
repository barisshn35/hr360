import { useEffect, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Callout } from '@/components/ui/callout'
import { tx } from '@/lib/i18n'

const POLL_MS = 5 * 60_000

/**
 * Yeni sürüm uyarısı. Güncellemeden sonra açık kalan sekmeler eski paketle çalışmaya devam ediyordu
 * (ör. içe aktarma penceresinin eski hâli). Sekme /version.json'u beş dakikada bir ve sekmeye
 * dönüldüğünde yoklar; derleme kimliği değiştiyse yenileme şeridi gösterilir. Yenileme zorlanmaz:
 * yarım kalmış bir form kaybolmasın diye kullanıcı ne zaman yenileyeceğine kendisi karar verir.
 */
export function NewVersionBanner() {
  const [next, setNext] = useState<string | null>(null)

  useEffect(() => {
    if (import.meta.env.DEV) return
    let stopped = false
    const check = async () => {
      if (stopped || document.visibilityState !== 'visible') return
      try {
        const res = await fetch('/version.json', { cache: 'no-store' })
        if (!res.ok) return
        const v = (await res.json()) as { version?: string; build?: string }
        if (v.build && v.build !== __BUILD_ID__) setNext(v.version ?? '')
      } catch {
        // çevrimdışı ya da geçici hata: sonraki yoklamada yeniden denenir
      }
    }
    const timer = window.setInterval(() => void check(), POLL_MS)
    const onVisible = () => void check()
    document.addEventListener('visibilitychange', onVisible)
    void check()
    return () => { stopped = true; window.clearInterval(timer); document.removeEventListener('visibilitychange', onVisible) }
  }, [])

  if (next === null) return null
  return (
    <div className="mx-auto mt-3 w-full max-w-[1480px] px-3 sm:px-5">
      <div role="status">
        <Callout tone="info" icon={RefreshCw} action={<Button size="sm" onClick={() => window.location.reload()}>{tx('Yenile')}</Button>}>
          {next && next !== __APP_VERSION__ ? tx('HR360 {0} yayımlandı. Yeni özellikleri görmek için sayfayı yenileyin.', [next]) : tx('Uygulamanın yeni bir sürümü hazır. Yeni özellikleri görmek için sayfayı yenileyin.')}
        </Callout>
      </div>
    </div>
  )
}
