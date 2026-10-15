import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Copy, FolderSync, Globe2, KeyRound, Plug, Plus, RefreshCw, ShieldAlert, ShieldCheck, Trash2, Users } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import {
  identityDirectoryApi, type CreatedScimToken, type DirectorySettings, type LdapSettingsInput, type SyncResult,
} from '@/api/identityDirectory'
import { formatDateTime } from '@/lib/format'
import { Metric, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/** Radix Select boş değerli seçeneğe izin vermez; "kullanma" bu işaretle temsil edilir. */
const NONE = '__none__'
const fromSel = (v: string) => (v === NONE ? '' : v)

const adminOnly = <p className="text-[13px] text-muted-foreground">{tx('Bu ayar yalnızca şirket yöneticisine açıktır.')}</p>

function MinimisationNote({ s }: { s?: DirectorySettings }) {
  return (
    <InfoNote>
      {tx('KVKK veri minimizasyonu: yalnızca kullanıcı adı, ad, soyad, birincil e-posta, etkinlik durumu, unvan ve departman saklanır. Telefon, adres, fotoğraf, yönetici gibi diğer nitelikler yok sayılır ve saklanmaz.')}
      {s && <span className="mt-1 block font-mono text-[11px] text-muted-foreground">{s.storedAttributes.join(', ')}</span>}
    </InfoNote>
  )
}

/* ------------------------------------------------------------------ SCIM 2.0 */

function ScimPanel() {
  const toast = useToast()
  const settings = useQuery({ queryKey: ['identity-dir', 'settings'], queryFn: ({ signal }) => identityDirectoryApi.settings(signal), retry: false })
  const tokens = useQuery({ queryKey: ['identity-dir', 'tokens'], queryFn: ({ signal }) => identityDirectoryApi.tokens(signal), retry: false })
  const [name, setName] = useState('')
  const [created, setCreated] = useState<CreatedScimToken | null>(null)
  const create = useAction(() => identityDirectoryApi.createToken(name.trim()), {
    invalidate: [['identity-dir', 'tokens']],
    onDone: (r) => { setCreated(r); setName('') },
  })
  const revoke = useAction((id: string) => identityDirectoryApi.revokeToken(id), { success: tx('Jeton iptal edildi'), invalidate: [['identity-dir', 'tokens']] })
  const confirm = useConfirm()
  const askRevoke = async (t: { id: string; name: string }) => {
    if (await confirm({
      title: tx('“{0}” SCIM jetonu iptal edilsin mi?', [t.name]),
      note: tx('Bu jetonu kullanan kimlik sağlayıcı (ör. Entra ID, Okta) hesap eşitleyemez. İptal geri alınamaz; yeni jeton oluşturup sağlayıcıya girmeniz gerekir.'),
      action: tx('İptal et'),
    })) revoke.mutate(t.id)
  }
  const copy = (text: string) => void navigator.clipboard?.writeText(text).then(() => toast.ok(tx('Panoya kopyalandı')))

  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Users className="size-4 text-primary" />{' '}{tx('SCIM 2.0 ile kullanıcı sağlama')}</span>}
        note={tx('Entra ID, Okta, OneLogin gibi kimlik sağlayıcılar çalışan hesaplarını otomatik açar, günceller ve kapatır.')} />
      <PanelBody className="space-y-4">
        {tokens.isPending ? <RowsSkeleton rows={2} /> : tokens.isError ? adminOnly : (
          <>
            {settings.data && (
              <div className="space-y-1">
                <p className="text-[12px] font-medium text-muted-foreground">{tx('SCIM taban adresi')}</p>
                <div className="flex items-center gap-2 rounded-lg border border-border bg-muted/40 px-3 py-2">
                  <code className="min-w-0 flex-1 truncate font-mono text-[12px]">{settings.data.scimBaseUrl}</code>
                  <Button size="icon" variant="ghost" aria-label={tx('Kopyala')} onClick={() => copy(settings.data.scimBaseUrl)}><Copy className="size-4" /></Button>
                </div>
              </div>
            )}
            <div className="flex flex-wrap items-end gap-2">
              <TextField label={tx('Yeni jeton adı')} placeholder={tx('ör. Entra ID')} value={name} maxLength={100} onChange={(e) => setName(e.target.value)} className="w-56" />
              <Button onClick={() => create.mutate(undefined)} disabled={!name.trim() || create.isPending}><Plus className="size-4" />{' '}{tx('Jeton oluştur')}</Button>
            </div>
            {tokens.data.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Henüz SCIM jetonu yok.')}</p> : (
              <ul className="divide-y divide-border rounded-xl border border-border text-[12.5px]">
                {tokens.data.map((t) => (
                  <li key={t.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                    <KeyRound className="size-3.5 text-muted-foreground" />
                    <span className="min-w-0 flex-1">
                      <span className="font-medium">{t.name}</span>{' '}<span className="font-mono text-muted-foreground">{t.tokenPrefix}…</span>
                      <span className="block text-muted-foreground">{tx('Son kullanım: {0}', [formatDateTime(t.lastUsedAt)])}</span>
                    </span>
                    <StatusBadge tone={t.active ? 'success' : 'neutral'}>{t.active ? tx('Etkin') : tx('İptal edildi')}</StatusBadge>
                    {t.active && <Button size="icon" variant="ghost" aria-label={tx('İptal et')} onClick={() => askRevoke(t)}><Trash2 className="size-4" /></Button>}
                  </li>
                ))}
              </ul>
            )}
            <MinimisationNote s={settings.data} />
            <InfoNote>{tx('Her sağlanan hesap şirket organizasyonuna yalnızca "employee" rolüyle açılır ve çalışan kaydı oluşturulur. active=false ya da silme hesabı kapatır ve tüm oturumları sonlandırır; çalışan kaydı silinmez.')}</InfoNote>
          </>
        )}
      </PanelBody>
      {created && (
        <Modal open onClose={() => setCreated(null)} title={tx('SCIM jetonu oluşturuldu')} note={created.name}
          footer={<Button onClick={() => setCreated(null)}>{tx('Kopyaladım, kapat')}</Button>}>
          <div className="space-y-3">
            <p className="flex gap-2 text-[13px] text-amber-700 dark:text-amber-300"><ShieldAlert className="mt-0.5 size-4 shrink-0" />{tx('Jeton yalnızca şimdi gösteriliyor; sunucuda yalnızca özeti saklanır. Güvenli bir yere kopyalayın.')}</p>
            <div className="flex items-center gap-2 rounded-lg border border-border bg-muted/40 px-3 py-2">
              <code className="min-w-0 flex-1 break-all font-mono text-[12px]">{created.token}</code>
              <Button size="icon" variant="ghost" aria-label={tx('Kopyala')} onClick={() => copy(created.token)}><Copy className="size-4" /></Button>
            </div>
          </div>
        </Modal>
      )}
    </Panel>
  )
}

