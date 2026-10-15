import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { BarChart3, Copy, KeyRound, MessageSquare, Plus, RefreshCw, Send, Trash2, Webhook as WebhookIcon } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { useToast } from '@/components/ui/Toast'
import { useConfirm } from '@/components/ui/Confirm'
import { governanceApi, type ApiKeyRow, type Webhook } from '@/api/governance'
import { formatDate, formatDateTime, formatNumber, formatRelativeToNow } from '@/lib/format'
import { TenantGate, useAction } from '@/features/shared/kit'
import { ChatAppsPanel } from './ChatAppsPanel'
import { CalendarProvidersPanel } from './CalendarProvidersPanel'
import { AccountProvisioningPanel } from './AccountProvisioningPanel'
import { WebhookDeliveriesPanel } from './WebhookDeliveriesPanel'
import { retryStateView, scopeLabel, simplifyScopes } from './integrationsHelpers'
import { tx } from '@/lib/i18n'

const EVENTS = ['*', 'employee.hired', 'employee.assigned', 'employee.status-changed', 'workflow.submitted', 'workflow.approved', 'workflow.rejected', 'leave.approved']

function EventPicker({ value, onChange }: { value: string[]; onChange: (v: string[]) => void }) {
  return (
    <div>
      <p className="mb-1.5 text-[13px] font-medium">{tx('Olaylar')}</p>
      <div className="flex flex-wrap gap-1.5">
        {EVENTS.map((e) => {
          const on = value.includes(e)
          return <button key={e} type="button" onClick={() => onChange(on ? value.filter((x) => x !== e) : [...value, e])} className={`cursor-pointer rounded-full border px-2.5 py-1 font-mono text-[11.5px] transition ${on ? 'border-primary bg-primary/15' : 'border-border text-muted-foreground'}`}>{e === '*' ? tx('tümü (*)') : e}</button>
        })}
      </div>
    </div>
  )
}

function Copyable({ text, mono = true }: { text: string; mono?: boolean }) {
  const toast = useToast()
  return (
    <span className="inline-flex max-w-full items-center gap-1.5 rounded-lg bg-muted/60 px-2 py-1">
      <span className={`truncate ${mono ? 'font-mono text-[11.5px]' : 'text-[12.5px]'}`}>{text}</span>
      <button onClick={() => navigator.clipboard.writeText(text).then(() => toast.ok(tx('Kopyalandı')))} className="cursor-pointer text-muted-foreground hover:text-foreground" aria-label={tx('Kopyala')}><Copy className="size-3.5" /></button>
    </span>
  )
}

