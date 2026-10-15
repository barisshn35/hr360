import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, BellRing, CheckCircle2, ClipboardCheck, Fingerprint, Play, Search, ShieldAlert, ShieldCheck, Unlock, UserCheck, UserX } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { roleLabels, type Role } from '@/auth/roles'
import {
  dataProtectionApi, payrollCapableCount, removableAccess, reviewProgress,
  type AccessReviewItem, type AccessReviewSummary, type SecuritySettings, type TraceLookup,
} from '@/api/dataProtection'
import { tenantApi } from '@/api/tenant'
import { formatDate, formatDateTime } from '@/lib/format'
import { PersonSelect, errMsg, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/* ======================================================================
 * Güvenlik dalgası 2B — Yönetim › Veri koruma (İK / şirket yöneticisi) ve
 * Yönetim › Erişim gözden geçirme (yönetici: ekibinin rolleri).
 * ==================================================================== */

const accessLabel = (code: string) => roleLabels[code as Role] ?? code

function AccessChips({ roles, permissions, strike = [] }: { roles: string[]; permissions: string[]; strike?: string[] }) {
  const all = [...roles, ...permissions]
  if (all.length === 0) return <span className="text-muted-foreground">—</span>
  return (
    <span className="flex flex-wrap gap-1">
      {all.map((r) => (
        <span key={r} className={`rounded-md border border-border px-1.5 py-px text-[11.5px] ${strike.includes(r) ? 'text-destructive line-through' : ''}`}>{accessLabel(r)}</span>
      ))}
    </span>
  )
}

/* ---------------------------------------------------------------- karar penceresi (yönetici / İK) */

function DecideModal({ item, onClose }: { item: AccessReviewItem; onClose: () => void }) {
  const options = removableAccess(item)
  const [sel, setSel] = useState<string[]>(item.removeRoles.length ? item.removeRoles : options)
  const [note, setNote] = useState(item.note ?? '')
  const decide = useAction(() => dataProtectionApi.decide(item.id, { decision: 'Remove', removeRoles: sel, note: note || undefined }),
    { success: tx('Kaldırma kararı kaydedildi'), invalidate: [['access-review']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Kaldırılsın: {0}', [item.employeeName ?? '—'])}
      note={tx('Seçtiğiniz roller/izinler İK onayıyla kaldırılır. Temel çalışan erişimi bu ekrandan kaldırılmaz.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button variant="destructive" disabled={sel.length === 0 || decide.isPending} onClick={() => decide.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      <div className="space-y-3">
        {options.map((o) => (
          <label key={o} className="flex items-center gap-2 text-[13px]">
            <Checkbox checked={sel.includes(o)} onCheckedChange={(v) => setSel(v === true ? [...sel, o] : sel.filter((x) => x !== o))} />
            {accessLabel(o)}
          </label>
        ))}
        <TextAreaField label={tx('Not (isteğe bağlı)')} rows={3} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
      </div>
    </Modal>
  )
}

function ReviewRows({ rows, showReviewer = false, reviewId, canApply = false }: {
  rows: AccessReviewItem[]; showReviewer?: boolean; reviewId?: string; canApply?: boolean
}) {
  const [removing, setRemoving] = useState<AccessReviewItem | null>(null)
  const keep = useAction((id: string) => dataProtectionApi.decide(id, { decision: 'Keep' }), { success: tx('Uygun olarak işaretlendi'), invalidate: [['access-review']] })
  const apply = useAction((id: string) => dataProtectionApi.apply(reviewId!, id), {
    success: (r) => (r.applied ? tx('Kaldırıldı') : tx('Bazı kaldırmalar yapılamadı: {0}', [r.result])), invalidate: [['access-review']],
  })
  return (
    <div className="divide-y divide-border">
      {rows.map((i) => (
        <div key={i.id} className="flex flex-wrap items-center gap-3 py-2.5 text-[13px]">
          <div className="min-w-48 flex-1">
            <div className="font-medium">{i.employeeName ?? '—'}</div>
            <div className="text-[12px] text-muted-foreground">{[i.position, i.department, i.employeeId ? null : tx('Çalışan kaydı yok')].filter(Boolean).join(' · ') || '—'}
              {showReviewer && <> · {tx('Gözden geçiren')}: {i.reviewerName ?? tx('İK')}</>}</div>
          </div>
          <div className="min-w-40 flex-1"><AccessChips roles={i.roles} permissions={i.permissions} strike={i.decision === 'Remove' ? i.removeRoles : []} /></div>
          <div className="flex items-center gap-2">
            {i.decision === 'Keep' && <StatusBadge tone="success">{tx('Uygun')}</StatusBadge>}
            {i.decision === 'Remove' && <StatusBadge tone={i.appliedAt ? 'neutral' : 'danger'}>{i.appliedAt ? tx('Kaldırıldı') : tx('Kaldırılsın')}</StatusBadge>}
            {!i.decision && <StatusBadge tone="warning">{tx('Bekliyor')}</StatusBadge>}
            {!i.appliedAt && !canApply && (
              <>
                <Button size="sm" variant="outline" disabled={keep.isPending} onClick={() => keep.mutate(i.id)}><UserCheck className="size-4" /> {tx('Uygun')}</Button>
                <Button size="sm" variant="outline" onClick={() => setRemoving(i)}><UserX className="size-4" /> {tx('Kaldırılsın')}</Button>
              </>
            )}
            {canApply && i.decision === 'Remove' && !i.appliedAt && (
              <Button size="sm" variant="destructive" disabled={apply.isPending} onClick={() => apply.mutate(i.id)}>{tx('Kaldırmayı uygula')}</Button>
            )}
          </div>
          {i.applyResult && !i.appliedAt && <p className="w-full text-[12px] text-destructive">{i.applyResult}</p>}
        </div>
      ))}
      {removing && <DecideModal item={removing} onClose={() => setRemoving(null)} />}
    </div>
  )
}

/** Yönetici (ve İK): karar vereceği satırlar. */
function MyReviewList() {
  const q = useQuery({ queryKey: ['access-review', 'mine'], queryFn: ({ signal }) => dataProtectionApi.mine(signal) })
  if (q.isPending) return <RowsSkeleton rows={4} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const rows = q.data.items
  if (rows.length === 0) return <EmptyState icon={ClipboardCheck} title={tx('Gözden geçirmeniz gereken kimse yok')} detail={tx('İK bir erişim gözden geçirme kampanyası başlattığında ekibiniz burada listelenir.')} />
  const pending = rows.filter((r) => !r.item.decision).length
  return (
    <Panel>
      <PanelHead title={rows[0].review} note={tx('{0} kişi · {1} karar bekliyor · son tarih {2}', [rows.length, pending, rows[0].dueDate ? formatDate(rows[0].dueDate) : '—'])} />
      <PanelBody><ReviewRows rows={rows.map((r) => r.item)} /></PanelBody>
    </Panel>
  )
}

/** /panel/erisim-gozden-gecirme — yöneticinin kendi listesi. */
export function AccessReviewMyPage() {
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Erişim gözden geçirme')} description={tx('Ekibinizdeki kişilerin sistem rolleri ve ek izinleri hâlâ gerekli mi? Her kişi için "uygun" ya da "kaldırılsın" seçin.')} />
      <InfoNote>{tx('Yalnızca rol ve izin adları gösterilir. Kaldırma kararlarını İK uygular; her karar denetim kaydına yazılır.')}</InfoNote>
      <MyReviewList />
    </div>
  )
}

/* ---------------------------------------------------------------- İK: kampanyalar */

function CampaignDetail({ r, onClose }: { r: AccessReviewSummary; onClose: () => void }) {
  const q = useQuery({ queryKey: ['access-review', 'detail', r.id], queryFn: ({ signal }) => dataProtectionApi.review(r.id, signal) })
  const remind = useAction(() => dataProtectionApi.remind(r.id), { success: (x) => tx('{0} kişiye hatırlatma gönderildi', [x.notified]) })
  const confirm = useConfirm()
  const close = useAction(() => dataProtectionApi.closeReview(r.id), { success: tx('Kampanya kapatıldı'), invalidate: [['access-review']], onDone: onClose })
  return (
    <Modal open size="lg" onClose={onClose} title={r.title}
      note={tx('Yöneticiler kendi ekiplerine karar verir; departman başı olmayanların kararı İK\'dadır. "Kaldırılsın" kararlarını buradan uygulayın.')}
      footer={<>
        {r.status === 'Open' && <Button variant="outline" disabled={remind.isPending} onClick={() => remind.mutate(undefined)}><BellRing className="size-4" /> {tx('Hatırlat')}</Button>}
        {r.status === 'Open' && <Button variant="outline" disabled={close.isPending} onClick={async () => {
          if (await confirm({ title: tx('Kampanya kapatılsın mı?'), note: tx('Karar bekleyen satırlar kapanışta olduğu gibi kalır.'), action: tx('Kapat') })) close.mutate(undefined)
        }}>{tx('Kampanyayı kapat')}</Button>}
        <Button onClick={onClose}>{tx('Tamam')}</Button>
      </>}>
      {q.isPending ? <RowsSkeleton rows={5} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : (
        <ReviewRows rows={q.data.items} showReviewer reviewId={r.id} canApply />
      )}
    </Modal>
  )
}

function NewCampaign({ onClose }: { onClose: () => void }) {
  const [title, setTitle] = useState('')
  const [due, setDue] = useState(() => new Date(Date.now() + 14 * 86400000).toISOString().slice(0, 10))
  const create = useAction(() => dataProtectionApi.createReview({ title: title.trim() || undefined, dueDate: due }), {
    success: (r) => tx('{0} kişilik kampanya başlatıldı; yöneticilere bildirim gitti', [r.items]), invalidate: [['access-review']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={tx('Yeni erişim gözden geçirme')}
      note={tx('Rolü ya da ek izni olan herkes listelenir ve departman başına gönderilir. Önerilen sıklık: üç ayda bir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={create.isPending} onClick={() => create.mutate(undefined)}><Play className="size-4" /> {tx('Başlat')}</Button></>}>
      <div className="space-y-3">
        <TextField label={tx('Başlık (boşsa çeyrek adı)')} maxLength={120} value={title} onChange={(e) => setTitle(e.target.value)} />
        <TextField label={tx('Son tarih')} type="date" value={due} onChange={(e) => setDue(e.target.value)} />
      </div>
    </Modal>
  )
}

function Campaigns() {
  const q = useQuery({ queryKey: ['access-review', 'list'], queryFn: ({ signal }) => dataProtectionApi.reviews(signal) })
  const [creating, setCreating] = useState(false)
  const [open, setOpen] = useState<AccessReviewSummary | null>(null)
  const hasOpen = q.data?.items.some((r) => r.status === 'Open') ?? false
  return (
    <div className="space-y-4">
      {q.data?.overdue && !hasOpen && <InfoNote>{tx('Son üç ayda erişim gözden geçirmesi yapılmadı. Yeni bir kampanya başlatın.')}</InfoNote>}
      <Panel>
        <PanelHead title={tx('Kampanyalar')} note={tx('Yöneticiler ekiplerinin rol ve izinlerini yeniden onaylar.')}
          action={<Button size="sm" disabled={hasOpen} onClick={() => setCreating(true)}><Play className="size-4" /> {tx('Kampanya başlat')}</Button>} />
        <PanelBody>
          {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : q.data.items.length === 0 ? (
            <EmptyState icon={ClipboardCheck} title={tx('Henüz kampanya yok')} detail={tx('İlk erişim gözden geçirmesini başlatın.')} />
          ) : (
            <div className="divide-y divide-border">
              {q.data.items.map((r) => (
                <button key={r.id} type="button" onClick={() => setOpen(r)} className="flex w-full flex-wrap items-center gap-3 py-2.5 text-left text-[13px] hover:bg-muted/30">
                  <span className="min-w-48 flex-1 font-medium">{r.title}</span>
                  <StatusBadge tone={r.status === 'Open' ? 'info' : 'neutral'}>{r.status === 'Open' ? tx('Açık') : tx('Kapandı')}</StatusBadge>
                  <span className="tabular text-muted-foreground">{tx('%{0} tamamlandı ({1}/{2})', [reviewProgress(r), r.decided, r.total])}</span>
                  <span className="text-muted-foreground">{tx('{0} kaldırma · {1} uygulandı', [r.removals, r.applied])}</span>
                  <span className="text-muted-foreground">{r.dueDate ? formatDate(r.dueDate) : '—'}</span>
                </button>
              ))}
            </div>
          )}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Benim kararlarım')} note={tx('Departman başı olmayan kişiler İK tarafından gözden geçirilir.')} />
        <PanelBody><MyReviewList /></PanelBody>
      </Panel>
      {creating && <NewCampaign onClose={() => setCreating(false)} />}
      {open && <CampaignDetail r={open} onClose={() => setOpen(null)} />}
    </div>
  )
}

/* ---------------------------------------------------------------- İK: uyarılar */

function Alerts() {
  const q = useQuery({ queryKey: ['data-protection', 'alerts'], queryFn: ({ signal }) => dataProtectionApi.alerts(signal) })
  const ack = useAction((a: { id: string; unblock: boolean }) => dataProtectionApi.ackAlert(a.id, a.unblock), { success: tx('Kaydedildi'), invalidate: [['data-protection', 'alerts']] })
  return (
    <Panel>
      <PanelHead title={tx('Toplu görüntüleme uyarıları')} note={tx('Kısa sürede çok sayıda farklı çalışan kaydı açan kullanıcılar (olası veri sızdırma). Son 90 gün.')} />
      <PanelBody>
        {q.isPending ? <RowsSkeleton rows={3} /> : q.isError ? <ErrorState message={errMsg(q.error)} /> : q.data.length === 0 ? (
          <EmptyState icon={ShieldCheck} title={tx('Uyarı yok')} detail={tx('Eşiği aşan bir görüntüleme olmadı.')} />
        ) : (
          <div className="divide-y divide-border">
            {q.data.map((a) => (
              <div key={a.id} className="flex flex-wrap items-center gap-3 py-2.5 text-[13px]">
                <ShieldAlert className="size-4 text-destructive" />
                <span className="min-w-48 flex-1"><strong>{a.userName ?? '—'}</strong> · {tx('{0} dakikada {1} farklı kayıt (eşik {2})', [a.windowMinutes, a.distinctCount, a.threshold])}</span>
                <span className="text-muted-foreground">{formatDateTime(a.detectedAt)}</span>
                {a.blocked && <StatusBadge tone="danger">{tx('Engelli: {0}', [formatDateTime(a.blockedUntil)])}</StatusBadge>}
                {a.acknowledgedAt ? <StatusBadge tone="success">{tx('İncelendi: {0}', [a.acknowledgedBy ?? '—'])}</StatusBadge> : (
                  <Button size="sm" variant="outline" disabled={ack.isPending} onClick={() => ack.mutate({ id: a.id, unblock: false })}><CheckCircle2 className="size-4" /> {tx('İncelendi')}</Button>
                )}
                {a.blocked && <Button size="sm" variant="outline" disabled={ack.isPending} onClick={() => ack.mutate({ id: a.id, unblock: true })}><Unlock className="size-4" /> {tx('Engeli kaldır')}</Button>}
              </div>
            ))}
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ---------------------------------------------------------------- İK: iz kodu */

function TraceSearch() {
  const [code, setCode] = useState('')
  const [hit, setHit] = useState<TraceLookup | null>(null)
  const find = useAction(() => dataProtectionApi.findTrace(code.trim()), { onDone: setHit })
  return (
    <Panel>
      <PanelHead title={tx('İz kodu sorgula')} note={tx('Yazdırılan/indirilen çıktılardaki filigran: "ad soyad · tarih saat · iz kodu". Sızan bir belgedeki kodu girin; kimin, ne zaman, hangi çıktıyı aldığı denetim kaydından bulunur.')} />
      <PanelBody className="space-y-3">
        <form className="flex flex-wrap items-end gap-2" onSubmit={(e) => { e.preventDefault(); setHit(null); find.mutate(undefined) }}>
          <TextField label={tx('İz kodu')} placeholder="K7P2-MX9Q" maxLength={12} value={code} onChange={(e) => setCode(e.target.value)} className="w-48" />
          <Button type="submit" disabled={code.trim().length < 8 || find.isPending}><Search className="size-4" /> {tx('Sorgula')}</Button>
        </form>
        {hit && (
          <dl className="grid grid-cols-[10rem_1fr] gap-x-3 gap-y-1 rounded-xl border border-border p-3 text-[13px]">
            <dt className="text-muted-foreground">{tx('İndiren')}</dt><dd className="font-medium">{hit.userName ?? '—'}</dd>
            <dt className="text-muted-foreground">{tx('Zaman')}</dt><dd>{formatDateTime(hit.occurredAt)}</dd>
            <dt className="text-muted-foreground">{tx('Çıktı')}</dt><dd>{String(hit.changes.kind ?? hit.changes.field ?? hit.entityType)}{hit.changes.format ? ` · ${String(hit.changes.format)}` : ''}{typeof hit.changes.rows === 'number' ? ` · ${tx('{0} satır', [hit.changes.rows])}` : ''}</dd>
            <dt className="text-muted-foreground">{tx('Denetim kaydı')}</dt><dd className="tabular">#{hit.auditId} · {hit.service}</dd>
          </dl>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ---------------------------------------------------------------- ayarlar */

function SettingsForm({ s }: { s: SecuritySettings }) {
  const [f, setF] = useState({ payrollSod: s.payrollSod, reason: '', threshold: String(s.massViewThreshold), window: String(s.massViewWindowMinutes), block: s.massViewBlock })
  const [recipients, setRecipients] = useState<string[]>(s.alertRecipients.map((r) => r.id))
  const [pick, setPick] = useState('')
  useEffect(() => { if (pick && !recipients.includes(pick)) { setRecipients([...recipients, pick]); setPick('') } }, [pick, recipients])
  const members = useQuery({ queryKey: ['tenant', 'members'], queryFn: ({ signal }) => tenantApi.members(signal), enabled: s.canEdit, staleTime: 60_000 })
  const capable = members.data ? payrollCapableCount(members.data) : null
  const save = useAction(() => dataProtectionApi.saveSettings({
    payrollSod: f.payrollSod, payrollSodReason: f.reason || null, massViewThreshold: Number(f.threshold), massViewWindowMinutes: Number(f.window),
    massViewBlock: f.block, alertEmployeeIds: recipients,
  }), { success: tx('Ayarlar kaydedildi'), invalidate: [['data-protection', 'settings']] })
  const disabled = !s.canEdit
  const turningOff = s.payrollSod && !f.payrollSod
  const names = new Map(s.alertRecipients.map((r) => [r.id, r.name]))
  return (
    <div className="space-y-4">
      {disabled && <InfoNote>{tx('Ayarları yalnızca şirket yöneticisi değiştirebilir.')}</InfoNote>}
      <Panel>
        <PanelHead title={tx('Bordro görevler ayrılığı')} note={tx('Bordroyu hazırlayan (hesaplayan ya da elle kalem giren) kişi aynı dönemi kapatamaz; bir çalışanın IBAN\'ını değiştiren kişi o değişikliğin ilk uygulandığı bordroyu kapatamaz.')} />
        <PanelBody className="space-y-3 text-[13px]">
          {capable !== null && capable <= 1 && (
            <p className="flex items-start gap-2 rounded-xl border border-[hsl(var(--warning))]/40 bg-[hsl(var(--warning))]/10 p-3">
              <AlertTriangle className="mt-0.5 size-4 shrink-0" />
              {tx('Şirkette bordroyu hazırlayıp kapatabilecek tek bir kullanıcı var. Kural açıkken bu kişi dönemi kapatamaz; ikinci bir İK kullanıcısı ekleyin ya da kuralı gerekçeyle kapatın.')}
            </p>
          )}
          <label className="flex items-center gap-2">
            <Checkbox disabled={disabled} checked={f.payrollSod} onCheckedChange={(v) => setF({ ...f, payrollSod: v === true })} />
            {tx('Görevler ayrılığını uygula (önerilen)')}
          </label>
          {!f.payrollSod && !turningOff && s.payrollSodReason && <p className="text-muted-foreground">{tx('Kapatma gerekçesi: {0}', [s.payrollSodReason])}</p>}
          {turningOff && <TextAreaField label={tx('Kapatma gerekçesi (en az 10 karakter, denetim kaydına yazılır)')} rows={2} value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} />}
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Toplu görüntüleme uyarısı')} note={tx('Bir kullanıcı kısa sürede eşikten fazla farklı çalışan kaydı açarsa (ayrıntı, hassas alan, dışa aktarım) denetim kaydı yazılır ve alıcılara bildirim gider.')} />
        <PanelBody className="space-y-3 text-[13px]">
          <div className="flex flex-wrap gap-3">
            <TextField label={tx('Eşik (farklı kayıt)')} type="number" min={10} max={1000} disabled={disabled} value={f.threshold} onChange={(e) => setF({ ...f, threshold: e.target.value })} className="w-40" />
            <TextField label={tx('Zaman penceresi (dakika)')} type="number" min={1} max={60} disabled={disabled} value={f.window} onChange={(e) => setF({ ...f, window: e.target.value })} className="w-48" />
          </div>
          <label className="flex items-center gap-2">
            <Checkbox disabled={disabled} checked={f.block} onCheckedChange={(v) => setF({ ...f, block: v === true })} />
            {tx('Uyarıdan sonra hassas alan açmayı {0} dakika engelle', [s.blockMinutes])}
          </label>
          <div className="space-y-2">
            <p className="font-medium">{tx('Uyarı alıcıları')}</p>
            <p className="text-[12px] text-muted-foreground">{tx('Onay ayarlarındaki İK onaycısı her zaman alıcıdır.')}</p>
            <div className="flex flex-wrap gap-1.5">
              {recipients.map((id) => (
                <span key={id} className="flex items-center gap-1 rounded-md border border-border px-2 py-0.5">
                  {names.get(id) ?? id.slice(0, 8)}
                  {!disabled && <button type="button" aria-label={tx('Kaldır')} className="text-muted-foreground hover:text-destructive" onClick={() => setRecipients(recipients.filter((x) => x !== id))}>×</button>}
                </span>
              ))}
            </div>
            {!disabled && <div className="max-w-sm"><PersonSelect label={tx('Alıcı ekle')} value={pick} onChange={setPick} exclude={recipients} /></div>}
          </div>
        </PanelBody>
      </Panel>
      {!disabled && <Button disabled={save.isPending || (turningOff && f.reason.trim().length < 10)} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>}
      {s.updatedAt && <p className="text-[12px] text-muted-foreground">{tx('Son değişiklik: {0} ({1})', [formatDateTime(s.updatedAt), s.updatedBy ?? '—'])}</p>}
    </div>
  )
}

function Settings() {
  const q = useQuery({ queryKey: ['data-protection', 'settings'], queryFn: ({ signal }) => dataProtectionApi.settings(signal) })
  if (q.isPending) return <RowsSkeleton rows={4} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  return <SettingsForm key={q.data.updatedAt ?? 'default'} s={q.data} />
}

type TabKey = 'gozden-gecirme' | 'uyarilar' | 'iz-kodu' | 'ayarlar'

/** /panel/veri-koruma — İK ve şirket yöneticisi. */
export function DataProtectionPage() {
  const [tab, setTab] = useTabParam<TabKey>('sekme', 'gozden-gecirme')
  const { roles } = useAuth()
  const isTenantAdmin = roles.includes('tenant-admin')
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Veri koruma')} description={tx('Erişim gözden geçirme, toplu görüntüleme uyarıları, filigran iz kodu ve görevler ayrılığı.')} />
      <Tabs label={tx('Veri koruma bölümleri')} value={tab} onChange={setTab} tabs={[
        { key: 'gozden-gecirme', label: tx('Erişim gözden geçirme') },
        { key: 'uyarilar', label: tx('Uyarılar') },
        { key: 'iz-kodu', label: tx('İz kodu') },
        { key: 'ayarlar', label: isTenantAdmin ? tx('Ayarlar') : tx('Ayarlar (salt okunur)') },
      ]} />
      {tab === 'gozden-gecirme' && <Campaigns />}
      {tab === 'uyarilar' && <Alerts />}
      {tab === 'iz-kodu' && <><TraceSearch /><InfoNote><Fingerprint className="mr-1 inline size-4" />{tx('Bordro pusulası, belge, sertifika ve tablo dışa aktarımlarına indiren kişinin filigranı eklenir. Banka/SGK/muhasebe dosyaları dış sistemlerin biçiminde olduğundan içeriğe dokunulmaz; iz kodu yalnızca denetim kaydında tutulur.')}</InfoNote></>}
      {tab === 'ayarlar' && <Settings />}
    </div>
  )
}