/* ------------------------------------------------------------------ LDAP / Active Directory */

function SyncResultView({ r }: { r: SyncResult }) {
  return (
    <div className="space-y-3 rounded-xl border border-border p-3">
      <p className="text-[13px] font-medium">{r.dryRun ? tx('Önizleme (hiçbir şey değişmedi)') : tx('Eşitleme tamamlandı')} · {tx('{0} dizin kaydı', [r.entries])}</p>
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <Metric label={r.dryRun ? tx('Oluşturulacak') : tx('Oluşturulan')} value={r.created} tone="good" />
        <Metric label={r.dryRun ? tx('Güncellenecek') : tx('Güncellenen')} value={r.updated + r.enabled} />
        <Metric label={r.dryRun ? tx('Kapatılacak') : tx('Kapatılan')} value={r.disabled} tone={r.disabled ? 'warn' : undefined} />
        <Metric label={tx('Atlanan')} value={r.skipped} tone={r.skipped ? 'warn' : undefined} />
      </div>
      {(r.warning || r.connectionWarning) && <p className="text-[12.5px] text-amber-700 dark:text-amber-300">{[r.warning, r.connectionWarning].filter(Boolean).join(' ')}</p>}
      {r.errors.length > 0 && <ul className="list-disc pl-5 text-[12px] text-destructive">{r.errors.map((e) => <li key={e}>{e}</li>)}</ul>}
      {r.actions.length > 0 && (
        <ul className="max-h-48 divide-y divide-border overflow-y-auto rounded-lg border border-border text-[12px]">
          {r.actions.map((a, i) => (
            <li key={i} className="flex gap-2 px-2.5 py-1.5"><StatusBadge tone={a.kind === 'Create' ? 'success' : a.kind === 'Disable' ? 'warning' : 'neutral'}>{a.kind}</StatusBadge><span className="min-w-0 flex-1 truncate">{a.userName}{a.detail ? ` — ${a.detail}` : ''}</span></li>
          ))}
        </ul>
      )}
    </div>
  )
}

