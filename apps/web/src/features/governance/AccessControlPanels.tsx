import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { KeyRound, LogOut, MonitorSmartphone, Network } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useConfirm } from '@/components/ui/Confirm'
import { accountSecurityApi } from '@/api/kvkkOps'
import { keycloak, readPreferredTenant, scopeForTenant } from '@/auth/keycloak'
import { formatDateTime } from '@/lib/format'
import { Metric, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/** Şirket yöneticisi: IP izin listesi (G22). */
export function IpAllowlistPanel() {
  const q = useQuery({ queryKey: ['security', 'ip'], queryFn: ({ signal }) => accountSecurityApi.ipAllowlist(signal) })
  const [text, setText] = useState<string | null>(null)
  const value = text ?? (q.data?.entries ?? []).join('\n')
  const save = useAction(() => accountSecurityApi.setIpAllowlist(value.split(/[\n,;]+/).map((s) => s.trim()).filter(Boolean)),
    { success: tx('IP kısıtı kaydedildi'), invalidate: [['security', 'ip']], onDone: () => setText(null) })
  const confirm = useConfirm()
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Network className="size-4 text-primary" />{' '}{tx('IP kısıtı')}</span>}
        note={tx('Boş bırakılırsa her yerden erişilebilir. Bir satıra bir adres ya da aralık (ör. 203.0.113.0/24). Değişiklik 30 sn içinde geçerli olur.')} />
      <PanelBody className="space-y-3">
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p> : (
          <>
            <TextAreaField label={tx('İzin verilen adresler')} rows={4} value={value} onChange={(e) => setText(e.target.value)} placeholder="203.0.113.0/24" />
            <p className="text-[12px] text-muted-foreground">{tx('Şu anki adresiniz: {0}', [q.data?.yourIp ?? '—'])}</p>
            <InfoNote>{tx('Sunucu bir yük dengeleyicinin arkasındaysa gerçek istemci adresinin geçirildiğinden emin olun; aksi hâlde herkes dengeleyicinin adresiyle görünür.')}</InfoNote>
            <Button disabled={save.isPending || text === null} onClick={async () => {
              if (await confirm({ title: tx('IP izin listesi kaydedilsin mi?'), note: tx('Listede olmayan adreslerden şirkete giriş engellenir. Kendi adresiniz listede değilse oturumunuz kapanınca giriş yapamazsınız.'), action: tx('Kaydet') })) save.mutate(undefined)
            }}>{tx('Kaydet')}</Button>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

/** Şirket yöneticisi: açık oturumlar ve passkey kullanımı (G22). */
export function TenantSessionsPanel() {
  const q = useQuery({ queryKey: ['security', 'sessions'], queryFn: ({ signal }) => accountSecurityApi.tenantSessions(signal) })
  const pk = useQuery({ queryKey: ['security', 'passkeys'], queryFn: ({ signal }) => accountSecurityApi.passkeyStats(signal) })
  const confirm = useConfirm()
  const out = useAction((id: string) => accountSecurityApi.logoutUser(id), { success: tx('Kullanıcının tüm oturumları kapatıldı'), invalidate: [['security', 'sessions']] })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><MonitorSmartphone className="size-4 text-primary" />{' '}{tx('Açık oturumlar')}</span>}
        note={tx('Kayıp cihaz ya da ayrılan çalışan için kullanıcının tüm oturumlarını kapatın.')} />
      <PanelBody className="space-y-4">
        {pk.data && <div className="grid grid-cols-2 gap-3"><Metric label={tx('Passkey kayıtlı kullanıcı')} value={pk.data.withPasskey} /><Metric label={tx('Toplam kullanıcı')} value={pk.data.members} /></div>}
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p> : !q.data?.length ? <p className="text-[13px] text-muted-foreground">{tx('Açık oturum yok.')}</p> : (
          <ul className="max-h-80 divide-y divide-border overflow-y-auto rounded-xl border border-border text-[12.5px]">
            {q.data.map((u) => (
              <li key={u.userId} className="flex flex-wrap items-center gap-3 px-3 py-2">
                <span className="min-w-0 flex-1">{u.username ?? u.userId}<span className="block text-muted-foreground">{u.sessions.map((s) => `${s.ipAddress ?? '?'} · ${formatDateTime(s.lastAccess)}`).join(' | ')}</span></span>
                <StatusBadge tone="neutral">{tx('{0} oturum', [u.sessions.length])}</StatusBadge>
                <Button size="sm" variant="outline" onClick={async () => {
                  if (await confirm({ title: tx('Oturumlar kapatılsın mı?'), note: tx('{0} kullanıcısının {1} açık oturumu hemen sonlandırılır.', [u.username ?? u.userId, u.sessions.length]), action: tx('Oturumları kapat') })) out.mutate(u.userId)
                }}><LogOut className="size-4" />{' '}{tx('Oturumları kapat')}</Button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

/** Çalışan: kendi oturumları ve passkey kaydı (Profilim → Güvenlik). */
export function MySecurityPanel() {
  const q = useQuery({ queryKey: ['me', 'sessions'], queryFn: ({ signal }) => accountSecurityApi.mySessions(signal) })
  const end = useAction((id: string) => accountSecurityApi.endSession(id), { success: tx('Oturum kapatıldı'), invalidate: [['me', 'sessions']] })
  const others = useAction(() => accountSecurityApi.endOthers(), { success: (r) => tx('{0} oturum kapatıldı', [r.closed]), invalidate: [['me', 'sessions']] })
  return (
    <div className="space-y-5">
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><KeyRound className="size-4 text-primary" />{' '}{tx('Passkey / güvenlik anahtarı')}</span>}
          note={tx('Parmak izi, yüz tanıma ya da cihaz PIN\'iyle çalışan passkey, girişte ikinci adım olarak kullanılır. Biyometrik veri cihazınızdan çıkmaz; HR360 yalnızca açık anahtarı saklar.')} />
        <PanelBody className="flex flex-wrap items-center gap-3">
          {q.data && <StatusBadge tone={q.data.hasPasskey ? 'success' : 'neutral'}>{q.data.hasPasskey ? tx('Kayıtlı') : tx('Kayıtlı değil')}</StatusBadge>}
          {q.data && <StatusBadge tone={q.data.hasOtp ? 'success' : 'neutral'}>{q.data.hasOtp ? tx('Doğrulayıcı uygulama açık') : tx('Doğrulayıcı uygulama kapalı')}</StatusBadge>}
          <Button variant="outline" onClick={() => void keycloak.login({ action: 'webauthn-register', redirectUri: window.location.href, scope: scopeForTenant(readPreferredTenant()) })}>{q.data?.hasPasskey ? tx('Yeni passkey ekle') : tx('Passkey ekle')}</Button>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Oturumlarım')} note={tx('HR360\'a giriş yapılmış cihaz ve tarayıcılar.')}
          action={<Button size="sm" variant="outline" onClick={() => others.mutate(undefined)} disabled={others.isPending}>{tx('Diğer oturumları kapat')}</Button>} />
        <PanelBody className="p-0">
          {q.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : (
            <ul className="divide-y divide-border">
              {q.data?.sessions.map((s) => (
                <li key={s.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                  <span className="min-w-0 flex-1">{s.ipAddress ?? '—'}<span className="block text-[12px] text-muted-foreground">{tx('Başlangıç {0} · son etkinlik {1}', [formatDateTime(s.start), formatDateTime(s.lastAccess)])}</span></span>
                  {s.current ? <StatusBadge tone="success">{tx('Bu oturum')}</StatusBadge> : <Button size="sm" variant="ghost" onClick={() => end.mutate(s.id)}>{tx('Kapat')}</Button>}
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}
