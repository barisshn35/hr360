import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Copy, KeyRound, Pencil, Play, Plus, RefreshCw, Trash2, UserCheck, UserX } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { EmployeePicker } from '@/components/ui/EmployeePicker'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import {
  provisioningApi,
  type ApproveResult,
  type ProvisioningProvider,
  type ProvisioningProviderInfo,
  type ProvisioningRequest,
} from '@/api/provisioning'
import { formatDate, formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/*
 * Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma ve askıya alma.
 * İşe giriş ve ayrılış taraması yalnızca "onay bekliyor" isteği açar; sağlayıcıya çağrı
 * İK onayıyla yapılır (dört göz: elle açılan isteği açan kişi onaylayamaz).
 */

const KEY = ['account-provisioning']

const META: Record<ProvisioningProvider, { title: string; steps: string[] }> = {
  Google: {
    title: tx('Google Workspace'),
    steps: [
      tx('Google Cloud Console › APIs & Services › Library: “Admin SDK API”yi etkinleştirin.'),
      tx('IAM › Service accounts: bir servis hesabı oluşturun, “Keys › Add key › JSON” ile anahtar indirin.'),
      tx('Google Admin › Security › API controls › Domain-wide delegation: servis hesabının istemci kimliğine şu kapsamı verin:'),
      tx('Adına işlem yapılacak süper yönetici hesabını ve JSON anahtarının içeriğini buraya girin.'),
    ],
  },
  Microsoft: {
    title: tx('Microsoft 365 (Entra ID)'),
    steps: [
      tx('Entra ID › App registrations › New registration (yalnızca bu kuruluş dizini).'),
      tx('API permissions › Microsoft Graph › Application permissions: User.ReadWrite.All. “Grant admin consent” deyin.'),
      tx('Certificates & secrets › New client secret. Uygulama (istemci) kimliğini, dizin (kiracı) kimliğini ve gizli anahtarı buraya girin.'),
      tx('Lisans atamak için kullanım konumu (ör. TR) gerekir; lisans ataması HR360 tarafından yapılmaz.'),
    ],
  },
}

const STATUS: Record<string, { label: string; tone: StatusTone }> = {
  Pending: { label: tx('Onay bekliyor'), tone: 'warning' },
  Processing: { label: tx('Gönderiliyor'), tone: 'info' },
  Done: { label: tx('Tamamlandı'), tone: 'success' },
  Failed: { label: tx('Başarısız'), tone: 'danger' },
  Rejected: { label: tx('Reddedildi'), tone: 'neutral' },
}

function ConfigEditor({ info, onClose }: { info: ProvisioningProviderInfo; onClose: () => void }) {
  const google = info.provider === 'Google'
  const [f, setF] = useState({
    domain: info.domain ?? '', isEnabled: info.configured ? info.isEnabled : true, autoCreate: info.autoCreate, autoSuspend: info.autoSuspend,
    clientId: google ? '' : info.clientId ?? '', credentials: '', adminSubject: info.adminSubject ?? '', orgUnit: info.orgUnit ?? '',
    msTenant: info.msTenant ?? '', usageLocation: info.usageLocation ?? 'TR',
  })
  const save = useAction(() => provisioningApi.saveConfig(info.provider, f), { success: tx('Kaydedildi'), invalidate: [KEY], onDone: onClose })
  const m = META[info.provider]
  const ready = !!f.domain && (info.hasCredentials || !!f.credentials) && (google ? !!f.adminSubject : !!f.clientId && !!f.msTenant)
  return (
    <Modal open onClose={onClose} size="lg" title={m.title}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ready || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <ol className="space-y-1.5 rounded-xl border border-border bg-muted/30 p-4 text-[12.5px] leading-relaxed">
          {m.steps.map((s, i) => <li key={i}><b>{i + 1}.</b> {s}{google && i === 2 && <code className="ml-1 font-mono text-[11px]">{info.scopes}</code>}</li>)}
        </ol>
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={tx('Alan adı')} placeholder="sirket.com" value={f.domain} onChange={(e) => setF({ ...f, domain: e.target.value })} hint={tx('Hesaplar yalnızca bu alan adında açılır.')} />
          {google ? (
            <>
              <TextField label={tx('Yönetici hesabı (adına işlem yapılır)')} placeholder="admin@sirket.com" value={f.adminSubject} onChange={(e) => setF({ ...f, adminSubject: e.target.value })} />
              <TextField label={tx('Kuruluş birimi (isteğe bağlı)')} placeholder="/Çalışanlar" value={f.orgUnit} onChange={(e) => setF({ ...f, orgUnit: e.target.value })} />
            </>
          ) : (
            <>
              <TextField label={tx('Uygulama (istemci) kimliği')} value={f.clientId} onChange={(e) => setF({ ...f, clientId: e.target.value })} />
              <TextField label={tx('Dizin (kiracı) kimliği')} value={f.msTenant} onChange={(e) => setF({ ...f, msTenant: e.target.value })} />
              <TextField label={tx('Kullanım konumu')} maxLength={2} value={f.usageLocation} onChange={(e) => setF({ ...f, usageLocation: e.target.value.toUpperCase() })} />
            </>
          )}
        </div>
        {google ? (
          <TextAreaField label={tx('Servis hesabı anahtarı (JSON)')} rows={5} autoComplete="off" spellCheck={false} className="font-mono text-[11px]"
            placeholder={info.hasCredentials ? tx('•••• kayıtlı ({0}) — değiştirmek için yeni anahtarı yapıştırın', [info.clientId ?? '']) : tx('Google Cloud hizmet hesabının JSON anahtar dosyasının içeriğini yapıştırın')}
            value={f.credentials} onChange={(e) => setF({ ...f, credentials: e.target.value })} />
        ) : (
          <TextField label={tx('Client secret')} type="password" autoComplete="off" placeholder={info.hasCredentials ? tx('•••• (değiştirmek için girin)') : ''}
            value={f.credentials} onChange={(e) => setF({ ...f, credentials: e.target.value })} />
        )}
        <div className="flex flex-wrap gap-x-5 gap-y-2 text-[13px]">
          <label className="flex items-center gap-2"><Checkbox checked={f.autoCreate} onCheckedChange={(v) => setF({ ...f, autoCreate: v === true })} />{' '}{tx('İşe girişte açma isteği oluştur')}</label>
          <label className="flex items-center gap-2"><Checkbox checked={f.autoSuspend} onCheckedChange={(v) => setF({ ...f, autoSuspend: v === true })} />{' '}{tx('Ayrılışta askıya alma isteği oluştur')}</label>
          <label className="flex items-center gap-2"><Checkbox checked={f.isEnabled} onCheckedChange={(v) => setF({ ...f, isEnabled: v === true })} />{' '}{tx('Etkin')}</label>
        </div>
        <p className="text-[12px] text-muted-foreground">{tx('Anahtar şifreli saklanır ve bir daha gösterilmez. Etkinleştirmek için KVKK › Yurt dışı aktarım ekranında hukuki dayanak kaydı gerekir.')}</p>
      </div>
    </Modal>
  )
}

function NewRequest({ providers, onClose }: { providers: ProvisioningProvider[]; onClose: () => void }) {
  const [f, setF] = useState({ employeeId: '', provider: providers[0] ?? 'Google', action: 'Create' as 'Create' | 'Suspend', accountEmail: '', note: '' })
  const create = useAction(() => provisioningApi.createRequest(f), { success: tx('İstek açıldı; başka bir İK yöneticisinin onayını bekliyor'), invalidate: [KEY], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Elle istek')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!f.employeeId || create.isPending} onClick={() => create.mutate(undefined)}>{tx('İstek aç')}</Button></>}>
      <div className="space-y-4">
        <EmployeePicker value={f.employeeId} onChange={(v) => setF({ ...f, employeeId: v })} />
        <div className="grid grid-cols-2 gap-3">
          <SelectField label={tx('Sağlayıcı')} value={f.provider} onChange={(v) => setF({ ...f, provider: v as ProvisioningProvider })} options={providers.map((p) => ({ value: p, label: META[p].title }))} />
          <SelectField label={tx('İşlem')} value={f.action} onChange={(v) => setF({ ...f, action: v as 'Create' | 'Suspend' })} options={[{ value: 'Create', label: tx('Hesap aç') }, { value: 'Suspend', label: tx('Askıya al') }]} />
        </div>
        <TextField label={tx('Hesap adresi (isteğe bağlı)')} placeholder={tx('Boşsa ad.soyad@alan-adı önerilir')} value={f.accountEmail} onChange={(e) => setF({ ...f, accountEmail: e.target.value })} />
        <TextField label={tx('Not (isteğe bağlı)')} value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} />
      </div>
    </Modal>
  )
}

function ApproveCreate({ req, onClose, onResult }: { req: ProvisioningRequest; onClose: () => void; onResult: (r: ApproveResult) => void }) {
  const [email, setEmail] = useState(req.accountEmail)
  const approve = useAction(() => provisioningApi.approve(req.id, email), { invalidate: [KEY], onDone: (r) => { onClose(); onResult(r) } })
  return (
    <Modal open onClose={onClose} title={tx('Hesap açılsın mı?')} note={tx('{0} için {1} hesabı açılır. Geçici parola yalnızca bir kez gösterilir.', [req.employeeName ?? '—', META[req.provider].title])}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!email || approve.isPending} onClick={() => approve.mutate(undefined)}><UserCheck className="size-4" />{' '}{tx('Onayla ve aç')}</Button></>}>
      <TextField label={tx('Hesap adresi')} value={email} onChange={(e) => setEmail(e.target.value)} />
    </Modal>
  )
}