function LdapPanel() {
  const q = useQuery({ queryKey: ['identity-dir', 'settings'], queryFn: ({ signal }) => identityDirectoryApi.settings(signal), retry: false })
  const [form, setForm] = useState<(LdapSettingsInput & { bindPassword: string }) | null>(null)
  const [result, setResult] = useState<SyncResult | null>(null)
  const [force, setForce] = useState(false)
  const l = q.data?.ldap
  const f = form ?? (l ? {
    enabled: l.enabled, autoSync: l.autoSync, url: l.url ?? '', bindDn: l.bindDn ?? '', bindPassword: '', baseDn: l.baseDn ?? '',
    userFilter: l.userFilter ?? '', usernameAttr: l.usernameAttr, emailAttr: l.emailAttr, departmentAttr: l.departmentAttr ?? '',
    titleAttr: l.titleAttr ?? '', disabledAttr: l.disabledAttr ?? '',
  } : null)
  const set = (patch: Partial<LdapSettingsInput & { bindPassword: string }>) => f && setForm({ ...f, ...patch })
  const save = useAction(() => identityDirectoryApi.saveSettings({ ldap: { ...f!, bindPassword: f!.bindPassword ? f!.bindPassword : undefined } }), {
    success: (r) => r.warning ? tx('Kaydedildi. {0}', [r.warning]) : tx('LDAP ayarları kaydedildi'),
    invalidate: [['identity-dir']],
    onDone: () => setForm(null),
  })
  const test = useAction(() => identityDirectoryApi.testLdap(), {
    success: (r) => tx('Bağlantı başarılı: {0} kayıt bulundu', [r.entries]) + (r.warning ? ` — ${r.warning}` : ''),
  })
  const sync = useAction((dryRun: boolean) => identityDirectoryApi.syncLdap(dryRun, force), {
    invalidate: [['identity-dir']],
    onDone: (r) => setResult(r),
  })
  const opts = (list: string[] | undefined, optional: boolean) =>
    [...(optional ? [{ value: NONE, label: tx('(kullanma)') }] : []), ...(list ?? []).map((v) => ({ value: v, label: v }))]
  const insecure = (f?.url ?? '').trim().toLowerCase().startsWith('ldap://')

  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><FolderSync className="size-4 text-primary" />{' '}{tx('LDAP / Active Directory eşitlemesi')}</span>}
        note={tx('Dizindeki kullanıcılar HR360\'a aktarılır; dizinden silinen ya da kapatılan hesaplar HR360\'ta da kapatılır.')} />
      <PanelBody className="space-y-4">
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError || !f ? adminOnly : (
          <>
            <div className="flex flex-wrap gap-4 text-[13px]">
              <label className="flex items-center gap-2"><Checkbox checked={f.enabled} onCheckedChange={(v) => set({ enabled: v === true })} />{' '}{tx('Eşitleme etkin')}</label>
              <label className="flex items-center gap-2"><Checkbox checked={f.autoSync} disabled={!f.enabled} onCheckedChange={(v) => set({ autoSync: v === true })} />{' '}{tx('Otomatik eşitle (her {0} dk)', [Math.round(q.data.syncIntervalMinutes * 10) / 10])}</label>
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <TextField label={tx('Sunucu adresi')} placeholder="ldaps://dc01.sirket.local:636" value={f.url ?? ''} onChange={(e) => set({ url: e.target.value })}
                error={insecure ? tx('ldap:// şifresizdir; parola ve kişisel veriler ağda açık taşınır. ldaps:// kullanın.') : undefined} />
              <TextField label={tx('Base DN')} placeholder="ou=people,dc=sirket,dc=com" value={f.baseDn ?? ''} onChange={(e) => set({ baseDn: e.target.value })} />
              <TextField label={tx('Bağlama DN')} placeholder="cn=hr360-okuyucu,ou=svc,dc=sirket,dc=com" value={f.bindDn ?? ''} onChange={(e) => set({ bindDn: e.target.value })} />
              <TextField label={tx('Bağlama parolası')} type="password" autoComplete="new-password" value={f.bindPassword}
                placeholder={l?.hasPassword ? tx('Kayıtlı (değiştirmek için yazın)') : ''} onChange={(e) => set({ bindPassword: e.target.value })}
                hint={tx('AES-256-GCM ile şifrelenerek saklanır; yalnızca okuma yetkili bir hesap kullanın.')} />
              <TextField label={tx('Kullanıcı filtresi')} placeholder="(objectClass=person)" value={f.userFilter ?? ''} onChange={(e) => set({ userFilter: e.target.value })} />
              <SelectField label={tx('Kullanıcı adı niteliği')} value={f.usernameAttr ?? 'uid'} onChange={(v) => set({ usernameAttr: v })} options={opts(q.data.allowed.username, false)} />
              <SelectField label={tx('E-posta niteliği')} value={f.emailAttr ?? 'mail'} onChange={(v) => set({ emailAttr: v })} options={opts(q.data.allowed.email, false)} />
              <SelectField label={tx('Departman niteliği')} value={f.departmentAttr || NONE} onChange={(v) => set({ departmentAttr: fromSel(v) })} options={opts(q.data.allowed.department, true)} />
              <SelectField label={tx('Unvan niteliği')} value={f.titleAttr || NONE} onChange={(v) => set({ titleAttr: fromSel(v) })} options={opts(q.data.allowed.title, true)} />
              <SelectField label={tx('Pasif hesap niteliği')} value={f.disabledAttr || NONE} onChange={(v) => set({ disabledAttr: fromSel(v) })} options={opts(q.data.allowed.disabled, true)} />
            </div>
            {l?.urlWarning && !form && <p className="flex gap-2 text-[12.5px] text-amber-700 dark:text-amber-300"><ShieldAlert className="mt-0.5 size-4 shrink-0" />{l.urlWarning}</p>}
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => save.mutate(undefined)} disabled={save.isPending || form === null}>{tx('Kaydet')}</Button>
              <Button variant="outline" onClick={() => test.mutate(undefined)} disabled={test.isPending || form !== null || !l?.url}><Plug className="size-4" />{' '}{tx('Bağlantıyı test et')}</Button>
              <Button variant="outline" onClick={() => sync.mutate(true)} disabled={sync.isPending || form !== null || !l?.enabled}>{tx('Önizleme (dry-run)')}</Button>
              <Button onClick={() => sync.mutate(false)} disabled={sync.isPending || form !== null || !l?.enabled}><RefreshCw className="size-4" />{' '}{tx('Şimdi eşitle')}</Button>
            </div>
            <label className="flex items-center gap-2 text-[12.5px] text-muted-foreground"><Checkbox checked={force} onCheckedChange={(v) => setForce(v === true)} />{' '}{tx('Toplu kapatmayı onaylıyorum (aktiflerin yarısından fazlası dizinde yoksa)')}</label>
            {l?.lastSyncAt && <p className="text-[12px] text-muted-foreground">{tx('Son eşitleme: {0} ({1}, {2})', [formatDateTime(l.lastSyncAt), l.lastSyncTrigger ?? '—', l.lastSyncStatus ?? '—'])}</p>}
            {result && <SyncResultView r={result} />}
            <MinimisationNote s={q.data} />
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ G28 özel alan adı */

