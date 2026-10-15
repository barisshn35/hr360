import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { KeyRound, LogIn, LogOut } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField } from '@/components/ui/Field'
import { InfoNote } from '@/components/ui/States'
import { platformAccessApi } from '@/api/platformAccess'
import type { Tenant } from '@/api/tenant'
import { readGrantTenant, writeGrantTenant } from '@/auth/platformAccess'
import { formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/** Seçili kiracıyı değiştirip panele döner (istekler X-HR360-Tenant başlığıyla gider). */
function useTenantView() {
  return {
    enter: (slug: string) => {
      writeGrantTenant(slug)
      window.location.assign('/panel')
    },
    leave: () => {
      writeGrantTenant(null)
      window.location.assign('/panel/platform/kiracilar')
    },
  }
}

/**
 * Güvenlik dalgası 2A — platform yöneticisi: kiracıya süreli erişim izni açar. Gerekçe
 * zorunlu, süre en fazla 4 saat; şirket yöneticileri bildirim alır.
 */
export function GrantAccessModal({ tenant, onClose }: { tenant: Tenant | null; onClose: () => void }) {
  const [reason, setReason] = useState('')
  const [hours, setHours] = useState('1')
  const view = useTenantView()
  const open = useAction(() => platformAccessApi.open({ tenantSlug: tenant!.slug, reason: reason.trim(), hours: Number(hours) }), {
    success: tx('Erişim izni açıldı'),
    invalidate: [['platform-access', 'mine']],
    onDone: (g) => view.enter(g.tenantSlug),
  })
  if (!tenant) return null
  const valid = reason.trim().length >= 10
  return (
    <Modal open onClose={onClose} title={tx('Süreli erişim izni aç')} note={`${tenant.name} (${tenant.slug})`}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!valid || open.isPending} onClick={() => open.mutate(undefined)}><KeyRound className="size-4" />{' '}{tx('İzni aç ve şirkete geç')}</Button></>}>
      <div className="space-y-4">
        <InfoNote>{tx('Şirket verisine erişim yalnızca bu izin süresince açıktır. Şirket yöneticileri bildirim alır, izni görür ve kapatabilir; her erişim şirketin denetim kaydına yazılır.')}</InfoNote>
        <TextAreaField label={tx('Gerekçe')} rows={3} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500}
          hint={tx('En az 10 karakter; ör. destek talebi numarası ve yapılacak iş.')} />
        <SelectField label={tx('Süre')} value={hours} onChange={setHours}
          options={[1, 2, 3, 4].map((h) => ({ value: String(h), label: tx('{0} saat', [h]) }))} />
      </div>
    </Modal>
  )
}

/** Platform yöneticisi: etkin izinlerim; şirkete geç / izni kapat / şirket görünümünden çık. */
export function MyGrantsPanel() {
  const q = useQuery({ queryKey: ['platform-access', 'mine'], queryFn: ({ signal }) => platformAccessApi.mine(signal) })
  const revoke = useAction((id: string) => platformAccessApi.revoke(id), { success: tx('Erişim izni kapatıldı'), invalidate: [['platform-access', 'mine']] })
  const view = useTenantView()
  const current = readGrantTenant()
  if (!q.data?.length && !current) return null
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><KeyRound className="size-4 text-primary" />{' '}{tx('Etkin erişim izinlerim')}</span>}
        note={tx('Kiracı verisi yalnızca etkin izinle açılır. İş bitince izni kapatın.')}
        action={current ? <Button size="sm" variant="outline" onClick={view.leave}><LogOut className="size-4" />{' '}{tx('Şirket görünümünden çık ({0})', [current])}</Button> : undefined} />
      <PanelBody className="p-0">
        <ul className="divide-y divide-border">
          {(q.data ?? []).map((g) => (
            <li key={g.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
              <span className="min-w-0 flex-1"><span className="font-mono">{g.tenantSlug}</span>{' · '}{g.reason}
                <span className="block text-[12px] text-muted-foreground">{tx('Bitiş: {0}', [formatDateTime(g.expiresAt)])}</span></span>
              <Button size="sm" variant="outline" onClick={() => view.enter(g.tenantSlug)}><LogIn className="size-4" />{' '}{tx('Şirkete geç')}</Button>
              <Button size="sm" variant="ghost" disabled={revoke.isPending} onClick={() => revoke.mutate(g.id)}>{tx('İzni kapat')}</Button>
            </li>
          ))}
        </ul>
      </PanelBody>
    </Panel>
  )
}
