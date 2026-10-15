import { useQuery } from '@tanstack/react-query'
import { ShieldAlert, UserCog } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { identitySecurityApi, platformAccessApi, type LoginAlert } from '@/api/platformAccess'
import { formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * Güvenlik dalgası 2A — şirket yöneticisi: platform yöneticisinin şirkete açtığı süreli
 * erişim izinleri (etkin + son 90 gün). Etkin izin buradan erken kapatılabilir.
 */
export function TenantPlatformAccessPanel() {
  const q = useQuery({ queryKey: ['security', 'platform-access'], queryFn: ({ signal }) => platformAccessApi.forMyTenant(signal) })
  const confirm = useConfirm()
  const revoke = useAction((id: string) => platformAccessApi.revoke(id), { success: tx('Erişim izni kapatıldı'), invalidate: [['security', 'platform-access']] })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><UserCog className="size-4 text-primary" />{' '}{tx('Platform yöneticisi erişimi')}</span>}
        note={tx('Platform yöneticisi şirket verisine yalnızca gerekçeli ve en fazla 4 saatlik izinle erişebilir; her erişim denetim kaydına yazılır.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p> : !q.data?.length ? (
          <p className="text-[13px] text-muted-foreground">{tx('Son 90 günde platform yöneticisi erişim izni açılmadı.')}</p>
        ) : (
          <ul className="max-h-80 divide-y divide-border overflow-y-auto rounded-xl border border-border text-[12.5px]">
            {q.data.map((g) => (
              <li key={g.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{g.grantedToName ?? tx('Platform yöneticisi')}</span>{' · '}{g.reason}
                  <span className="block text-muted-foreground">
                    {tx('{0} – {1}', [formatDateTime(g.createdAt), formatDateTime(g.revokedAt ?? g.expiresAt)])}
                    {g.revokedAt ? ` · ${tx('Kapatan: {0}', [g.revokedByName ?? '—'])}` : ''}
                  </span>
                </span>
                <StatusBadge tone={g.active ? 'warning' : 'neutral'}>{g.active ? tx('Etkin') : g.revokedAt ? tx('Kapatıldı') : tx('Süresi doldu')}</StatusBadge>
                {g.active && (
                  <Button size="sm" variant="outline" disabled={revoke.isPending} onClick={async () => {
                    if (await confirm({ title: tx('Erişim izni kapatılsın mı?'), note: tx('Platform yöneticisinin şirket verisine erişimi en geç 15 saniye içinde sona erer.'), action: tx('İzni kapat') })) revoke.mutate(g.id)
                  }}>{tx('İzni kapat')}</Button>
                )}
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

const ALERT_LABEL: Record<LoginAlert['kind'], string> = {
  new_network: tx('Yeni ağdan giriş'),
  failed_user: tx('Art arda hatalı giriş (kullanıcı)'),
  failed_network: tx('Art arda hatalı giriş (aynı ağ)'),
}

/** Güvenlik dalgası 2A — şirket yöneticisi: şüpheli giriş uyarıları (son 90 gün). */
export function LoginAlertsPanel() {
  const q = useQuery({ queryKey: ['security', 'login-alerts'], queryFn: ({ signal }) => identitySecurityApi.loginAlerts(signal) })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><ShieldAlert className="size-4 text-primary" />{' '}{tx('Şüpheli giriş uyarıları')}</span>}
        note={tx('Yeni bir ağdan yapılan girişler ve 10 dakikada 5 ya da daha fazla hatalı giriş denemesi. IP adresi saklanmaz.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p> : !q.data?.length ? (
          <p className="text-[13px] text-muted-foreground">{tx('Son 90 günde şüpheli giriş uyarısı yok.')}</p>
        ) : (
          <ul className="max-h-72 divide-y divide-border overflow-y-auto rounded-xl border border-border text-[12.5px]">
            {q.data.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                <span className="min-w-0 flex-1">
                  {ALERT_LABEL[a.kind] ?? a.kind}{a.username ? ` · ${a.username}` : ''}
                  <span className="block text-muted-foreground">{formatDateTime(a.createdAt)}</span>
                </span>
                {a.kind !== 'new_network' && <StatusBadge tone="danger">{tx('{0} deneme', [a.count])}</StatusBadge>}
              </li>
            ))}
          </ul>
        )}
        <InfoNote>{tx('Kullanıcılar yeni ağdan girişte, İK ve şirket yöneticileri art arda hatalı girişte bildirim alır.')}</InfoNote>
      </PanelBody>
    </Panel>
  )
}
