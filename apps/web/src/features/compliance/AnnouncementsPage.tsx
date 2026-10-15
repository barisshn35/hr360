import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { BarChart3, CheckCircle2, Megaphone, Pencil, Plus, Trash2, TimerOff } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { useConfirm } from '@/components/ui/Confirm'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { complianceApi, type ManagedAnnouncement } from '@/api/compliance'
import { formatDateTime } from '@/lib/format'
import { useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'
import { AckStatsView, DepartmentChecklist, RichText } from './shared'

/** ISO zamanını datetime-local alanının beklediği yerel "YYYY-MM-DDTHH:mm" biçimine çevirir. */
function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
}

function NewAnnouncementModal({ onClose, editing }: { onClose: () => void; editing?: ManagedAnnouncement }) {
  const [f, setF] = useState(() => editing
    ? { title: editing.title, body: editing.body, audience: editing.audience, departmentIds: editing.departmentIds, publishAt: toLocalInput(editing.publishAt), expireAt: toLocalInput(editing.expireAt), requiresAck: editing.requiresAck }
    : { title: '', body: '', audience: 'All' as 'All' | 'Departments', departmentIds: [] as string[], publishAt: '', expireAt: '', requiresAck: false })
  const save = useAction(
    () => {
      const input = {
        title: f.title, body: f.body, audience: f.audience, departmentIds: f.departmentIds, requiresAck: f.requiresAck,
        publishAt: f.publishAt ? new Date(f.publishAt).toISOString() : null, expireAt: f.expireAt ? new Date(f.expireAt).toISOString() : null,
      }
      return editing ? complianceApi.updateAnnouncement(editing.id, input) : complianceApi.createAnnouncement(input)
    },
    {
      success: (r) => (r.notified > 0 ? tx('Duyuru yayımlandı; {0} kişiye bildirim gitti', [r.notified]) : editing ? tx('Duyuru güncellendi') : tx('Duyuru kaydedildi')),
      invalidate: [['announcements']], onDone: onClose,
    },
  )
  return (
    <Modal open size="lg" onClose={onClose} title={editing ? tx('Duyuruyu düzenle') : tx('Yeni duyuru')}
      note={editing ? tx('Okuma kayıtları korunur. Bildirimde yalnızca başlık gönderilir; metne kişisel veri yazmayın.') : tx('Bildirimde yalnızca başlık gönderilir; metne kişisel veri yazmayın.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => save.mutate(undefined)} disabled={save.isPending || f.title.trim().length < 3 || !f.body.trim() || (f.audience === 'Departments' && !f.departmentIds.length)}>{editing ? tx('Kaydet') : tx('Yayımla')}</Button></>}>
      <div className="space-y-3">
        <TextField label={tx('Başlık')} value={f.title} maxLength={200} onChange={(e) => setF({ ...f, title: e.target.value })} />
        <TextAreaField label={tx('Metin')} rows={7} value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} hint={tx('Paragraflar için boş satır, kalın için **metin** kullanın.')} />
        <SelectField label={tx('Hedef kitle')} value={f.audience} onChange={(v) => setF({ ...f, audience: v as 'All' | 'Departments' })}
          options={[{ value: 'All', label: tx('Tüm çalışanlar') }, { value: 'Departments', label: tx('Seçili departmanlar') }]} />
        {f.audience === 'Departments' && <DepartmentChecklist value={f.departmentIds} onChange={(ids) => setF({ ...f, departmentIds: ids })} />}
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label={tx('Yayım zamanı (boşsa hemen)')} type="datetime-local" value={f.publishAt} onChange={(e) => setF({ ...f, publishAt: e.target.value })} />
          <TextField label={tx('Bitiş (isteğe bağlı)')} type="datetime-local" value={f.expireAt} onChange={(e) => setF({ ...f, expireAt: e.target.value })} />
        </div>
        <label className="flex items-center gap-2 text-[13px]">
          <Checkbox checked={f.requiresAck} onCheckedChange={(v) => setF({ ...f, requiresAck: v === true })} />
          {tx('Okundu onayı gereksin (okumayanlar listelenir)')}
        </label>
      </div>
    </Modal>
  )
}

function StatsModal({ a, onClose }: { a: ManagedAnnouncement; onClose: () => void }) {
  const q = useQuery({ queryKey: ['announcements', 'stats', a.id], queryFn: ({ signal }) => complianceApi.announcementStats(a.id, signal) })
  return (
    <Modal open onClose={onClose} title={a.title} note={a.requiresAck ? tx('Onay gerektiren duyuru: okumayanlar listelenir.') : tx('Onay gerektirmeyen duyuruda yalnızca sayı gösterilir.')}>
      {q.isPending ? <RowsSkeleton rows={3} /> : q.data && <AckStatsView stats={q.data} listMissing={a.requiresAck} />}
    </Modal>
  )
}

function ManageTab() {
  const q = useQuery({ queryKey: ['announcements', 'manage'], queryFn: ({ signal }) => complianceApi.manageAnnouncements(signal) })
  const [creating, setCreating] = useState(false)
  const [stats, setStats] = useState<ManagedAnnouncement | null>(null)
  const [editing, setEditing] = useState<ManagedAnnouncement | null>(null)
  const confirm = useConfirm()
  const expire = useAction((id: string) => complianceApi.expireAnnouncement(id), { success: tx('Duyuru yayından kaldırıldı'), invalidate: [['announcements']] })
  const remove = useAction((id: string) => complianceApi.deleteAnnouncement(id), { success: tx('Duyuru silindi'), invalidate: [['announcements']] })
  const stateView = { Scheduled: { label: tx('Planlandı'), tone: 'info' as const }, Published: { label: tx('Yayında'), tone: 'success' as const }, Expired: { label: tx('Süresi doldu'), tone: 'neutral' as const } }
  return (
    <Panel>
      <PanelHead title={tx('Duyuru yönetimi')} note={tx('Okuma oranları ve okumayanlar (onay gerektirenlerde).')} action={<Button onClick={() => setCreating(true)}><Plus className="size-4" />{' '}{tx('Yeni duyuru')}</Button>} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton /></div> : !q.data?.length ? <EmptyState icon={Megaphone} title={tx('Henüz duyuru yok')} /> : (
          <ul className="divide-y divide-border">
            {q.data.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{a.title}</span>
                  <span className="block text-[12px] text-muted-foreground">
                    {formatDateTime(a.publishAt)} · {a.audience === 'All' ? tx('Tüm çalışanlar') : tx('{0} departman', [a.departmentIds.length])}
                    {' · '}{a.stats.total === null ? tx('{0} okudu', [a.stats.read]) : tx('{0}/{1} okudu', [a.stats.read, a.stats.total])}
                  </span>
                </span>
                {a.requiresAck && <StatusBadge tone="warning">{tx('Onay gerekli')}</StatusBadge>}
                <StatusBadge tone={stateView[a.state].tone}>{stateView[a.state].label}</StatusBadge>
                <Button size="sm" variant="ghost" onClick={() => setStats(a)}><BarChart3 className="size-4" />{' '}{tx('Okuma')}</Button>
                {a.state !== 'Expired' && <Button size="sm" variant="ghost" onClick={() => expire.mutate(a.id)} aria-label={tx('Yayından kaldır')}><TimerOff className="size-4" /></Button>}
                {a.state !== 'Expired' && <Button size="sm" variant="ghost" onClick={() => setEditing(a)} aria-label={tx('Düzenle')}><Pencil className="size-4" /></Button>}
                <Button size="sm" variant="ghost" onClick={async () => { if (await confirm({ title: tx('Duyuru ve okuma kayıtları silinsin mi?'), note: a.title, action: tx('Sil') })) remove.mutate(a.id) }} aria-label={tx('Sil')}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {creating && <NewAnnouncementModal onClose={() => setCreating(false)} />}
      {editing && <NewAnnouncementModal editing={editing} onClose={() => setEditing(null)} />}
      {stats && <StatsModal a={stats} onClose={() => setStats(null)} />}
    </Panel>
  )
}

function FeedTab() {
  const q = useQuery({ queryKey: ['announcements', 'mine'], queryFn: ({ signal }) => complianceApi.announcements(signal) })
  const read = useAction((id: string) => complianceApi.readAnnouncement(id), { success: tx('Okundu olarak kaydedildi'), invalidate: [['announcements']] })
  if (q.isPending) return <RowsSkeleton />
  if (!q.data?.length) return <EmptyState icon={Megaphone} title={tx('Duyuru yok')} detail={tx('Size yönelik yayında bir duyuru bulunmuyor.')} />
  const pending = q.data.filter((a) => a.requiresAck && !a.read).length
  return (
    <div className="space-y-4">
      {pending > 0 && <InfoNote>{tx('{0} duyuru okundu onayınızı bekliyor.', [pending])}</InfoNote>}
      {q.data.map((a) => (
        <Panel key={a.id}>
          <PanelHead title={a.title} note={formatDateTime(a.publishAt)}
            action={a.read
              ? <StatusBadge tone="success"><CheckCircle2 className="size-3.5" />{' '}{tx('Okudum')}</StatusBadge>
              : <Button size="sm" variant={a.requiresAck ? 'default' : 'outline'} onClick={() => read.mutate(a.id)} disabled={read.isPending}>{tx('Okudum')}</Button>} />
          <PanelBody>
            <RichText text={a.body} />
            {a.requiresAck && !a.read && <p className="mt-3 text-[12px] text-muted-foreground">{tx('Bu duyuru için okuduğunuzu onaylamanız isteniyor. Bu bir rıza beyanı değildir.')}</p>}
          </PanelBody>
        </Panel>
      ))}
    </div>
  )
}

/** /panel/duyurular — Y14. Çalışan okur/onaylar; İK yönetir. */
export function AnnouncementsPage() {
  const { roles } = useAuth()
  const hr = isHr(roles)
  const [tab, setTab] = useTabParam<'feed' | 'manage'>('sekme', 'feed')
  return (
    <>
      <PageHeader title={tx('Duyurular')} description={tx('Şirket duyuruları ve okundu onayları.')} />
      {hr && <div className="mb-5"><Tabs label={tx('Duyuru sekmeleri')} value={tab} onChange={setTab} tabs={[{ key: 'feed', label: tx('Bana yönelik') }, { key: 'manage', label: tx('Yönetim') }]} /></div>}
      {hr && tab === 'manage' ? <ManageTab /> : <FeedTab />}
    </>
  )
}