function Webhooks() {
  const list = useQuery({ queryKey: ['webhooks'], queryFn: ({ signal }) => governanceApi.webhooks(signal) })
  const [form, setForm] = useState<{ name: string; url: string; events: string[] } | null>(null)
  const [inbox, setInbox] = useState<string | null>(null)
  const [deliveries, setDeliveries] = useState<Webhook | null>(null)
  const create = useAction(() => governanceApi.createWebhook({ ...form!, isEnabled: true }), { success: tx('Webhook eklendi'), invalidate: [['webhooks']], onDone: () => setForm(null) })
  const test = useAction(() => governanceApi.createTestReceiver(), { success: tx('Test alıcısı oluşturuldu'), invalidate: [['webhooks']], onDone: (r) => setInbox(r.token) })
  const ping = useAction((id: string) => governanceApi.pingWebhook(id), { success: (r) => (r.ok ? tx('Teslim edildi (HTTP {0})', [r.lastStatus]) : tx('Başarısız (HTTP {0})', [r.lastStatus ?? '—'])), invalidate: [['webhooks']] })
  const rotate = useAction((id: string) => governanceApi.rotateWebhookSecret(id), { success: tx('Yeni gizli anahtar üretildi'), invalidate: [['webhooks']] })
  const del = useAction((id: string) => governanceApi.deleteWebhook(id), { success: tx('Silindi'), invalidate: [['webhooks']] })
  const confirm = useConfirm()
  const askRotate = async (w: Webhook) => {
    if (await confirm({
      title: tx('“{0}” için gizli anahtar yenilensin mi?', [w.name]),
      note: tx('Eski anahtar hemen geçersiz olur; alıcı sistem yeni anahtarla güncellenene kadar imza doğrulaması başarısız olur.'),
      action: tx('Anahtarı yenile'),
    })) rotate.mutate(w.id)
  }
  const askDelete = async (w: Webhook) => {
    if (await confirm({
      title: tx('“{0}” webhook\'u silinsin mi?', [w.name]),
      note: tx('Bu adrese artık olay gönderilmez. Teslimat geçmişi denetim için saklanır (“Teslimat hataları” sekmesinde görünür) ancak yeniden gönderilemez. Bu işlem geri alınamaz.'),
      action: tx('Sil'),
    })) del.mutate(w.id)
  }
  const inboxQ = useQuery({ queryKey: ['webhook-inbox', inbox], enabled: !!inbox, queryFn: ({ signal }) => governanceApi.webhookInbox(inbox!, signal), refetchInterval: 3000 })
  const dQ = useQuery({ queryKey: ['webhook-deliveries', deliveries?.id], enabled: !!deliveries, queryFn: ({ signal }) => governanceApi.webhookDeliveries(deliveries!.id, signal) })
  return (
    <div className="space-y-5">
      <InfoNote>{tx('Her teslimat')}{' '}<code className="font-mono text-[12px]">{tx('X-HR360-Signature: sha256=…')}</code>{' '}{tx('başlığıyla HMAC-SHA256 imzalanır (gövde + gizli anahtar). Alıcı imzayı doğrulamalıdır. Geçici hatalar üstel geri çekilmeyle otomatik yeniden denenir (ayrıntı: “Teslimat hataları” sekmesi). 20 ardışık hatada webhook otomatik kapanır.')}</InfoNote>
      <div className="flex gap-2"><Button onClick={() => setForm({ name: '', url: 'https://', events: ['*'] })}><Plus className="size-4" />{' '}{tx('Webhook')}</Button><Button variant="outline" onClick={() => test.mutate(undefined)}><WebhookIcon className="size-4" />{' '}{tx('Test alıcısı oluştur')}</Button></div>
      {list.isPending ? <RowsSkeleton /> : (list.data ?? []).length === 0 ? <EmptyState icon={WebhookIcon} title={tx('Webhook yok')} detail={tx('Bir sistem HR360 olaylarını anında almak istiyorsa buraya adresini ekleyin.')} /> : (
        <div className="grid gap-4 lg:grid-cols-2">
          {list.data!.map((w) => (
            <div key={w.id} className="surface rounded-2xl border border-border p-4">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0"><p className="text-[14px] font-semibold">{w.name}</p><Copyable text={w.url} /></div>
                <StatusBadge tone={!w.isEnabled ? 'neutral' : w.lastStatus == null ? 'info' : w.lastStatus < 300 ? 'success' : 'danger'}>{!w.isEnabled ? tx('Kapalı') : w.lastStatus == null ? tx('Bekliyor') : tx('HTTP {0}', [w.lastStatus])}</StatusBadge>
              </div>
              <div className="mt-2 flex flex-wrap gap-1">{w.events.map((e) => <span key={e} className="rounded-full bg-muted px-2 py-0.5 font-mono text-[10.5px]">{e}</span>)}</div>
              <div className="mt-2 text-[12px] text-muted-foreground">{tx('Gizli anahtar:')}{' '}<Copyable text={w.secret} /></div>
              <p className="mt-1 text-[11.5px] text-muted-foreground">{w.lastDeliveredAt ? tx('Son teslim {0}', [formatRelativeToNow(w.lastDeliveredAt)]) : tx('Henüz teslim yok')}{w.failureCount ? tx(' · {0} ardışık hata', [w.failureCount]) : ''}</p>
              <div className="mt-3 flex flex-wrap gap-1.5">
                <Button size="sm" variant="outline" onClick={() => ping.mutate(w.id)}><Send className="size-4" />{' '}{tx('Ping')}</Button>
                <Button size="sm" variant="outline" onClick={() => setDeliveries(w)}>{tx('Teslimatlar')}</Button>
                {w.url.includes('/inbox/') && <Button size="sm" variant="outline" onClick={() => setInbox(w.url.split('/inbox/')[1])}>{tx('Gelen kutusu')}</Button>}
                <Button size="sm" variant="ghost" onClick={() => askRotate(w)} title={tx('Anahtarı yenile')} aria-label={tx('Anahtarı yenile')}><RefreshCw className="size-4" /></Button>
                <Button size="sm" variant="ghost" onClick={() => askDelete(w)} aria-label={tx('Sil')}><Trash2 className="size-4" /></Button>
              </div>
            </div>
          ))}
        </div>
      )}
      {form && (
        <Modal open onClose={() => setForm(null)} title={tx('Yeni webhook')} footer={<><Button variant="outline" onClick={() => setForm(null)}>{tx('Vazgeç')}</Button><Button disabled={!form.name || !form.url.startsWith('http') || !form.events.length} onClick={() => create.mutate(undefined)}>{tx('Ekle')}</Button></>}>
          <div className="space-y-4"><TextField label={tx('Ad')} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /><TextField label={tx('Adres (https)')} value={form.url} onChange={(e) => setForm({ ...form, url: e.target.value })} /><EventPicker value={form.events} onChange={(v) => setForm({ ...form, events: v })} /></div>
        </Modal>
      )}
      {inbox && (
        <Modal open onClose={() => setInbox(null)} size="lg" title={tx('Test alıcısı — gelen kutusu')} note={tx('3 sn\'de bir yenilenir. Bir izin talebi oluşturun veya Ping gönderin.')}>
          <ul className="space-y-2">
            {(inboxQ.data ?? []).length === 0 && <p className="text-[13px] text-muted-foreground">{tx('Henüz teslimat yok.')}</p>}
            {inboxQ.data?.map((d, i) => (
              <motion.li key={d.receivedAt + i} initial={{ opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }} className="rounded-xl border border-border p-3">
                <div className="mb-1 flex items-center gap-2 text-[12px]"><span className="font-mono">{d.event ?? '—'}</span><StatusBadge tone={d.signatureValid ? 'success' : d.signatureValid === false ? 'danger' : 'neutral'}>{d.signatureValid ? tx('imza geçerli') : d.signatureValid === false ? tx('imza GEÇERSİZ') : tx('imzasız')}</StatusBadge><span className="text-muted-foreground">{formatDateTime(d.receivedAt)}</span></div>
                <pre className="max-h-40 overflow-auto rounded-lg bg-muted/50 p-2 font-mono text-[11px] whitespace-pre-wrap">{(() => { try { return JSON.stringify(JSON.parse(d.body), null, 2) } catch { return d.body } })()}</pre>
              </motion.li>
            ))}
          </ul>
        </Modal>
      )}
      {deliveries && (
        <Modal open onClose={() => setDeliveries(null)} size="lg" title={tx('Teslimatlar — {0}', [deliveries.name])}>
          <ul className="divide-y divide-border text-[12.5px]">
            {dQ.data?.map((d) => <li key={d.id} className="flex items-center gap-3 py-2"><span className="tabular w-36 text-muted-foreground">{formatDateTime(d.occurredAt)}</span><span className="font-mono">{d.eventType}</span><StatusBadge tone={d.statusCode && d.statusCode < 300 ? 'success' : 'danger'}>{d.statusCode ?? tx('hata')}</StatusBadge><span className="text-muted-foreground">{tx('{0} ms', [d.durationMs])}</span>{(d.attempt ?? 1) > 1 && <span className="text-muted-foreground">{tx('deneme {0}', [d.attempt])}</span>}{retryStateView(d.retryState) && <StatusBadge tone={retryStateView(d.retryState)!.tone}>{retryStateView(d.retryState)!.label}</StatusBadge>}{d.error && <span className="truncate text-destructive">{d.error}</span>}</li>)}
          </ul>
        </Modal>
      )}
    </div>
  )
}

