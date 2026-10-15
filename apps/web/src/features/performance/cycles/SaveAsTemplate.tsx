/** G12: dönemin yapılandırmasını (bölümler/sorular, ağırlıklar, ölçek) şablon olarak kaydeder. */
import { useState } from 'react'
import { Copy } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { TextField } from '@/components/ui/Field'
import { performanceExtrasApi } from '@/api/performanceExtras'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

export function SaveAsTemplateButton({ cycleId, cycleName }: { cycleId: string; cycleName: string }) {
  const [open, setOpen] = useState(false)
  const [name, setName] = useState(tx('{0} şablonu', [cycleName]))
  const save = useAction(() => performanceExtrasApi.saveAsTemplate(cycleId, { name: name.trim() }), {
    success: (t) => tx('«{0}» şablonu kaydedildi', [t.name]),
    invalidate: [['perf', 'cycle-templates']],
    onDone: () => setOpen(false),
  })
  return (
    <>
      <Button size="sm" variant="ghost" onClick={() => setOpen(true)}><Copy aria-hidden />{tx('Şablon olarak kaydet')}</Button>
      {open && (
        <Modal open onClose={() => setOpen(false)} title={tx('Şablon olarak kaydet')}
          note={tx('Dönemin süresi, türü ve değerlendirme yapılandırması (bölümler, sorular, ağırlıklar, ölçek) saklanır. Yapılandırma yoksa etkin metrikler ve puanlama ağırlıkları kullanılır.')}
          footer={<><Button variant="outline" onClick={() => setOpen(false)}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || name.trim().length < 2}>{tx('Kaydet')}</Button></>}>
          <TextField label={tx('Şablon adı')} value={name} maxLength={150} onChange={(e) => setName(e.target.value)} />
        </Modal>
      )}
    </>
  )
}
