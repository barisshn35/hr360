import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Check, Coins, Gift, Package, Pencil, Plus, Settings2, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { Checkbox } from '@/components/ui/checkbox'
import { TextAreaField, TextField } from '@/components/ui/field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/states'
import { Tabs, useTabParam } from '@/components/ui/tabs'
import { useConfirm } from '@/components/ui/confirm'
import { PersonSelect, useAction } from '@/features/shared/kit'
import { useAuth } from '@/auth/useAuth'
import { recognitionApi, type Redemption, type RedemptionStatus, type Reward } from '@/api/recognition'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const KEY = ['recognition'] as const
const STATUS_TONE: Record<RedemptionStatus, string> = {
  Pending: 'bg-amber-400/15 text-amber-700 dark:text-amber-300', Fulfilled: 'bg-primary/15 text-primary', Rejected: 'bg-rose-500/10 text-rose-600 dark:text-rose-300',
}
const statusLabel = (s: RedemptionStatus) => ({ Pending: tx('Bekliyor'), Fulfilled: tx('Teslim edildi'), Rejected: tx('Reddedildi') })[s]

function useIsHr() {
  const { roles } = useAuth()
  return roles.some((r) => r === 'hr-admin' || r === 'tenant-admin')
}

function RewardCard({ r, balance, enabled }: { r: Reward; balance: number; enabled: boolean }) {
  const confirm = useConfirm()
  const redeem = useAction(() => recognitionApi.redeem(r.id), { success: tx('Talebiniz alındı; İK ödülü hazırlayınca haber verilir.'), invalidate: [KEY] })
  const out = r.stock === 0
  const short = balance < r.cost
  return (
    <Panel>
      <PanelBody className="flex h-full flex-col gap-3">
        <div className="flex items-start gap-3">
          <span className="grid size-10 shrink-0 place-items-center rounded-xl bg-primary/10 text-primary"><Gift className="size-5" /></span>
          <div className="min-w-0 flex-1">
            <h3 className="text-[14.5px] font-semibold">{r.title}</h3>
            {r.description && <p className="mt-0.5 text-[12.5px] text-muted-foreground">{r.description}</p>}
          </div>
        </div>
        <div className="mt-auto flex items-center gap-2">
          <span className="flex items-center gap-1 text-[15px] font-semibold tabular-nums"><Coins className="size-4 text-amber-500" />{r.cost}</span>
          {r.stock != null && <span className="text-[12px] text-muted-foreground">{out ? tx('Tükendi') : tx('{0} adet kaldı', [r.stock])}</span>}
          <Button size="sm" className="ml-auto" disabled={!enabled || out || short || redeem.isPending}
            onClick={async () => { if (await confirm({ title: tx('{0} puan karşılığında alınsın mı?', [r.cost]), note: r.title, action: tx('Takas et'), destructive: false })) redeem.mutate(undefined) }}>
            {short && !out ? tx('{0} puan eksik', [r.cost - balance]) : tx('Takas et')}
          </Button>
        </div>
      </PanelBody>
    </Panel>
  )
}