function CustomDomainPanel() {
  const toast = useToast()
  const q = useQuery({ queryKey: ['identity-dir', 'domains'], queryFn: ({ signal }) => identityDirectoryApi.domains(signal), retry: false })
  const [domain, setDomain] = useState('')
  const add = useAction(() => identityDirectoryApi.addDomain(domain.trim()), { success: tx('Alan adı eklendi; DNS kaydını oluşturup doğrulayın'), invalidate: [['identity-dir', 'domains']], onDone: () => setDomain('') })
  const verify = useAction((id: string) => identityDirectoryApi.verifyDomain(id), { success: (r) => r.warning ?? tx('Alan adı doğrulandı'), invalidate: [['identity-dir', 'domains']] })
  const remove = useAction((id: string) => identityDirectoryApi.removeDomain(id), { success: tx('Alan adı kaldırıldı'), invalidate: [['identity-dir', 'domains']] })
  const confirm = useConfirm()
  const askRemove = async (d: { id: string; domain: string }) => {
    if (await confirm({
      title: tx('“{0}” alan adı kaldırılsın mı?', [d.domain]),
      note: tx('Çalışanlar bu adresten giriş yapamaz. Yeniden eklerseniz DNS doğrulamasını tekrarlamanız gerekir.'),
      action: tx('Kaldır'),
    })) remove.mutate(d.id)
  }
  const copy = (text: string) => void navigator.clipboard?.writeText(text).then(() => toast.ok(tx('Panoya kopyalandı')))
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Globe2 className="size-4 text-primary" />{' '}{tx('Özel alan adı')}</span>}
        note={tx('Çalışanlar HR360\'a şirketinizin alan adından (ör. ik.sirket.com.tr) şirket markasıyla girer.')} />
      <PanelBody className="space-y-4">
        {q.isPending ? <RowsSkeleton rows={2} /> : q.isError ? adminOnly : (
          <>
            <div className="flex flex-wrap items-end gap-2">
              <TextField label={tx('Alan adı')} placeholder="ik.sirket.com.tr" value={domain} onChange={(e) => setDomain(e.target.value)} className="w-64" />
              <Button onClick={() => add.mutate(undefined)} disabled={!domain.trim() || add.isPending}><Plus className="size-4" />{' '}{tx('Ekle')}</Button>
            </div>
            {q.data.items.map((d) => (
              <motion.div key={d.id} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="space-y-2 rounded-xl border border-border p-3">
                <div className="flex flex-wrap items-center gap-2">
                  {d.status === 'Verified' ? <ShieldCheck className="size-4 text-[hsl(var(--success))]" /> : <ShieldAlert className="size-4 text-amber-500" />}
                  <span className="min-w-0 flex-1 truncate font-medium">{d.domain}</span>
                  <StatusBadge tone={d.status === 'Verified' ? 'success' : 'warning'}>{d.status === 'Verified' ? tx('Doğrulandı') : tx('Doğrulama bekliyor')}</StatusBadge>
                  {d.status !== 'Verified' && <Button size="sm" variant="outline" onClick={() => verify.mutate(d.id)} disabled={verify.isPending}>{tx('Doğrula')}</Button>}
                  <Button size="icon" variant="ghost" aria-label={tx('Kaldır')} onClick={() => askRemove(d)}><Trash2 className="size-4" /></Button>
                </div>
                {d.status !== 'Verified' && (
                  <div className="space-y-1 text-[12px]">
                    <p className="text-muted-foreground">{tx('DNS\'e şu TXT kaydını ekleyin:')}</p>
                    <div className="grid grid-cols-[60px_1fr_auto] items-center gap-x-2 gap-y-1 rounded-lg bg-muted/40 px-2.5 py-1.5 font-mono text-[11.5px]">
                      <span className="text-muted-foreground">{tx('Ad')}</span><span className="break-all">{d.txtName}</span>
                      <Button size="icon" variant="ghost" aria-label={tx('Kopyala')} onClick={() => copy(d.txtName)}><Copy className="size-3.5" /></Button>
                      <span className="text-muted-foreground">{tx('Değer')}</span><span className="break-all">{d.txtValue}</span>
                      <Button size="icon" variant="ghost" aria-label={tx('Kopyala')} onClick={() => copy(d.txtValue)}><Copy className="size-3.5" /></Button>
                    </div>
                    {d.lastCheckError && <p className="text-destructive">{d.lastCheckError} · {formatDateTime(d.lastCheckedAt)}</p>}
                  </div>
                )}
              </motion.div>
            ))}
            <InfoNote>{tx('Doğrulamadan sonra platform yöneticisinin alan adı için TLS sertifikası alması ve gateway\'in bu adı kabul etmesi gerekir (README: Özel alan adı).')}</InfoNote>
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

/** Güvenlik sayfasına eklenen Y26/G28 panelleri (SCIM, LDAP/AD, özel alan adı). */
export function IdentityProvisioningPanels() {
  return (
    <div className="mt-6 grid gap-6 xl:grid-cols-2">
      <ScimPanel />
      <LdapPanel />
      <CustomDomainPanel />
    </div>
  )
}