const EXPIRY_OPTIONS = [
  { value: '0', label: tx('Süresiz') },
  { value: '30', label: tx('30 gün') },
  { value: '90', label: tx('90 gün') },
  { value: '180', label: tx('180 gün') },
  { value: '365', label: tx('1 yıl') },
]

function ApiKeyUsageModal({ k, onClose }: { k: ApiKeyRow; onClose: () => void }) {
  const q = useQuery({ queryKey: ['api-key-usage', k.id], queryFn: ({ signal }) => governanceApi.apiKeyUsage(k.id, 30, signal) })
  const max = Math.max(1, ...(q.data?.byDay ?? []).map((d) => d.count + d.denied))
  return (
    <Modal open onClose={onClose} size="lg" title={tx('Kullanım — {0}', [k.name])} note={tx('Son 30 gün · istek ve ret (yetki dışı / hız sınırı) sayıları. IP adresi ve istek içeriği tutulmaz.')}>
      {q.isPending ? <RowsSkeleton /> : (
        <div className="space-y-4 text-[12.5px]">
          <div className="flex flex-wrap gap-4">
            {/* Sunucudaki "total" yalnızca kabul edilen istekleri sayar; reddedilenler ayrıdır. */}
            <span>{tx('Başarılı: {0}', [formatNumber(q.data?.total ?? 0)])}</span>
            <span className={q.data?.denied ? 'text-destructive' : ''}>{tx('Reddedilen: {0}', [formatNumber(q.data?.denied ?? 0)])}</span>
            <span>{tx('Toplam: {0}', [formatNumber((q.data?.total ?? 0) + (q.data?.denied ?? 0))])}</span>
            <span className="text-muted-foreground">{q.data?.lastUsedAt ? tx('Son kullanım {0} ({1})', [formatRelativeToNow(q.data.lastUsedAt), q.data.lastUsedScope ?? '—']) : tx('hiç kullanılmadı')}</span>
          </div>
          {(q.data?.byDay ?? []).length === 0 ? <p className="text-muted-foreground">{tx('Bu dönemde kullanım yok.')}</p> : (
            <>
              <div className="flex h-24 items-end gap-0.5" role="img" aria-label={tx('Günlük istek sayısı')}>
                {q.data!.byDay.map((d) => (
                  <div key={d.day} className="flex flex-1 flex-col justify-end" title={tx('{0}: {1} başarılı, {2} reddedilen', [formatDate(d.day), d.count, d.denied])}>
                    {d.denied > 0 && <div className="bg-destructive/70" style={{ height: `${(d.denied / max) * 96}px` }} />}
                    <div className="rounded-t bg-primary/70" style={{ height: `${(d.count / max) * 96}px` }} />
                  </div>
                ))}
              </div>
              <ul className="divide-y divide-border">
                {q.data!.byScope.map((s) => <li key={s.scope} className="flex items-center gap-3 py-1.5"><span className="w-40 font-mono">{s.scope}</span><span className="tabular">{formatNumber(s.count)}</span>{s.denied > 0 && <span className="tabular text-destructive">{tx('{0} ret', [s.denied])}</span>}</li>)}
              </ul>
            </>
          )}
        </div>
      )}
    </Modal>
  )
}

