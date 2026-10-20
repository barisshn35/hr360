import { useQuery } from '@tanstack/react-query'
import { Eraser, Sparkles } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { RowsSkeleton } from '@/components/ui/states'
import { useConfirm } from '@/components/ui/confirm'
import { useAction } from '@/features/shared/kit'
import { sampleDataApi } from '@/api/sampleData'
import { tx } from '@/lib/i18n'

/**
 * Örnek veri (yenilik haritası #35): boş bir şirketi denemek için tek tıkla örnek departman, çalışan,
 * takdir, fikir ve etkinlik; sonra tek tıkla temizlik. <paramref name="slug"/> verilirse platform
 * yöneticisi başka bir şirket için çalışır.
 */
export function SampleDataPanel({ slug }: { slug?: string }) {
  const key = ['sample-data', slug ?? 'mine']
  const q = useQuery({ queryKey: key, queryFn: ({ signal }) => sampleDataApi.status(slug, signal) })
  const confirm = useConfirm()
  const inv = [key, ['employees'], ['departments'], ['kudos'], ['ideas'], ['events']]
  const fill = useAction(() => sampleDataApi.fill(slug), { success: tx('Örnek veriler eklendi. Çalışanlar, departmanlar ve topluluk sayfalarına göz atabilirsiniz.'), invalidate: inv })
  const remove = useAction(() => sampleDataApi.remove(slug), { success: tx('Örnek veriler kaldırıldı.'), invalidate: inv })
  const s = q.data
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Sparkles className="size-4 text-primary" />{tx('Örnek veriler')}</span>}
        note={tx('Uygulamayı boş bir şirkette denemek için örnek çalışanlar, departmanlar, takdirler, fikirler ve bir etkinlik ekleyin; işiniz bitince tek tıkla kaldırın.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : !s ? null : s.hasSample ? (
          <>
            <p className="text-[13px]">{tx('Şirkette {0} örnek çalışan var. Örnek kişilerin e-posta adresleri gerçek değildir; onlara e-posta gitmez ve giriş hesapları yoktur.', [s.sampleEmployees])}</p>
            <Button variant="outline" disabled={remove.isPending} onClick={async () => {
              if (await confirm({ title: tx('Örnek veriler kaldırılsın mı?'), note: tx('Örnek çalışanlar, onların takdirleri, fikirleri, etkinlik katılımları ve örnek veriyle açılan departmanlar silinir. Sizin eklediğiniz kayıtlara dokunulmaz.'), action: tx('Kaldır') }))
                remove.mutate(undefined)
            }}><Eraser className="size-4" />{tx('Örnek verileri kaldır')}</Button>
          </>
        ) : s.canFill ? (
          <Button disabled={fill.isPending} onClick={async () => {
            if (await confirm({ title: tx('Örnek veriler eklensin mi?'), note: tx('4 departman ve 16 örnek çalışan eklenir. İstediğiniz zaman tek tıkla kaldırabilirsiniz.'), action: tx('Ekle'), destructive: false }))
              fill.mutate(undefined)
          }}><Sparkles className="size-4" />{fill.isPending ? tx('Ekleniyor…') : tx('Örnek verilerle doldur')}</Button>
        ) : (
          <p className="text-[13px] text-muted-foreground">{tx('Örnek veri yalnızca henüz az çalışanı olan (en fazla 5) şirketlere eklenebilir; bu şirkette {0} çalışan var.', [s.realEmployees])}</p>
        )}
      </PanelBody>
    </Panel>
  )
}