function RewardModal({ editing, onClose }: { editing?: Reward; onClose: () => void }) {
  const [f, setF] = useState({ title: editing?.title ?? '', description: editing?.description ?? '', cost: editing?.cost?.toString() ?? '', stock: editing?.stock?.toString() ?? '', active: editing?.active ?? true })
  const body = () => ({ title: f.title.trim(), description: f.description.trim() || null, cost: Number(f.cost), stock: f.stock === '' ? null : Number(f.stock), active: f.active })
  const save = useAction(() => (editing ? recognitionApi.updateReward(editing.id, body()) : recognitionApi.createReward(body())), { success: tx('Kaydedildi.'), invalidate: [KEY], onDone: onClose })
  const ok = f.title.trim().length >= 2 && Number(f.cost) >= 1
  return (
    <Modal open onClose={onClose} title={editing ? tx('Ödülü düzenle') : tx('Yeni ödül')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ok || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Ödül')} value={f.title} maxLength={120} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder={tx('ör. Yarım gün izin, kahve kuponu, kitap')} />
        <TextAreaField label={tx('Açıklama')} value={f.description} maxLength={1000} rows={3} onChange={(e) => setF({ ...f, description: e.target.value })} />
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={tx('Puan değeri')} type="number" min={1} value={f.cost} onChange={(e) => setF({ ...f, cost: e.target.value })} />
          <TextField label={tx('Stok (boşsa sınırsız)')} type="number" min={0} value={f.stock} onChange={(e) => setF({ ...f, stock: e.target.value })} />
        </div>
        <label className="flex cursor-pointer items-center gap-2 text-[13px]">
          <Checkbox checked={f.active} onCheckedChange={(v) => setF({ ...f, active: v === true })} />{tx('Katalogda görünsün')}
        </label>
      </div>
    </Modal>
  )
}

function DecideModal({ r, onClose }: { r: Redemption; onClose: () => void }) {
  const [note, setNote] = useState('')
  const decide = useAction((fulfilled: boolean) => recognitionApi.decide(r.id, fulfilled, note.trim() || null), { success: tx('Kaydedildi.'), invalidate: [KEY], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={r.rewardTitle} note={tx('{0} · {1} puan', [r.name ?? '', r.cost])}
      footer={<>
        <Button variant="outline" disabled={!note.trim() || decide.isPending} onClick={() => decide.mutate(false)}><X className="size-4" />{tx('Reddet ve puanı iade et')}</Button>
        <Button disabled={decide.isPending} onClick={() => decide.mutate(true)}><Check className="size-4" />{tx('Teslim edildi')}</Button>
      </>}>
      <TextAreaField label={tx('Not (ret için zorunlu)')} value={note} maxLength={500} rows={3} onChange={(e) => setNote(e.target.value)} hint={tx('Çalışana bildirimle iletilir.')} />
    </Modal>
  )
}

function Admin() {
  const settings = useQuery({ queryKey: [...KEY, 'settings'], queryFn: ({ signal }) => recognitionApi.settings(signal) })
  const rewards = useQuery({ queryKey: [...KEY, 'rewards', 'all'], queryFn: ({ signal }) => recognitionApi.rewards(true, signal) })
  const pending = useQuery({ queryKey: [...KEY, 'redemptions', 'Pending'], queryFn: ({ signal }) => recognitionApi.redemptions('Pending', signal) })
  const [form, setForm] = useState<{ enabled: boolean; monthlyAllowance: string; maxPerKudos: string } | null>(null)
  const [editing, setEditing] = useState<Reward | 'new' | null>(null)
  const [deciding, setDeciding] = useState<Redemption | null>(null)
  const [grant, setGrant] = useState({ employeeId: '', points: '', note: '' })
  const f = form ?? (settings.data ? { enabled: settings.data.enabled, monthlyAllowance: String(settings.data.monthlyAllowance), maxPerKudos: String(settings.data.maxPerKudos) } : null)
  const save = useAction(() => recognitionApi.saveSettings({ enabled: f!.enabled, monthlyAllowance: Number(f!.monthlyAllowance), maxPerKudos: Number(f!.maxPerKudos) }),
    { success: tx('Ayarlar kaydedildi.'), invalidate: [KEY], onDone: () => setForm(null) })
  const give = useAction(() => recognitionApi.grant(grant.employeeId, Number(grant.points), grant.note.trim()),
    { success: tx('Puan işlendi.'), invalidate: [KEY], onDone: () => setGrant({ employeeId: '', points: '', note: '' }) })
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      <Panel>
        <PanelHead title={<span className="flex items-center gap-2"><Settings2 className="size-4 text-primary" />{tx('Ayarlar')}</span>} />
        <PanelBody className="space-y-4">
          {!f ? <RowsSkeleton rows={2} /> : (
            <>
              <label className="flex cursor-pointer items-start gap-2 text-[13px]">
                <Checkbox checked={f.enabled} onCheckedChange={(v) => setForm({ ...f, enabled: v === true })} className="mt-0.5" />
                <span>{tx('Takdir puanları açık')}<span className="block text-[12px] text-muted-foreground">{tx('Kapalıyken takdir duvarı puansız çalışır; mevcut bakiyeler korunur.')}</span></span>
              </label>
              <div className="grid gap-3 sm:grid-cols-2">
                <TextField label={tx('Kişi başı aylık bütçe')} type="number" min={1} value={f.monthlyAllowance} onChange={(e) => setForm({ ...f, monthlyAllowance: e.target.value })} hint={tx('Her ay yenilenir; devretmez.')} />
                <TextField label={tx('Takdir başına en fazla')} type="number" min={1} value={f.maxPerKudos} onChange={(e) => setForm({ ...f, maxPerKudos: e.target.value })} />
              </div>
              <Button disabled={!form || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
            </>
          )}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Bekleyen talepler')} note={pending.data ? tx('{0} talep', [pending.data.length]) : undefined} />
        <PanelBody>
          {pending.isPending ? <RowsSkeleton rows={3} /> : pending.isError ? <ErrorState onRetry={() => void pending.refetch()} /> : !pending.data.length ? (
            <p className="text-[13px] text-muted-foreground">{tx('Bekleyen talep yok.')}</p>
          ) : (
            <ul className="divide-y divide-border">
              {pending.data.map((r) => (
                <li key={r.id} className="flex items-center gap-3 py-2 text-[13px]">
                  <div className="min-w-0 flex-1"><p className="font-medium">{r.rewardTitle}</p><p className="text-[12px] text-muted-foreground">{r.name} · {formatRelativeToNow(r.createdAt)}</p></div>
                  <span className="tabular-nums">{r.cost}</span>
                  <Button size="sm" variant="outline" onClick={() => setDeciding(r)}>{tx('Sonuçlandır')}</Button>
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      <Panel className="lg:col-span-2">
        <PanelHead title={<span className="flex items-center gap-2"><Package className="size-4 text-primary" />{tx('Ödül kataloğu')}</span>}
          action={<Button size="sm" onClick={() => setEditing('new')}><Plus className="size-4" />{tx('Ödül ekle')}</Button>} />
        <PanelBody>
          {rewards.isPending ? <RowsSkeleton rows={3} /> : rewards.isError ? <ErrorState onRetry={() => void rewards.refetch()} /> : !rewards.data.length ? (
            <p className="text-[13px] text-muted-foreground">{tx('Katalog boş. Yarım gün izin, kahve kuponu, kitap ya da bağış gibi ödüller ekleyin.')}</p>
          ) : (
            <ul className="divide-y divide-border">
              {rewards.data.map((r) => (
                <li key={r.id} className={cn('flex items-center gap-3 py-2 text-[13px]', !r.active && 'opacity-60')}>
                  <span className="min-w-0 flex-1 font-medium">{r.title}{!r.active && <span className="ml-2 text-[11.5px] font-normal text-muted-foreground">{tx('Gizli')}</span>}</span>
                  <span className="text-muted-foreground">{r.stock == null ? tx('Sınırsız') : tx('{0} adet', [r.stock])}</span>
                  <span className="w-14 text-right font-semibold tabular-nums">{r.cost}</span>
                  <Button size="sm" variant="ghost" aria-label={tx('Düzenle')} onClick={() => setEditing(r)}><Pencil className="size-4" /></Button>
                </li>
              ))}
            </ul>
          )}
        </PanelBody>
      </Panel>
      <Panel className="lg:col-span-2">
        <PanelHead title={tx('Puan ekle ya da düş')} note={tx('Yarışma ödülü ya da düzeltme için; gerekçe çalışanın hareketlerinde görünür.')} />
        <PanelBody className="grid items-end gap-3 md:grid-cols-[1fr_140px_1fr_auto]">
          <PersonSelect id="grant-to" label={tx('Çalışan')} value={grant.employeeId} onChange={(v) => setGrant({ ...grant, employeeId: v })} />
          <TextField label={tx('Puan')} type="number" value={grant.points} onChange={(e) => setGrant({ ...grant, points: e.target.value })} placeholder={tx('ör. 50 ya da -20')} />
          <TextField label={tx('Gerekçe')} value={grant.note} maxLength={200} onChange={(e) => setGrant({ ...grant, note: e.target.value })} />
          <Button disabled={!grant.employeeId || !Number(grant.points) || grant.note.trim().length < 3 || give.isPending} onClick={() => give.mutate(undefined)}>{tx('İşle')}</Button>
        </PanelBody>
      </Panel>
      {editing && <RewardModal editing={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
      {deciding && <DecideModal r={deciding} onClose={() => setDeciding(null)} />}
    </div>
  )
}

/** Takdir puanları ve ödül kataloğu (yenilik haritası #25). */
export function RewardsPage() {
  const isHr = useIsHr()
  const [tab, setTab] = useTabParam<'katalog' | 'taleplerim' | 'hareketler' | 'yonetim'>('sekme', 'katalog')
  const me = useQuery({ queryKey: [...KEY, 'me'], queryFn: ({ signal }) => recognitionApi.me(signal) })
  const rewards = useQuery({ queryKey: [...KEY, 'rewards'], queryFn: ({ signal }) => recognitionApi.rewards(false, signal), enabled: tab === 'katalog' })
  const mine = useQuery({ queryKey: [...KEY, 'redemptions', 'me'], queryFn: ({ signal }) => recognitionApi.myRedemptions(signal), enabled: tab === 'taleplerim' })
  const enabled = me.data?.enabled === true
  const balance = me.data?.balance ?? 0
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Puanlar ve ödüller')} eyebrow={[tx('Topluluk')]} description={tx('Takdirle gelen puanlarınızı katalogdaki ödüllerle takas edin.')}
        actions={me.data && (
          <div className="flex items-center gap-3 rounded-xl border border-border px-4 py-2">
            <Coins className="size-5 text-amber-500" />
            <div><p className="text-[18px] leading-tight font-semibold tabular-nums">{balance}</p><p className="text-[11.5px] text-muted-foreground">{tx('Bu ay verebileceğiniz: {0}', [me.data.remaining])}</p></div>
          </div>
        )} />
      {me.data && !enabled && <p className="rounded-lg border border-border bg-muted/40 px-4 py-2.5 text-[13px] text-muted-foreground">{isHr ? tx('Takdir puanları kapalı. Yönetim sekmesinden açabilirsiniz.') : tx('Takdir puanları şu an kapalı.')}</p>}
      <Tabs label={tx('Puanlar ve ödüller')} value={tab} onChange={setTab}
        tabs={[{ key: 'katalog', label: tx('Katalog') }, { key: 'taleplerim', label: tx('Taleplerim') }, { key: 'hareketler', label: tx('Hareketler') }, ...(isHr ? [{ key: 'yonetim' as const, label: tx('Yönetim') }] : [])]} />
      {tab === 'katalog' && (rewards.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : rewards.isError ? <ErrorState onRetry={() => void rewards.refetch()} /> : !rewards.data.length ? (
        <EmptyState icon={Gift} title={tx('Katalogda ödül yok')} detail={isHr ? tx('Yönetim sekmesinden ödül ekleyin.') : tx('İK ödül eklediğinde burada görünecek.')} />
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">{rewards.data.map((r) => <RewardCard key={r.id} r={r} balance={balance} enabled={enabled} />)}</div>
      ))}
      {tab === 'taleplerim' && (mine.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : mine.isError ? <ErrorState onRetry={() => void mine.refetch()} /> : !mine.data.length ? (
        <EmptyState icon={Gift} title={tx('Henüz talebiniz yok')} />
      ) : (
        <Panel><PanelBody>
          <ul className="divide-y divide-border">
            {mine.data.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-3 py-2.5 text-[13px]">
                <div className="min-w-0 flex-1"><p className="font-medium">{r.rewardTitle}</p><p className="text-[12px] text-muted-foreground">{formatDateTime(r.createdAt)}{r.note ? ` · ${r.note}` : ''}</p></div>
                <span className="tabular-nums">{r.cost}</span>
                <span className={cn('rounded-full px-2 py-0.5 text-[11.5px]', STATUS_TONE[r.status])}>{statusLabel(r.status)}</span>
              </li>
            ))}
          </ul>
        </PanelBody></Panel>
      ))}
      {tab === 'hareketler' && (me.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : me.isError ? <ErrorState onRetry={() => void me.refetch()} /> : !me.data.history.length ? (
        <EmptyState icon={Coins} title={tx('Henüz puan hareketi yok')} detail={tx('Size puanlı takdir gönderildiğinde burada görünür.')} />
      ) : (
        <Panel><PanelBody>
          <ul className="divide-y divide-border">
            {me.data.history.map((h) => (
              <li key={h.id} className="flex items-center gap-3 py-2 text-[13px]">
                <span className="min-w-0 flex-1">{h.label}</span>
                <span className="text-[12px] text-muted-foreground">{formatRelativeToNow(h.createdAt)}</span>
                <span className={cn('w-14 text-right font-semibold tabular-nums', h.delta > 0 ? 'text-primary' : 'text-rose-500')}>{h.delta > 0 ? `+${h.delta}` : h.delta}</span>
              </li>
            ))}
          </ul>
        </PanelBody></Panel>
      ))}
      {tab === 'yonetim' && isHr && <Admin />}
    </div>
  )
}