function ApiKeys() {
  const list = useQuery({ queryKey: ['api-keys'], queryFn: ({ signal }) => governanceApi.apiKeys(signal) })
  const scopes = useQuery({ queryKey: ['api-scopes'], queryFn: ({ signal }) => governanceApi.apiScopes(signal), staleTime: Infinity })
  const [form, setForm] = useState<{ name: string; scopes: string[]; expires: string } | null>(null)
  const [created, setCreated] = useState<{ key: string; note?: string } | null>(null)
  const [usage, setUsage] = useState<ApiKeyRow | null>(null)
  const [rotating, setRotating] = useState<{ key: ApiKeyRow; grace: string } | null>(null)
  const create = useAction(() => governanceApi.createApiKey(form!.name, simplifyScopes(form!.scopes), Number(form!.expires) || null), { invalidate: [['api-keys']], onDone: (r) => { setForm(null); setCreated({ key: r.key }) } })
  const rotate = useAction(() => governanceApi.rotateApiKey(rotating!.key.id, Number(rotating!.grace)), {
    invalidate: [['api-keys']],
    onDone: (r) => {
      setRotating(null)
      setCreated({ key: r.key, note: r.oldKeyValidUntil ? tx('Eski anahtar {0} tarihine kadar çalışmaya devam eder.', [formatDateTime(r.oldKeyValidUntil)]) : undefined })
    },
  })
  const revoke = useAction((id: string) => governanceApi.revokeApiKey(id), { success: tx('Anahtar iptal edildi'), invalidate: [['api-keys']] })
  const confirm = useConfirm()
  const askRevoke = async (k: { id: string; name: string }) => {
    if (await confirm({
      title: tx('“{0}” API anahtarı iptal edilsin mi?', [k.name]),
      note: tx('Bu anahtarı kullanan sistemlerin istekleri hemen reddedilir. İptal geri alınamaz; gerekirse yeni anahtar oluşturmanız gerekir.'),
      action: tx('İptal et'),
    })) revoke.mutate(k.id)
  }
  const base = `${window.location.origin}/api/governance/public/v1`
  const statusBadge = (k: ApiKeyRow) => {
    const st = k.status ?? (k.active ? 'active' : 'revoked')
    if (st === 'revoked') return <StatusBadge>{tx('İptal')}</StatusBadge>
    if (st === 'expired') return <StatusBadge tone="danger">{tx('Süresi doldu')}</StatusBadge>
    if (k.expiresSoon) return <StatusBadge tone="warning">{tx('Süresi yaklaşıyor')}</StatusBadge>
    return <StatusBadge tone="success">{tx('Etkin')}</StatusBadge>
  }
  return (
    <div className="space-y-5">
      <div className="flex gap-2"><Button onClick={() => setForm({ name: '', scopes: ['read-only'], expires: '90' })}><KeyRound className="size-4" />{' '}{tx('Yeni anahtar')}</Button></div>
      <Panel>
        <PanelBody className="p-0">
          {list.isPending ? <div className="p-5"><RowsSkeleton /></div> : (list.data ?? []).length === 0 ? <p className="p-5 text-[13px] text-muted-foreground">{tx('Anahtar yok.')}</p> : (
            <ul className="divide-y divide-border" data-testid="api-key-list">{list.data!.map((k) => (
              <li key={k.id} className="flex flex-col gap-2 px-4 py-3 text-[13px] sm:flex-row sm:items-center sm:gap-3 sm:px-5">
                {/* Dar ekranda (390 px) bilgi satırı tam genişlik, işlemler altta ayrı satır. */}
                <div className="flex min-w-0 flex-1 items-start gap-3">
                  <KeyRound className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                  <div className="min-w-0 flex-1">
                    <p className="break-words font-medium">{k.name}</p>
                    <p className="break-all font-mono text-[11.5px] text-muted-foreground">{tx('hr360_{0}_•••• · {1}', [k.prefix, k.scopes.join(', ')])}</p>
                    <p className="text-[11.5px] text-muted-foreground">
                      {k.expiresAt ? tx('son kullanma {0}', [formatDate(k.expiresAt)]) : tx('süresiz')}
                      {' · '}{tx('30 günde {0} başarılı', [formatNumber(k.last30 ?? 0)])}
                      {k.denied30 ? tx(' · {0} reddedilen', [formatNumber(k.denied30)]) : ''}
                      {k.rotatedFromId ? tx(' · döndürülmüş anahtar') : ''}
                    </p>
                    <p className="text-[11.5px] text-muted-foreground sm:hidden">{k.lastUsedAt ? tx('son kullanım {0}', [formatRelativeToNow(k.lastUsedAt)]) : tx('hiç kullanılmadı')}</p>
                  </div>
                </div>
                <div className="flex flex-wrap items-center gap-2 pl-7 sm:pl-0">
                  <span className="hidden text-[12px] text-muted-foreground sm:inline">{k.lastUsedAt ? tx('son kullanım {0}', [formatRelativeToNow(k.lastUsedAt)]) : tx('hiç kullanılmadı')}</span>
                  {statusBadge(k)}
                  <Button size="sm" variant="ghost" onClick={() => setUsage(k)} aria-label={tx('Kullanım')} title={tx('Kullanım')}><BarChart3 className="size-4" /></Button>
                  {k.active && <Button size="sm" variant="outline" onClick={() => setRotating({ key: k, grace: '24' })}><RefreshCw className="size-4" />{' '}{tx('Döndür')}</Button>}
                  {k.active && <Button size="sm" variant="outline" onClick={() => askRevoke(k)}>{tx('İptal et')}</Button>}
                </div>
              </li>
            ))}</ul>
          )}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Açık API v1')} note={tx('Salt okunur · dakikada 120 istek · OpenAPI tanımı aşağıda')} />
        <PanelBody className="space-y-2 text-[12.5px]">
          {['employees', 'departments', 'leaves?from=2026-01-01', 'events?since=2026-10-01T00:00:00Z'].map((p) => <pre key={p} className="overflow-x-auto rounded-lg bg-muted/50 p-2.5 font-mono text-[11.5px]">{tx('curl -H "X-Api-Key: hr360_…" {0}/{1}', [base, p])}</pre>)}
          <p>{tx('OpenAPI:')}{' '}<Copyable text={`${base}/openapi.json`} /></p>
        </PanelBody>
      </Panel>
      {form && (
        <Modal open onClose={() => setForm(null)} title={tx('Yeni API anahtarı')} footer={<><Button variant="outline" onClick={() => setForm(null)}>{tx('Vazgeç')}</Button><Button disabled={!form.name || !form.scopes.length || create.isPending} onClick={() => create.mutate(undefined)}>{tx('Oluştur')}</Button></>}>
          <div className="space-y-4"><TextField label={tx('Ad')} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder={tx('Örn. Power BI')} />
            <div className="space-y-2">
              <p className="text-[13px] font-medium">{tx('Yetkiler')}</p>
              {scopes.data?.map((s) => {
                const covered = s !== 'read-only' && s.endsWith(':read') && form.scopes.includes('read-only')
                return (
                  <label key={s} className={`flex items-start gap-2 text-[12.5px] ${covered ? 'opacity-50' : ''}`}>
                    <Checkbox checked={covered || form.scopes.includes(s)} disabled={covered} onCheckedChange={(v) => setForm({ ...form, scopes: v === true ? [...form.scopes, s] : form.scopes.filter((x) => x !== s) })} />
                    <span><span className="font-mono">{s}</span><span className="block text-[11.5px] text-muted-foreground">{scopeLabel(s)}</span></span>
                  </label>
                )
              })}
              <p className="text-[11.5px] text-muted-foreground">{tx('En az yetki ilkesi: anahtara yalnızca entegrasyonun ihtiyaç duyduğu yetkileri verin.')}</p>
            </div>
            <SelectField label={tx('Son kullanma')} value={form.expires} onChange={(v) => setForm({ ...form, expires: v })} options={EXPIRY_OPTIONS} />
          </div>
        </Modal>
      )}
      {rotating && (
        <Modal open onClose={() => setRotating(null)} title={tx('“{0}” anahtarını döndür', [rotating.key.name])} note={tx('Aynı ad ve yetkilerle yeni bir anahtar üretilir. Eski anahtar geçiş süresi sonunda kendiliğinden geçersiz olur; REST hook abonelikleri yeni anahtara taşınır.')}
          footer={<><Button variant="outline" onClick={() => setRotating(null)}>{tx('Vazgeç')}</Button><Button disabled={rotate.isPending} onClick={() => rotate.mutate(undefined)}>{tx('Döndür')}</Button></>}>
          <SelectField label={tx('Eski anahtar için geçiş süresi')} value={rotating.grace} onChange={(v) => setRotating({ ...rotating, grace: v })} options={[
            { value: '0', label: tx('Hemen iptal et') },
            { value: '1', label: tx('1 saat') },
            { value: '24', label: tx('24 saat') },
            { value: '72', label: tx('72 saat') },
          ]} />
        </Modal>
      )}
      {created && (
        <Modal open onClose={() => setCreated(null)} title={tx('Anahtarınız hazır')} note={tx('Bu anahtar yalnızca şimdi gösterilir; güvenli bir yere kaydedin. Kaybederseniz iptal edip yenisini oluşturun.')}>
          <Copyable text={created.key} />
          {created.note && <p className="mt-3 text-[12.5px] text-muted-foreground">{created.note}</p>}
        </Modal>
      )}
      {usage && <ApiKeyUsageModal k={usage} onClose={() => setUsage(null)} />}
    </div>
  )
}

