import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Fingerprint, Globe, KeyRound, Lock, Plus, ShieldCheck, Trash2 } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { StatusBadge } from '@/components/ui/status-badge'
import { InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useConfirm } from '@/components/ui/confirm'
import { useToast } from '@/components/ui/toast'
import { securityApi, type SsoStatus } from '@/api/governance'
import { Metric, TenantGate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { IpAllowlistPanel, TenantSessionsPanel } from './AccessControlPanels'
import { IdentityProvisioningPanels } from './IdentityProvisioningPanels'
import { LoginAlertsPanel, TenantPlatformAccessPanel } from './IdentitySecurityPanels'
import { identitySecurityApi, type MfaPolicy } from '@/api/platformAccess'
import { SelectField, TextField } from '@/components/ui/field'

function SsoModal({ provider, s, onClose }: { provider: SsoStatus['supported'][number]; s: SsoStatus; onClose: () => void }) {
  const toast = useToast()
  const [f, setF] = useState({ clientId: '', clientSecret: '', directoryId: '', domain: s.domain ?? '' })
  const add = useAction(() => securityApi.addSso({ provider: provider.id, ...f, directoryId: f.directoryId || undefined, domain: f.domain || undefined }), { success: tx('{0} bağlandı', [provider.label]), invalidate: [['security']], onDone: onClose })
  return (
    <Modal open onClose={onClose} size="lg" title={tx('{0} ile giriş', [provider.label])} note={tx('Sağlayıcı tarafında bir OAuth/OIDC uygulaması oluşturup aşağıdaki yönlendirme adresini kaydedin.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!f.clientId || !f.clientSecret || (provider.id === 'microsoft' && !f.directoryId) || add.isPending} onClick={() => add.mutate(undefined)}>{tx('Bağla')}</Button></>}>
      <div className="space-y-4">
        <div className="rounded-xl bg-muted/50 p-3 text-[12.5px]">
          <p className="mb-1 font-medium">{tx('Yönlendirme (redirect) URI')}</p>
          <button onClick={() => navigator.clipboard.writeText(provider.redirectUri).then(() => toast.ok(tx('Kopyalandı')))} className="cursor-pointer break-all font-mono text-[11.5px] text-primary hover:underline">{provider.redirectUri}</button>
          <ol className="mt-2 list-decimal space-y-0.5 pl-4 text-muted-foreground">
            {provider.id === 'google' ? (
              <><li>{tx('Google Cloud Console → API\'ler ve Hizmetler → Kimlik bilgileri → OAuth istemci kimliği (Web uygulaması).')}</li><li>{tx('“Yetkili yönlendirme URI\'leri”ne yukarıdaki adresi ekleyin.')}</li></>
            ) : (
              <><li>{tx('Entra ID → Uygulama kayıtları → Yeni kayıt; yönlendirme URI türü “Web”.')}</li><li>{tx('Sertifikalar ve gizli diziler → yeni istemci gizli anahtarı; “Dizin (kiracı) kimliği”ni de kopyalayın.')}</li></>
            )}
          </ol>
        </div>
        <TextField label={tx('İstemci kimliği (Client ID)')} value={f.clientId} onChange={(e) => setF({ ...f, clientId: e.target.value })} />
        <TextField label={tx('İstemci gizli anahtarı')} type="password" value={f.clientSecret} onChange={(e) => setF({ ...f, clientSecret: e.target.value })} />
        {provider.id === 'microsoft' && <TextField label={tx('Dizin (kiracı) kimliği')} value={f.directoryId} onChange={(e) => setF({ ...f, directoryId: e.target.value })} placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" />}
        <TextField label={tx('Şirket e-posta alan adı')} value={f.domain} onChange={(e) => setF({ ...f, domain: e.target.value })} hint={tx('Bu alan adındaki kullanıcılar giriş ekranında doğrudan sağlayıcıya yönlenir.')} placeholder="sirketiniz.com" />
      </div>
    </Modal>
  )
}

export function SecurityPage() {
  const sso = useQuery({ queryKey: ['security', 'sso'], queryFn: ({ signal }) => securityApi.sso(signal) })
  const mfa = useQuery({ queryKey: ['security', 'mfa'], queryFn: ({ signal }) => securityApi.mfa(signal) })
  const [adding, setAdding] = useState<SsoStatus['supported'][number] | null>(null)
  const remove = useAction((alias: string) => securityApi.removeSso(alias), { success: tx('Sağlayıcı kaldırıldı'), invalidate: [['security']] })
  const enforce = useAction(() => securityApi.enforceMfa(), { success: (r) => tx('{0} kullanıcıya bir sonraki girişte doğrulayıcı kurulumu zorunlu kılındı', [r.required]), invalidate: [['security']] })
  const setPolicy = useAction((p: MfaPolicy) => identitySecurityApi.setMfaPolicy(p), {
    success: (r) => r.policy === 'off' ? tx('İki adımlı doğrulama zorunluluğu kaldırıldı') : tx('Politika kaydedildi; {0} kullanıcıya kurulum zorunluluğu eklendi', [r.required]),
    invalidate: [['security']],
  })
  const confirm = useConfirm()
  const m = mfa.data
  const pct = m && m.members ? Math.round((100 * m.withOtp) / m.members) : 0
  return (
    <TenantGate>
      <PageHeader title={tx('Güvenlik')} description={tx('Kurumsal tek oturum açma (Google Workspace, Microsoft Entra ID) ve iki adımlı doğrulama zorunluluğu.')} />
      <div className="grid gap-6 xl:grid-cols-2">
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><Globe className="size-4 text-primary" />{' '}{tx('Tek oturum açma (SSO)')}</span>} note={tx('Keycloak kimlik aracılığı (identity brokering) ve şirket organizasyonu ile.')} />
          <PanelBody className="space-y-4">
            {sso.isPending ? <RowsSkeleton rows={2} /> : sso.isError ? <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p> : (
              <>
                {sso.data.providers.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Bağlı sağlayıcı yok; kullanıcılar parola ile giriş yapıyor.')}</p> : sso.data.providers.map((p) => (
                  <motion.div key={p.alias} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="flex items-center gap-3 rounded-xl border border-border p-3">
                    <ShieldCheck className="size-5 text-[hsl(var(--success))]" />
                    <div className="min-w-0 flex-1"><p className="text-[13.5px] font-medium">{p.displayName ?? p.alias}</p><p className="truncate font-mono text-[11px] text-muted-foreground">{p.redirectUri}</p></div>
                    <StatusBadge tone={p.enabled ? 'success' : 'neutral'}>{p.enabled ? tx('Etkin') : tx('Kapalı')}</StatusBadge>
                    <Button size="icon" variant="ghost" aria-label={tx('Kaldır')} onClick={async () => {
                      if (await confirm({ title: tx('Sağlayıcı kaldırılsın mı?'), note: tx('{0} ile giriş kapanır; bu yolla giren kullanıcılar parola belirlemeden giriş yapamaz.', [p.displayName ?? p.alias]), action: tx('Kaldır') })) remove.mutate(p.alias)
                    }}><Trash2 className="size-4" /></Button>
                  </motion.div>
                ))}
                <div className="flex flex-wrap gap-2">
                  {sso.data.supported.filter((s) => !sso.data.providers.some((p) => p.alias.endsWith(`-${s.id}`))).map((s) => (
                    <Button key={s.id} variant="outline" onClick={() => setAdding(s)}><Plus className="size-4" /> {s.label}</Button>
                  ))}
                </div>
                <InfoNote>{tx('Sağlayıcı bağlandığında Keycloak giriş ekranında “Google/Microsoft ile giriş” düğmesi görünür; e-posta alan adı eşleşen kullanıcılar doğrudan yönlendirilir. Mevcut hesaplar e-postayla eşleştirilir.', [])}</InfoNote>
              </>
            )}
          </PanelBody>
        </Panel>
        <Panel>
          <PanelHead title={<span className="flex items-center gap-2"><Fingerprint className="size-4 text-primary" />{' '}{tx('İki adımlı doğrulama (TOTP)')}</span>} note={tx('Authenticator uygulamasıyla 6 haneli kod.')} />
          <PanelBody className="space-y-4">
            {mfa.isPending ? <RowsSkeleton rows={2} /> : mfa.isError ? <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p> : m && (
              <>
                <div className="grid grid-cols-3 gap-3">
                  <Metric label={tx('Etkin')} value={m.withOtp} tone="good" />
                  <Metric label={tx('Kurulum bekliyor')} value={m.pendingSetup} tone={m.pendingSetup ? 'warn' : undefined} />
                  <Metric label={tx('Kapalı')} value={m.without} tone={m.without ? 'bad' : 'good'} />
                </div>
                <div className="h-2.5 overflow-hidden rounded-full bg-muted"><motion.div initial={{ width: 0 }} animate={{ width: `${pct}%` }} className="h-full rounded-full bg-[hsl(var(--success))]" /></div>
                <p className="text-[12.5px] text-muted-foreground">{tx('İki adımlı doğrulama (doğrulayıcı uygulama ya da passkey) açık hesaplar: %{0}', [pct])}</p>
                <SelectField label={tx('Zorunluluk')} value={m.policy ?? 'off'} disabled={setPolicy.isPending}
                  hint={tx('Yeni şirketlerde herkes için zorunlu gelir. Kapsamdaki kullanıcı doğrulayıcı uygulaması ya da passkey kurmadan giriş yapamaz; kural düzenli olarak yeniden uygulanır (sonradan role atananlar dahil).')}
                  onChange={async (v) => {
                    const p = v as MfaPolicy
                    const ok = p === 'off'
                      ? await confirm({ title: tx('İki adımlı doğrulama zorunluluğu kaldırılsın mı?'), note: tx('Hesaplar yalnızca parolayla korunur. Henüz doğrulayıcı kurmamış kullanıcılardan kurulum istenmez; kurulmuş doğrulayıcılar çalışmaya devam eder.'), action: tx('Zorunluluğu kaldır') })
                      : await confirm({ title: tx('İki adımlı doğrulama zorunlu kılınsın mı?'), note: p === 'all' ? tx('Tüm şirket kullanıcıları için zorunlu olur.') : tx('İK, şirket yöneticisi ve yönetici rollerindeki kullanıcılar için zorunlu olur.'), action: tx('Zorunlu kıl') })
                    if (ok) setPolicy.mutate(p)
                  }}
                  options={[
                    { value: 'off', label: tx('Zorunlu değil') },
                    { value: 'privileged', label: tx('İK, yönetici ve platform rollerinde zorunlu') },
                    { value: 'all', label: tx('Herkes için zorunlu') },
                  ]} />
                {m.policy && m.policy !== 'off' && (m.requiredWithout ?? 0) > 0 && (
                  <InfoNote>{tx('Zorunluluk kapsamında {0} kullanıcının henüz ikinci adımı yok; bir sonraki girişlerinde kurulum istenecek.', [m.requiredWithout ?? 0])}</InfoNote>
                )}
                <Button disabled={m.without === 0 || enforce.isPending} onClick={async () => {
                  if (await confirm({ title: tx('İki adımlı doğrulama zorunlu kılınsın mı?'), note: tx('{0} kullanıcı bir sonraki girişinde doğrulayıcı uygulaması kurmadan devam edemez.', [m.without]), action: tx('Zorunlu kıl') })) enforce.mutate(undefined)
                }}><Lock className="size-4" />{' '}{tx('Şimdi herkese kurulum iste (tek sefer)')}</Button>
                <ul className="max-h-60 divide-y divide-border overflow-y-auto rounded-xl border border-border text-[12.5px]">
                  {m.users.map((u) => <li key={u.userId} className="flex items-center justify-between px-3 py-2"><span className="flex items-center gap-2"><KeyRound className="size-3.5 text-muted-foreground" /> {u.username}</span><span className="flex items-center gap-1.5">{u.required && <StatusBadge tone="info">{tx('Zorunlu')}</StatusBadge>}<StatusBadge tone={u.hasOtp || u.hasPasskey ? 'success' : u.pendingSetup ? 'warning' : 'neutral'}>{u.hasOtp ? tx('Açık') : u.hasPasskey ? tx('Passkey') : u.pendingSetup ? tx('Bekliyor') : tx('Kapalı')}</StatusBadge></span></li>)}
                </ul>
              </>
            )}
          </PanelBody>
        </Panel>
      </div>
      <div className="mt-6 grid gap-6 xl:grid-cols-2"><IpAllowlistPanel /><TenantSessionsPanel /></div>
      <div className="mt-6 grid gap-6 xl:grid-cols-2"><LoginAlertsPanel /><TenantPlatformAccessPanel /></div>
      <IdentityProvisioningPanels />
      {adding && sso.data && <SsoModal provider={adding} s={sso.data} onClose={() => setAdding(null)} />}
    </TenantGate>
  )
}
