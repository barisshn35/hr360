import { useQuery } from '@tanstack/react-query'
import { Download, Smartphone } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { tx } from '@/lib/i18n'

const APK = '/indir/HR360.apk'

/** Android uygulaması indirme (yenilik haritası #6). Dosya sunucuda yayımlanmışsa görünür. */
export function AndroidAppPanel() {
  const q = useQuery({
    queryKey: ['android-apk'],
    queryFn: async ({ signal }) => (await fetch(APK, { method: 'HEAD', signal, cache: 'no-store' })).ok,
    staleTime: 60 * 60_000, retry: false,
  })
  if (!q.data) return null
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Smartphone className="size-4 text-primary" />{tx('Android uygulaması')}</span>}
        note={tx('Telefonunuza kurun; ilk açılışta bu adresi girin: {0}', [window.location.origin])} />
      <PanelBody className="space-y-3">
        <Button asChild><a href={APK} download="HR360.apk"><Download className="size-4" />{tx('APK dosyasını indir')}</a></Button>
        <p className="text-[12.5px] text-muted-foreground">{tx('Uygulama mağaza dışından kurulduğu için Android bir kez "bilinmeyen uygulamaları yükleme" izni ister. İndirdiğiniz dosyayı yalnızca bu adresten alın.')}</p>
      </PanelBody>
    </Panel>
  )
}