function ChatIntegrations() {
  const list = useQuery({ queryKey: ['integrations'], queryFn: ({ signal }) => governanceApi.integrations(signal) })
  const [form, setForm] = useState<{ kind: string; name: string; webhookUrl: string; events: string[]; signingSecret: string } | null>(null)
  const create = useAction(() => governanceApi.createIntegration({ ...form!, signingSecret: form!.signingSecret || null, isEnabled: true }), { success: tx('Kanal bağlandı'), invalidate: [['integrations']], onDone: () => setForm(null) })
  const test = useAction((id: string) => governanceApi.testIntegration(id), { success: (r) => (r.ok ? tx('Test mesajı gönderildi') : tx('Gönderilemedi (HTTP {0})', [r.lastStatus ?? '—'])), invalidate: [['integrations']] })
  const del = useAction((id: string) => governanceApi.deleteIntegration(id), { success: tx('Kaldırıldı'), invalidate: [['integrations']] })
  const confirm = useConfirm()
  const askDelete = async (i: { id: string; kind: string; name: string }) => {
    if (await confirm({
      title: tx('“{0} · {1}” kanal bağlantısı kaldırılsın mı?', [i.kind, i.name]),
      note: tx('Bu kanala artık olay duyurusu gönderilmez. Yeniden bağlamak için adresi tekrar girmeniz gerekir.'),
      action: tx('Kaldır'),
    })) del.mutate(i.id)
  }
  return (
    <div className="space-y-5">
      <InfoNote>{tx('Bir kanala olay duyurusu (ör. “yeni çalışan katıldı”) göndermek için. Slack: “Incoming Webhooks” ile kanal adresi alın. Teams: kanalda')}{' '}<b>{tx('Workflows › “Post to a channel when a webhook request is received”')}</b>{' '}{tx('şablonuyla akış oluşturup adresini girin (eski “Gelen Web Kancası” bağlayıcıları Microsoft tarafından kapatıldı). Kişiye özel bildirim ve onay düğmeleri için “Sohbet uygulamaları” sekmesini kullanın.')}</InfoNote>
      <Button onClick={() => setForm({ kind: 'Slack', name: '#ik-duyurular', webhookUrl: 'https://hooks.slack.com/services/…', events: ['employee.hired', 'leave.approved'], signingSecret: '' })}><Plus className="size-4" />{' '}{tx('Kanal bağla')}</Button>
      {list.isPending ? <RowsSkeleton /> : (list.data ?? []).length === 0 ? <EmptyState icon={MessageSquare} title={tx('Bağlı kanal yok')} /> : (
        <div className="grid gap-4 lg:grid-cols-2">
          {list.data!.map((i) => (
            <div key={i.id} className="surface rounded-2xl border border-border p-4">
              <div className="flex items-center justify-between"><p className="text-[14px] font-semibold">{i.kind} · {i.name}</p><StatusBadge tone={i.lastStatus == null ? 'info' : i.lastStatus < 300 ? 'success' : 'danger'}>{i.lastStatus == null ? tx('Denenmedi') : tx('HTTP {0}', [i.lastStatus])}</StatusBadge></div>
              <p className="mt-1 font-mono text-[11.5px] text-muted-foreground">{i.webhookUrl}</p>
              <div className="mt-2 flex flex-wrap gap-1">{i.events.map((e) => <span key={e} className="rounded-full bg-muted px-2 py-0.5 font-mono text-[10.5px]">{e}</span>)}</div>
              {i.kind === 'Slack' && <div className="mt-2 text-[12px] text-muted-foreground">{tx('Komut adresi:')}{' '}<Copyable text={`${window.location.origin}/api/governance/integrations/${i.id}/slack-command`} /></div>}
              <div className="mt-3 flex gap-1.5"><Button size="sm" variant="outline" onClick={() => test.mutate(i.id)}><Send className="size-4" />{' '}{tx('Test')}</Button><Button size="sm" variant="ghost" onClick={() => askDelete(i)} aria-label={tx('Kaldır')}><Trash2 className="size-4" /></Button></div>
            </div>
          ))}
        </div>
      )}
      {form && (
        <Modal open onClose={() => setForm(null)} title={tx('Kanal bağla')} footer={<><Button variant="outline" onClick={() => setForm(null)}>{tx('Vazgeç')}</Button><Button disabled={!form.webhookUrl.startsWith('http')} onClick={() => create.mutate(undefined)}>{tx('Bağla')}</Button></>}>
          <div className="space-y-4">
            <div className="grid grid-cols-2 gap-3"><SelectField label={tx('Platform')} value={form.kind} onChange={(v) => setForm({ ...form, kind: v })} options={[{ value: 'Slack', label: tx('Slack') }, { value: 'Teams', label: tx('Microsoft Teams') }]} /><TextField label={tx('Ad')} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></div>
            <TextField label={tx('Gelen webhook adresi')} value={form.webhookUrl} onChange={(e) => setForm({ ...form, webhookUrl: e.target.value })} />
            {form.kind === 'Slack' && <TextField label={tx('Slack Signing Secret (isteğe bağlı, slash komutu için)')} value={form.signingSecret} onChange={(e) => setForm({ ...form, signingSecret: e.target.value })} />}
            <EventPicker value={form.events} onChange={(v) => setForm({ ...form, events: v })} />
          </div>
        </Modal>
      )}
    </div>
  )
}

