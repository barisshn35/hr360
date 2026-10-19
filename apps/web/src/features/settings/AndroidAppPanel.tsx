import { useQuery } from '@tanstack/react-query'
import { Download, Smartphone } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { tx } from '@/lib/i18n'
import { MODULE_IDS, useModules } from '@/lib/modules'

const APK = '/indir/HR360.apk'
/** Hafif sürüm uygulaması (scripts/android-build.sh --lite): ayrı ad ve uygulama kimliği. */
const APK_LITE = '/indir/HR360-Lite.apk'

async function exists(url: string, signal: AbortSignal) {
  return (await fetch(url, { method: 'HEAD', signal, cache: 'no-store' })).ok
}

/** Android uygulaması indirme (yenilik haritası #6). Dosya sunucuda yayımlanmışsa görünür. */
export function AndroidAppPanel() {
  const { enabled } = useModules()
  const q = useQuery({
    queryKey: ['android-apk'],
    queryFn: async ({ signal }) => ({ full: await exists(APK, signal), lite: await exists(APK_LITE, signal) }),
    staleTime: 60 * 60_000, retry: false,
  })
  if (!q.data || (!q.data.full && !q.data.lite)) return null
  // Bazı modüller kapalıysa (Hafif kurulum) önce HR360 Lite önerilir; ikisi de aynı adrese bağlanır.
  const preferLite = q.data.lite && (!q.data.full || enabled.size < MODULE_IDS.length)
  const main = preferLite ? { href: APK_LITE, file: 'HR360-Lite.apk' } : { href: APK, file: 'HR360.apk' }
  const other = preferLite ? (q.data.full ? { href: APK, file: 'HR360.apk' } : null) : (q.data.lite ? { href: APK_LITE, file: 'HR360-Lite.apk' } : null)
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Smartphone className="size-4 text-primary" />{tx('Android uygulaması')}</span>}
        note={tx('Telefonunuza kurun; ilk açılışta bu adresi girin: {0}', [window.location.origin])} />
      <PanelBody className="space-y-3">
        <div className="flex flex-wrap items-center gap-3">
          <Button asChild><a href={main.href} download={main.file}><Download className="size-4" />{preferLite ? tx('HR360 Lite APK dosyasını indir') : tx('APK dosyasını indir')}</a></Button>
          {other && (
            <a className="text-[13px] text-primary underline-offset-2 hover:underline" href={other.href} download={other.file}>
              {preferLite ? tx('Tam sürüm uygulaması (HR360)') : tx('Hafif sürüm uygulaması (HR360 Lite)')}
            </a>
          )}
        </div>
        <p className="text-[12.5px] text-muted-foreground">{tx('Uygulama mağaza dışından kurulduğu için Android bir kez "bilinmeyen uygulamaları yükleme" izni ister. İndirdiğiniz dosyayı yalnızca bu adresten alın.')}</p>
      </PanelBody>
    </Panel>
  )
}
