import { useState } from 'react'
import { Download, Share, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { dismissInstall, isIos, isStandalone, readInstallDismissed, shouldOfferInstall, useInstallPrompt } from '@/lib/pwa'
import { tx } from '@/lib/i18n'

/**
 * Dalga 12 (madde 85): "uygulamayı yükle" önerisi. Tarayıcı yükleme sunuyorsa (Chrome/Edge/Android)
 * tek düğme; iPhone/iPad Safari'de "Paylaş › Ana Ekrana Ekle" yönergesi. "Daha sonra" 30 gün susturur.
 * Sayfa akışında durur (sabit konumlu değil): içerik ve rıhtım düğmelerinin üstünü kapatmaz.
 */
export function InstallPrompt() {
  const install = useInstallPrompt()
  const [dismissed, setDismissed] = useState(() => readInstallDismissed())
  const [howOpen, setHowOpen] = useState(false)
  const mode = shouldOfferInstall({ hasPrompt: Boolean(install), ios: isIos(), standalone: isStandalone(), dismissedAt: dismissed, now: Date.now() })
  if (!mode) return null
  const later = () => {
    const now = Date.now()
    dismissInstall(now)
    setDismissed(now)
  }
  return (
    <div className="mx-auto mt-3 w-full max-w-[1480px] px-3 sm:px-5">
      <div className="rounded-xl border border-primary/25 bg-primary/[0.06] px-3 py-2 text-[13px] sm:px-4 sm:py-3">
        {/* Dar ekranda tek satır: kısa metin + "Nasıl?" açılır ayrıntı + kapat (iOS yönergesi çok yer kaplıyordu). */}
        <div className="flex items-center gap-2 sm:gap-3">
          {mode === 'prompt' ? <Download className="size-4 shrink-0 text-primary" /> : <Share className="size-4 shrink-0 text-primary" />}
          <p className="min-w-0 flex-1 truncate sm:whitespace-normal">
            {mode === 'prompt'
              ? <><span className="sm:hidden">{tx('HR360\'ı ana ekranınıza ekleyin.')}</span><span className="hidden sm:inline">{tx('HR360\'ı ana ekranınıza ekleyin: tek dokunuşla açılır, bildirimler gelir, bağlantı koptuğunda izin ve masraf taslaklarınız saklanır.')}</span></>
              : tx('HR360\'ı ana ekrana ekleyin.')}
          </p>
          {mode === 'prompt' && install && (
            <Button size="sm" onClick={() => void install()}>{tx('Yükle')}</Button>
          )}
          {mode === 'ios' && (
            <Button size="sm" variant="outline" aria-expanded={howOpen} aria-controls="install-how" onClick={() => setHowOpen((v) => !v)}>
              {tx('Nasıl?')}
            </Button>
          )}
          <Button size="sm" variant="ghost" className="shrink-0 px-2" onClick={later} aria-label={tx('Yükleme önerisini kapat')} title={tx('Daha sonra')}>
            <X className="size-4" />
            <span className="hidden sm:inline">{tx('Daha sonra')}</span>
          </Button>
        </div>
        {mode === 'ios' && howOpen && (
          <p id="install-how" className="mt-2 text-[12.5px] leading-relaxed text-muted-foreground">
            {tx('HR360\'ı ana ekrana eklemek için Safari\'de Paylaş düğmesine, ardından "Ana Ekrana Ekle"ye dokunun. Anlık bildirimler iOS 16.4 ve sonrasında yalnızca ana ekrandan açılan uygulamada çalışır.')}
          </p>
        )}
      </div>
    </div>
  )
}
