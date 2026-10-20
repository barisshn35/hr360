import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CalendarDays, CalendarPlus, Clock, HandHeart, MapPin, Pencil, Plus, Users, Video, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/states'
import { Tabs, useTabParam } from '@/components/ui/tabs'
import { StatCard } from '@/components/ui/stat-card'
import { useConfirm } from '@/components/ui/confirm'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useAction } from '@/features/shared/kit'
import { useAuth } from '@/auth/useAuth'
import { eventKindLabels, eventsApi, type CompanyEvent, type EventInput, type EventKind } from '@/api/events'
import { formatDateTime, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

const KEY = ['events'] as const
const KINDS: EventKind[] = ['Social', 'Training', 'Volunteer', 'Sports', 'Other']
const TONE: Record<EventKind, string> = {
  Social: 'bg-amber-500/15 text-amber-700 dark:text-amber-300', Training: 'bg-sky-500/15 text-sky-700 dark:text-sky-300',
  Volunteer: 'bg-primary/15 text-primary', Sports: 'bg-violet-500/15 text-violet-700 dark:text-violet-300', Other: 'bg-muted text-muted-foreground',
}

/** ISO → datetime-local alanının beklediği yerel "YYYY-MM-DDTHH:mm". */
function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
}
const fromLocal = (v: string) => (v ? new Date(v).toISOString() : null)

function useIsHr() {
  const { roles } = useAuth()
  return roles.some((r) => r === 'hr-admin' || r === 'tenant-admin')
}