function PasswordOnce({ result, onClose }: { result: ApproveResult; onClose: () => void }) {
  const toast = useToast()
  return (
    <Modal open onClose={onClose} title={tx('Hesap açıldı')} footer={<Button onClick={onClose}>{tx('Kapat')}</Button>}>
      <div className="space-y-3 text-[13px]">
        <p>{tx('Hesap:')}{' '}<b className="font-mono">{result.accountEmail}</b></p>
        {result.initialPassword && (
          <>
            <div className="flex items-center gap-2 rounded-xl border border-border bg-muted/40 px-3 py-2">
              <KeyRound className="size-4 text-muted-foreground" />
              <span className="flex-1 font-mono text-[13px]">{result.initialPassword}</span>
              <button className="cursor-pointer text-muted-foreground hover:text-foreground" aria-label={tx('Kopyala')} onClick={() => navigator.clipboard.writeText(result.initialPassword!).then(() => toast.ok(tx('Kopyalandı')))}><Copy className="size-4" /></button>
            </div>
            <p className="text-[12px] text-muted-foreground">{tx('Geçici parola saklanmaz ve bir daha gösterilmez. Çalışana güvenli bir kanaldan iletin; ilk girişte değiştirmesi istenir.')}</p>
          </>
        )}
      </div>
    </Modal>
  )
}