const INTEGRATION_TABS = ['webhook', 'teslimat', 'api', 'uygulama', 'sohbet', 'takvim', 'hesap'] as const
type IntegrationTab = (typeof INTEGRATION_TABS)[number]

export function IntegrationsPage() {
  const [rawTab, setTab] = useTabParam<IntegrationTab>('sekme', 'uygulama')
  // Geçersiz ?sekme= değeri boş sayfa yerine varsayılan sekmeye düşer.
  const tab: IntegrationTab = (INTEGRATION_TABS as readonly string[]).includes(rawTab) ? rawTab : 'uygulama'
  return (
    <TenantGate>
      <PageHeader title={tx('Entegrasyonlar')} description={tx('Slack ve Microsoft Teams\'ten onay, Google ve Microsoft 365 takvimleri, Zoom/Teams/Meet toplantıları, kanal bildirimleri, webhook\'lar ve açık API.')} />
      <div className="mb-5"><Tabs label={tx('Entegrasyon')} value={tab} onChange={setTab} tabs={[{ key: 'uygulama', label: tx('Sohbet uygulamaları') }, { key: 'takvim', label: tx('Takvim ve toplantı') }, { key: 'hesap', label: tx('Hesap açma/kapatma') }, { key: 'sohbet', label: tx('Kanal bildirimleri') }, { key: 'webhook', label: tx('Webhook') }, { key: 'teslimat', label: tx('Teslimat hataları') }, { key: 'api', label: tx('API anahtarları') }]} /></div>
      {tab === 'webhook' && <Webhooks />}
      {tab === 'teslimat' && <WebhookDeliveriesPanel />}
      {tab === 'api' && <ApiKeys />}
      {tab === 'uygulama' && <ChatAppsPanel />}
      {tab === 'takvim' && <CalendarProvidersPanel />}
      {tab === 'hesap' && <AccountProvisioningPanel />}
      {tab === 'sohbet' && <ChatIntegrations />}
    </TenantGate>
  )
}