function EventModal({ editing, onClose }: { editing?: CompanyEvent; onClose: () => void }) {
  const [f, setF] = useState(() => ({
    title: editing?.title ?? '', description: editing?.description ?? '', kind: editing?.kind ?? ('Social' as EventKind),
    startsAt: toLocalInput(editing?.startsAt), endsAt: toLocalInput(editing?.endsAt), location: editing?.location ?? '', onlineUrl: editing?.onlineUrl ?? '',
    capacity: editing?.capacity?.toString() ?? '', rsvpDeadline: toLocalInput(editing?.rsvpDeadline), volunteerHours: editing?.volunteerHours?.toString() ?? '', announce: !editing,
  }))
  const set = (p: Partial<typeof f>) => setF((x) => ({ ...x, ...p }))
  const body = (): EventInput => ({
    title: f.title.trim(), description: f.description.trim() || null, kind: f.kind, startsAt: fromLocal(f.startsAt)!, endsAt: fromLocal(f.endsAt)!,
    location: f.location.trim() || null, onlineUrl: f.onlineUrl.trim() || null, capacity: f.capacity ? Number(f.capacity) : null,
    rsvpDeadline: fromLocal(f.rsvpDeadline), volunteerHours: f.kind === 'Volunteer' && f.volunteerHours ? Number(f.volunteerHours.replace(',', '.')) : null, announce: f.announce,
  })
  const save = useAction(() => (editing ? eventsApi.update(editing.id, body()) : eventsApi.create(body())), {
    success: editing ? tx('Etkinlik güncellendi.') : tx('Etkinlik yayımlandı.'), invalidate: [KEY], onDone: onClose,
  })
  const ok = f.title.trim().length >= 3 && f.startsAt && f.endsAt && f.endsAt > f.startsAt
  const labels = eventKindLabels()
  return (
    <Modal open onClose={onClose} title={editing ? tx('Etkinliği düzenle') : tx('Yeni etkinlik')}
      note={tx('Kontenjan doluysa katılmak isteyenler bekleme listesine girer; yer açılınca sıradaki otomatik alınır ve haber verilir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!ok || save.isPending} onClick={() => save.mutate(undefined)}>{editing ? tx('Kaydet') : tx('Yayımla')}</Button></>}>
      <div className="space-y-4">
        <TextField label={tx('Başlık')} value={f.title} maxLength={120} onChange={(e) => set({ title: e.target.value })} placeholder={tx('ör. Yılsonu kahvaltısı')} />
        <div className="grid gap-3 md:grid-cols-2">
          <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => set({ kind: v as EventKind })} options={KINDS.map((k) => ({ value: k, label: labels[k] }))} />
          <TextField label={tx('Kontenjan (boşsa sınırsız)')} type="number" min={1} value={f.capacity} onChange={(e) => set({ capacity: e.target.value })} />
          <TextField label={tx('Başlangıç')} type="datetime-local" value={f.startsAt} onChange={(e) => set({ startsAt: e.target.value })} />
          <TextField label={tx('Bitiş')} type="datetime-local" value={f.endsAt} onChange={(e) => set({ endsAt: e.target.value })} />
          <TextField label={tx('Yer')} value={f.location} maxLength={200} onChange={(e) => set({ location: e.target.value })} />
          <TextField label={tx('Çevrim içi bağlantı')} value={f.onlineUrl} maxLength={500} onChange={(e) => set({ onlineUrl: e.target.value })} placeholder="https://" />
          <TextField label={tx('Son katılım (isteğe bağlı)')} type="datetime-local" value={f.rsvpDeadline} onChange={(e) => set({ rsvpDeadline: e.target.value })} />
          {f.kind === 'Volunteer' && (
            <TextField label={tx('Gönüllülük saati')} inputMode="decimal" value={f.volunteerHours} onChange={(e) => set({ volunteerHours: e.target.value })}
              hint={tx('Boşsa etkinlik süresi yazılır. Saat, katılımı onaylanan kişilere işlenir.')} />
          )}
        </div>
        <TextAreaField label={tx('Açıklama')} value={f.description} maxLength={2000} rows={4} onChange={(e) => set({ description: e.target.value })} />
        {!editing && (
          <label className="flex cursor-pointer items-center gap-2 text-[13px]">
            <Checkbox checked={f.announce} onCheckedChange={(v) => set({ announce: v === true })} />
            {tx('Herkese bildirim gönder')}
          </label>
        )}
      </div>
    </Modal>
  )
}

/** İK: katılımcılar ve (etkinlik başladıktan sonra) katılım işaretleme. */
function AttendeesModal({ event, onClose }: { event: CompanyEvent; onClose: () => void }) {
  const q = useQuery({ queryKey: [...KEY, 'attendees', event.id], queryFn: ({ signal }) => eventsApi.attendees(event.id, signal) })
  const [picked, setPicked] = useState<Set<string> | null>(null)
  const started = new Date(event.startsAt).getTime() <= Date.now()
  const sel = picked ?? new Set((q.data ?? []).filter((a) => a.attended).map((a) => a.employeeId))
  const save = useAction(() => eventsApi.attendance(event.id, [...sel]), { success: tx('Katılım kaydedildi.'), invalidate: [KEY], onDone: onClose })
  const toggle = (id: string) => { const n = new Set(sel); if (n.has(id)) n.delete(id); else n.add(id); setPicked(n) }
  return (
    <Modal open onClose={onClose} title={event.title}
      note={started ? tx('Etkinliğe gelenleri işaretleyin.') + (event.kind === 'Volunteer' ? ' ' + tx('Gönüllülük saatleri işaretlenen kişilere yazılır.') : '') : tx('Katılım, etkinlik başladıktan sonra işaretlenebilir.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button>{started && <Button disabled={save.isPending || !q.data?.length} onClick={() => save.mutate(undefined)}>{tx('Katılımı kaydet')}</Button>}</>}>
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : !q.data.length ? (
        <p className="text-[13px] text-muted-foreground">{tx('Henüz katılan yok.')}</p>
      ) : (
        <ul className="divide-y divide-border rounded-lg border border-border">
          {q.data.map((a) => (
            <li key={a.id} className="flex items-center gap-3 px-3 py-2 text-[13px]">
              {started && a.status === 'Going' && <Checkbox checked={sel.has(a.employeeId)} onCheckedChange={() => toggle(a.employeeId)} aria-label={a.name} />}
              <EmployeeAvatar id={a.employeeId} name={a.name} size={26} />
              <span className="flex-1">{a.name}</span>
              <span className={cn('rounded-full px-2 py-0.5 text-[11.5px]', a.status === 'Going' ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground')}>
                {a.status === 'Going' ? tx('Katılıyor') : tx('Bekleme listesi')}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  )
}

function EventCard({ e, isHr, onEdit, onAttendees }: { e: CompanyEvent; isHr: boolean; onEdit: () => void; onAttendees: () => void }) {
  const confirm = useConfirm()
  const rsvp = useAction((going: boolean) => eventsApi.rsvp(e.id, going), { invalidate: [KEY] })
  const cancel = useAction(() => eventsApi.cancel(e.id), { success: tx('Etkinlik iptal edildi; katılanlara haber verildi.'), invalidate: [KEY] })
  const now = Date.now()
  const upcoming = new Date(e.startsAt).getTime() > now
  const closed = !!e.rsvpDeadline && new Date(e.rsvpDeadline).getTime() < now
  const full = e.capacity != null && e.going >= e.capacity
  const cancelled = e.status === 'Cancelled'
  const download = async () => {
    const blob = await eventsApi.ics(e.id)
    if (!blob) return
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = 'etkinlik.ics'; a.click()
    URL.revokeObjectURL(url)
  }
  return (
    <Panel className={cn(cancelled && 'opacity-60')}>
      <PanelBody className="space-y-3">
        <div className="flex flex-wrap items-start gap-2">
          <span className={cn('rounded-full px-2 py-0.5 text-[11.5px] font-medium', TONE[e.kind])}>{eventKindLabels()[e.kind]}</span>
          {cancelled && <span className="rounded-full bg-rose-500/10 px-2 py-0.5 text-[11.5px] text-rose-600 dark:text-rose-300">{tx('İptal edildi')}</span>}
          {e.kind === 'Volunteer' && e.volunteerHours != null && <span className="text-[12px] text-muted-foreground">{tx('{0} saat gönüllülük', [formatNumber(e.volunteerHours)])}</span>}
          {isHr && !cancelled && (
            <div className="ml-auto flex gap-1">
              <Button size="sm" variant="ghost" onClick={onAttendees}><Users className="size-4" />{tx('Katılımcılar')}</Button>
              {upcoming && <Button size="sm" variant="ghost" onClick={onEdit} aria-label={tx('Düzenle')}><Pencil className="size-4" /></Button>}
              {upcoming && (
                <Button size="sm" variant="ghost" aria-label={tx('İptal et')} onClick={async () => {
                  if (await confirm({ title: tx('Etkinlik iptal edilsin mi?'), note: tx('Katılacağını söyleyen herkese bildirim gider.'), action: tx('İptal et') })) cancel.mutate(undefined)
                }}><X className="size-4" /></Button>
              )}
            </div>
          )}
        </div>
        <div>
          <h3 className="text-[15px] font-semibold">{e.title}</h3>
          {e.description && <p className="mt-1 text-[13px] whitespace-pre-line text-muted-foreground">{e.description}</p>}
        </div>
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-[12.5px] text-muted-foreground">
          <span className="flex items-center gap-1"><Clock className="size-3.5" />{formatDateTime(e.startsAt)} – {formatDateTime(e.endsAt)}</span>
          {e.location && <span className="flex items-center gap-1"><MapPin className="size-3.5" />{e.location}</span>}
          {e.onlineUrl && <a href={e.onlineUrl} target="_blank" rel="noopener noreferrer" className="flex items-center gap-1 text-primary hover:underline"><Video className="size-3.5" />{tx('Çevrim içi katıl')}</a>}
          {e.rsvpDeadline && upcoming && <span>{tx('Son katılım: {0}', [formatDateTime(e.rsvpDeadline)])}</span>}
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex -space-x-2">
            {e.attendees.slice(0, 8).map((a) => <EmployeeAvatar key={a.employeeId} id={a.employeeId} name={a.name} size={26} className="ring-2 ring-card" />)}
          </div>
          <span className="text-[12.5px] text-muted-foreground">
            {e.capacity != null ? tx('{0}/{1} kişi', [e.going, e.capacity]) : tx('{0} kişi katılıyor', [e.going])}
            {e.waitlist > 0 && ` · ${tx('{0} bekleme listesinde', [e.waitlist])}`}
          </span>
          {upcoming && !cancelled && (
            <div className="ml-auto flex w-full flex-wrap items-center justify-end gap-2 sm:w-auto">
              <Button size="sm" variant="ghost" onClick={() => void download()}><CalendarPlus className="size-4" />{tx('Takvime ekle')}</Button>
              {e.myStatus ? (
                <>
                  <span className="text-[12.5px] font-medium whitespace-nowrap text-primary">{e.myStatus === 'Going' ? tx('Katılıyorsunuz') : tx('Bekleme listesinde {0}. sıradasınız', [e.myPosition ?? ''])}</span>
                  <Button size="sm" variant="outline" disabled={rsvp.isPending} onClick={() => rsvp.mutate(false)}>{tx('Vazgeç')}</Button>
                </>
              ) : (
                <Button size="sm" disabled={rsvp.isPending || closed} onClick={() => rsvp.mutate(true)}>
                  {closed ? tx('Katılım kapandı') : full ? tx('Bekleme listesine gir') : tx('Katılıyorum')}
                </Button>
              )}
            </div>
          )}
          {!upcoming && e.myStatus === 'Going' && <span className="ml-auto text-[12.5px] text-muted-foreground">{tx('Katıldınız')}</span>}
        </div>
      </PanelBody>
    </Panel>
  )
}

function EventList({ past }: { past: boolean }) {
  const isHr = useIsHr()
  const [editing, setEditing] = useState<CompanyEvent | null>(null)
  const [attendees, setAttendees] = useState<CompanyEvent | null>(null)
  const q = useQuery({ queryKey: [...KEY, past ? 'past' : 'upcoming'], queryFn: ({ signal }) => eventsApi.list(past, signal) })
  if (q.isPending) return <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel>
  if (q.isError) return <ErrorState onRetry={() => void q.refetch()} />
  if (!q.data.length) return <EmptyState icon={CalendarDays} title={past ? tx('Geçmiş etkinlik yok') : tx('Yaklaşan etkinlik yok')} detail={isHr && !past ? tx('Kahvaltı, eğitim, spor ya da gönüllülük etkinliği açabilirsiniz.') : undefined} />
  return (
    <>
      <div className="grid gap-3">{q.data.map((e) => <EventCard key={e.id} e={e} isHr={isHr} onEdit={() => setEditing(e)} onAttendees={() => setAttendees(e)} />)}</div>
      {editing && <EventModal editing={editing} onClose={() => setEditing(null)} />}
      {attendees && <AttendeesModal event={attendees} onClose={() => setAttendees(null)} />}
    </>
  )
}

function Volunteering() {
  const [year, setYear] = useState(new Date().getFullYear())
  const q = useQuery({ queryKey: [...KEY, 'volunteering', year], queryFn: ({ signal }) => eventsApi.volunteering(year, signal) })
  const years = Array.from({ length: 4 }, (_, i) => new Date().getFullYear() - i)
  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <SelectField label={tx('Yıl')} value={String(year)} onChange={(v) => setYear(Number(v))} options={years.map((y) => ({ value: String(y), label: String(y) }))} />
      </div>
      {q.isPending ? <Panel><PanelBody><RowsSkeleton /></PanelBody></Panel> : q.isError ? <ErrorState onRetry={() => void q.refetch()} /> : (
        <>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <StatCard label={tx('Benim saatim')} count={q.data.mine} icon={HandHeart} />
            <StatCard label={tx('Şirket toplamı (saat)')} count={q.data.totalHours} icon={Clock} />
            <StatCard label={tx('Gönüllü sayısı')} count={q.data.volunteers} icon={Users} />
            <StatCard label={tx('Gönüllülük etkinliği')} count={q.data.events} icon={CalendarDays} />
          </div>
          <Panel>
            <PanelBody>
              <h3 className="mb-3 text-[14px] font-semibold">{tx('En çok gönüllü saat')}</h3>
              {!q.data.top.length ? <p className="text-[13px] text-muted-foreground">{tx('Bu yıl henüz onaylanmış gönüllülük saati yok.')}</p> : (
                <ol className="space-y-2">
                  {q.data.top.map((p, i) => (
                    <li key={p.employeeId} className="flex items-center gap-3 text-[13px]">
                      <span className="w-5 text-right tabular-nums text-muted-foreground">{i + 1}</span>
                      <EmployeeAvatar id={p.employeeId} name={p.name} size={26} />
                      <span className="flex-1">{p.name}</span>
                      <span className="text-muted-foreground">{tx('{0} etkinlik', [p.events])}</span>
                      <span className="w-16 text-right font-semibold tabular-nums">{tx('{0} sa', [formatNumber(p.hours)])}</span>
                    </li>
                  ))}
                </ol>
              )}
            </PanelBody>
          </Panel>
        </>
      )}
    </div>
  )
}

/** Etkinlikler (yenilik haritası #28) ve gönüllülük (#30). */
export function EventsPage() {
  const isHr = useIsHr()
  const [tab, setTab] = useTabParam<'yaklasan' | 'gecmis' | 'gonulluluk'>('sekme', 'yaklasan')
  const [creating, setCreating] = useState(false)
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Etkinlikler')} eyebrow={[tx('Topluluk')]} description={tx('Şirket etkinliklerine katılın, takviminize ekleyin; gönüllülük saatlerinizi görün.')}
        actions={isHr ? <Button onClick={() => setCreating(true)}><Plus className="size-4" />{tx('Etkinlik aç')}</Button> : undefined} />
      <Tabs label={tx('Etkinlikler')} value={tab} onChange={setTab}
        tabs={[{ key: 'yaklasan', label: tx('Yaklaşan') }, { key: 'gecmis', label: tx('Geçmiş') }, { key: 'gonulluluk', label: tx('Gönüllülük') }]} />
      {tab === 'gonulluluk' ? <Volunteering /> : <EventList past={tab === 'gecmis'} />}
      {creating && <EventModal onClose={() => setCreating(false)} />}
    </div>
  )
}