export function AccountProvisioningPanel() {
  const state = useQuery({ queryKey: KEY, queryFn: ({ signal }) => provisioningApi.state(signal) })
  const enabled = state.data?.enabled === true
  const [filter, setFilter] = useState<'open' | 'all'>('open')
  const requests = useQuery({ queryKey: [...KEY, 'requests'], queryFn: ({ signal }) => provisioningApi.requests(undefined, signal), enabled })
  const [edit, setEdit] = useState<ProvisioningProviderInfo | null>(null)
  const [newReq, setNewReq] = useState(false)
  const [approving, setApproving] = useState<ProvisioningRequest | null>(null)
  const [result, setResult] = useState<ApproveResult | null>(null)
  const confirm = useConfirm()
  const test = useAction((p: ProvisioningProvider) => provisioningApi.testConfig(p), {
    success: (r) => (r.ok ? tx('Bağlantı başarılı') : tx('Bağlantı başarısız: {0}', [r.message])), invalidate: [KEY],
  })
  const del = useAction((p: ProvisioningProvider) => provisioningApi.deleteConfig(p), { success: tx('Kaldırıldı'), invalidate: [KEY] })
  const scan = useAction(() => provisioningApi.scan(), { success: (r) => tx('{0} açma, {1} askıya alma isteği oluşturuldu', [r.create, r.suspend]), invalidate: [KEY] })
  const approveSuspend = useAction((id: string) => provisioningApi.approve(id), { success: tx('Hesap askıya alındı'), invalidate: [KEY] })
  const reject = useAction((id: string) => provisioningApi.reject(id), { success: tx('Reddedildi'), invalidate: [KEY] })

  if (state.isPending) return <RowsSkeleton />
  if (!enabled) {
    return (
      <InfoNote>
        {tx('Hesap açma/kapatma bu kurulumda kapalı. Sunucu yöneticisi .env dosyasında')}{' '}<code className="font-mono">ACCOUNT_PROVISIONING_ENABLED=true</code>{' '}
        {tx('yapıp governance-service\'i yeniden başlattığında, işe girenler için Google Workspace / Microsoft 365 hesabı açma ve ayrılanların hesabını askıya alma İK onayıyla yapılabilir.')}
      </InfoNote>
    )
  }
  const providers = state.data!.providers
  const configured = providers.filter((p) => p.configured).map((p) => p.provider)
  const rows = (requests.data ?? []).filter((r) => filter === 'all' || r.status === 'Pending' || r.status === 'Failed')

  const askSuspend = async (r: ProvisioningRequest) => {
    if (await confirm({ title: tx('{0} hesabı askıya alınsın mı?', [r.accountEmail]), note: tx('Hesap kapatılır ve açık oturumları sonlandırılır. Veriler silinmez.'), action: tx('Askıya al') }))
      approveSuspend.mutate(r.id)
  }
  const askReject = async (r: ProvisioningRequest) => {
    if (await confirm({ title: tx('İstek reddedilsin mi?'), note: tx('Bu çalışan için aynı işlem otomatik olarak yeniden önerilmez.'), action: tx('Reddet') }))
      reject.mutate(r.id)
  }

  return (
    <div className="space-y-5">
      <InfoNote>{tx('İşe giren çalışan için iş hesabı açma ve ayrılan çalışanın hesabını askıya alma istekleri otomatik hazırlanır;')}{' '}<b>{tx('hiçbir hesap İK onayı olmadan açılmaz ya da kapatılmaz.')}</b>{' '}{tx('Sağlayıcıya yalnızca ad, soyad ve iş e-postası gönderilir; her adım denetim kaydına yazılır.')}</InfoNote>
      <div className="grid gap-4 lg:grid-cols-2">
        {providers.map((p) => (
          <div key={p.provider} className="surface flex flex-col gap-3 rounded-2xl border border-border p-4">
            <div className="flex items-start justify-between gap-2">
              <div>
                <p className="text-[14px] font-semibold">{META[p.provider].title}</p>
                {p.configured && <p className="mt-0.5 font-mono text-[12px] text-muted-foreground">{p.domain}</p>}
              </div>
              <StatusBadge tone={!p.configured ? 'neutral' : p.isEnabled ? 'success' : 'warning'}>{!p.configured ? tx('Kurulmadı') : p.isEnabled ? tx('Etkin') : tx('Kapalı')}</StatusBadge>
            </div>
            {p.configured && (
              <p className="text-[12px] text-muted-foreground">
                {[p.autoCreate && tx('işe girişte açma'), p.autoSuspend && tx('ayrılışta askıya alma')].filter(Boolean).join(' · ') || tx('yalnızca elle istek')}
                {p.lastTestAt && <> · {tx('son test {0}', [formatDateTime(p.lastTestAt)])}</>}
              </p>
            )}
            {p.lastError && <p className="text-[12px] text-destructive">{p.lastError}</p>}
            <div className="mt-auto flex flex-wrap gap-1.5">
              <Button size="sm" variant={p.configured ? 'outline' : 'default'} onClick={() => setEdit(p)}><Pencil className="size-4" />{' '}{p.configured ? tx('Düzenle') : tx('Kur')}</Button>
              {p.configured && <Button size="sm" variant="outline" disabled={test.isPending} onClick={() => test.mutate(p.provider)}><Play className="size-4" />{' '}{tx('Bağlantıyı test et')}</Button>}
              {p.configured && <Button size="sm" variant="ghost" aria-label={tx('Kaldır')} onClick={() => del.mutate(p.provider)}><Trash2 className="size-4" /></Button>}
            </div>
          </div>
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <h3 className="mr-auto text-[14px] font-semibold">{tx('İstekler')}</h3>
        <SelectField label={tx('Göster')} value={filter} onChange={(v) => setFilter(v as 'open' | 'all')} options={[{ value: 'open', label: tx('Bekleyen ve başarısız') }, { value: 'all', label: tx('Tümü') }]} />
        <Button size="sm" variant="outline" disabled={!configured.length || scan.isPending} onClick={() => scan.mutate(undefined)}><RefreshCw className="size-4" />{' '}{tx('Şimdi tara')}</Button>
        <Button size="sm" disabled={!configured.length} onClick={() => setNewReq(true)}><Plus className="size-4" />{' '}{tx('Elle istek')}</Button>
      </div>
      {requests.isPending ? <RowsSkeleton /> : rows.length === 0 ? <EmptyState icon={UserCheck} title={tx('Bekleyen istek yok')} /> : (
        <div className="overflow-x-auto rounded-2xl border border-border">
          <table className="w-full text-[13px]">
            <thead className="bg-muted/40 text-left text-[12px] text-muted-foreground">
              <tr><th className="px-3 py-2">{tx('Çalışan')}</th><th className="px-3 py-2">{tx('İşlem')}</th><th className="px-3 py-2">{tx('Hesap')}</th><th className="px-3 py-2">{tx('Durum')}</th><th className="px-3 py-2" /></tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const open = r.status === 'Pending' || r.status === 'Failed'
                return (
                  <tr key={r.id} className="border-t border-border align-top">
                    <td className="px-3 py-2">
                      <p className="font-medium">{r.employeeName ?? '—'}</p>
                      <p className="text-[11.5px] text-muted-foreground">{r.hireDate && tx('İşe giriş {0}', [formatDate(r.hireDate)])}{r.source === 'Auto' ? ` · ${tx('otomatik')}` : r.requestedByName ? ` · ${r.requestedByName}` : ''}</p>
                    </td>
                    <td className="px-3 py-2">{r.action === 'Create' ? tx('Hesap aç') : tx('Askıya al')} · {META[r.provider].title}</td>
                    <td className="px-3 py-2 font-mono text-[12px]">{r.accountEmail}</td>
                    <td className="px-3 py-2">
                      <StatusBadge tone={STATUS[r.status]?.tone ?? 'neutral'}>{STATUS[r.status]?.label ?? r.status}</StatusBadge>
                      {r.error && <p className="mt-1 max-w-xs text-[11.5px] text-destructive">{r.error}</p>}
                      {r.decidedByName && <p className="mt-1 text-[11.5px] text-muted-foreground">{r.decidedByName} · {formatDateTime(r.decidedAt)}</p>}
                    </td>
                    <td className="px-3 py-2 text-right whitespace-nowrap">
                      {open && (
                        <>
                          <Button size="sm" variant="outline" onClick={() => (r.action === 'Create' ? setApproving(r) : askSuspend(r))}>
                            {r.action === 'Create' ? <UserCheck className="size-4" /> : <UserX className="size-4" />}{' '}{r.status === 'Failed' ? tx('Yeniden dene') : tx('Onayla')}
                          </Button>{' '}
                          <Button size="sm" variant="ghost" onClick={() => askReject(r)}>{tx('Reddet')}</Button>
                        </>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      {edit && <ConfigEditor info={edit} onClose={() => setEdit(null)} />}
      {newReq && <NewRequest providers={configured} onClose={() => setNewReq(false)} />}
      {approving && <ApproveCreate req={approving} onClose={() => setApproving(null)} onResult={setResult} />}
      {result && <PasswordOnce result={result} onClose={() => setResult(null)} />}
    </div>
  )
}
