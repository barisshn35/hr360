import { useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { tx } from '@/lib/i18n'
import { moveWidget, resolveLayout, type DashboardPref, type WidgetId } from './dashboardLayout'

const LABELS = (): Record<WidgetId, string> => ({
  welcome: tx('Karşılama ve hızlı eylemler'),
  team: tx('Ekibim bugün'),
  todos: tx('Yapılacaklarım'),
  eom: tx('Ayın çalışanı'),
  queue: tx('Sıradaki talepler'),
  pending: tx('Bekleyen onay'),
  overdue: tx('Süresi geçen'),
  kpis: tx('Gösterge kartları'),
  chart: tx('Açılan talepler grafiği'),
  organization: tx('Organizasyon'),
  modules: tx('Modülleriniz'),
  pinned: tx('Sabitlenmiş raporlar'),
})

/**
 * Dalga 12 (madde 89): ana panel kartlarını göster/gizle ve sırala. Sürükle-bırak yerine yukarı/aşağı
 * düğmeleri: klavye ve ekran okuyucuyla da kullanılır. Yalnızca yetkiyle görülebilen kartlar listelenir.
 */
export function DashboardCustomizer({
  open,
  onClose,
  available,
  layout,
  defaults,
  onSave,
  ready = true,
}: {
  open: boolean
  onClose: () => void
  available: WidgetId[]
  layout: { order: WidgetId[]; hidden: Set<WidgetId> }
  defaults: DashboardPref
  onSave: (next: DashboardPref | null) => void
  /** Kayıtlı tercihler yüklendi mi? Yüklenmeden açılırsa (ör. /panel?duzenle=1) varsayılanla başlayıp
   *  Kaydet'le kullanıcının düzenini ezmemek için liste yüklenene dek beklenir. */
  ready?: boolean
}) {
  const [order, setOrder] = useState<WidgetId[]>(layout.order)
  const [hidden, setHidden] = useState<Set<WidgetId>>(layout.hidden)
  const [initialized, setInitialized] = useState(false)
  // Pencere her açılışta kayıtlı düzenden başlar — tercihler yüklendikten sonra, bir kez.
  useEffect(() => {
    if (!open) { setInitialized(false); return }
    if (!ready || initialized) return
    setOrder(layout.order)
    setHidden(new Set(layout.hidden))
    setInitialized(true)
  }, [open, ready, initialized, layout])

  const labels = LABELS()
  const toggle = (id: WidgetId) => setHidden((prev) => {
    const next = new Set(prev)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    return next
  })
  const reset = () => {
    const d = resolveLayout(available, null, defaults)
    setOrder(d.order)
    setHidden(d.hidden)
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={tx('Paneli düzenle')}
      note={tx('Kartları gösterin, gizleyin ve sıralayın. Düzeniniz hesabınıza kaydedilir; diğer cihazlarda da aynı görünür.')}
      footer={
        <>
          <Button variant="ghost" className="mr-auto" onClick={reset} disabled={!initialized}>
            <RotateCcw className="size-4" />
            {tx('Varsayılana dön')}
          </Button>
          <Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button>
          <Button disabled={!initialized} onClick={() => {
            onSave({ order, hidden: [...hidden] })
            onClose()
          }}>{tx('Kaydet')}</Button>
        </>
      }
    >
      {!initialized ? (
        <p role="status" className="py-6 text-center text-[13px] text-muted-foreground">{tx('Kayıtlı düzeniniz yükleniyor…')}</p>
      ) : (
      <ol className="space-y-1.5">
        {order.map((id, i) => (
          <li key={id} className="flex items-center gap-3 rounded-xl border border-border bg-card/50 px-3 py-2">
            <Checkbox id={`w-${id}`} checked={!hidden.has(id)} onCheckedChange={() => toggle(id)} />
            <label htmlFor={`w-${id}`} className="flex-1 cursor-pointer text-[13.5px]">{labels[id]}</label>
            <Button size="icon" variant="ghost" className="size-8" disabled={i === 0}
              aria-label={tx('{0} yukarı taşı', [labels[id]])} onClick={() => setOrder((o) => moveWidget(o, id, -1))}>
              <ArrowUp className="size-4" />
            </Button>
            <Button size="icon" variant="ghost" className="size-8" disabled={i === order.length - 1}
              aria-label={tx('{0} aşağı taşı', [labels[id]])} onClick={() => setOrder((o) => moveWidget(o, id, 1))}>
              <ArrowDown className="size-4" />
            </Button>
          </li>
        ))}
      </ol>
      )}
    </Modal>
  )
}
