import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Copy, Armchair, Building2, ChevronLeft, ChevronRight, DoorOpen, Home, Laptop, MapPin, Plane, Plus, Trash2, Users } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextField } from '@/components/ui/Field'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { engagementApi, presenceLabels, type Booking, type Desk, type PresenceMode } from '@/api/engagement'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useMyEmployeeId } from '@/api/queries'
import { cn } from '@/lib/utils'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { Metric, TenantGate, isoDate, minutesToHHMM, useAction } from '@/features/shared/kit'
import { Checkbox } from '@/components/ui/checkbox'
import { tx, appLocale } from '@/lib/i18n'

const MODE_STYLE: Record<PresenceMode, { cls: string; icon: React.ElementType }> = {
  Office: { cls: 'bg-emerald-500/15 text-emerald-400 border-emerald-500/30', icon: Building2 },
  Remote: { cls: 'bg-sky-500/15 text-sky-400 border-sky-500/30', icon: Home },
  Travel: { cls: 'bg-violet-500/15 text-violet-400 border-violet-500/30', icon: Plane },
  Off: { cls: 'bg-zinc-500/15 text-zinc-400 border-zinc-500/30', icon: Laptop },
  Leave: { cls: 'bg-amber-500/15 text-amber-400 border-amber-500/30', icon: Plane },
  Unknown: { cls: 'border-dashed border-border text-muted-foreground', icon: MapPin },
}
const DAY = new Intl.DateTimeFormat(appLocale, { weekday: 'short', day: 'numeric', month: 'short' })

/** Masa her yerde aynı adla görünür (kutucuk, pencere, çip): kod (K1-M06); oda kendi adıyla. */
const deskLabel = (d: Desk) => (d.kind === 'Desk' ? d.code : d.name)

function startOfWeek(d: Date) {
  const x = new Date(d)
  x.setDate(x.getDate() - ((x.getDay() + 6) % 7))
  return x
}

function PresenceBoard() {
  const [anchor, setAnchor] = useState(() => startOfWeek(new Date()))
  const from = isoDate(anchor)
  const to = isoDate(new Date(anchor.getTime() + 4 * 86400000))
  const { employeeId } = useMyEmployeeId()
  const q = useQuery({ queryKey: ['presence', from], queryFn: ({ signal }) => engagementApi.presence(from, to, signal) })
  const set = useAction((v: { date: string; mode: PresenceMode }) => engagementApi.setPresence(v), { invalidate: [['presence']] })
  const today = isoDate()
  const mine = q.data?.people.find((p) => p.employeeId === employeeId)
  const [onlyTeam, setOnlyTeam] = useState(false)
  const team = (q.data?.people ?? []).filter((p) => mine?.department && p.department === mine.department)
  const shown = onlyTeam && mine?.department ? team : q.data?.people ?? []
  const teamInOffice = (d: string) => team.filter((p) => p.employeeId !== employeeId && p.days.find((x) => x.date === d)?.mode === 'Office').length
  // Geçen haftanın planı bu haftaya kopyalanır (izinli ve boş günler atlanır, bu haftadaki izin korunur).
  const copyLastWeek = useAction(async () => {
    const prevFrom = isoDate(new Date(anchor.getTime() - 7 * 86400000))
    const prevTo = isoDate(new Date(anchor.getTime() - 3 * 86400000))
    const prev = (await engagementApi.presence(prevFrom, prevTo)).people.find((p) => p.employeeId === employeeId)
    let n = 0
    for (const [i, d] of (q.data?.days ?? []).entries()) {
      const m = prev?.days[i]?.mode
      const cur = mine?.days.find((x) => x.date === d)?.mode
      if (!m || m === 'Leave' || m === 'Unknown' || cur === 'Leave' || cur === m) continue
      await engagementApi.setPresence({ date: d, mode: m })
      n++
    }
    return n
  }, { success: (n) => (n ? tx('Geçen haftanın planı kopyalandı ({0} gün).', [n]) : tx('Kopyalanacak gün yok.')), invalidate: [['presence']] })

  return (
    <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-4">
        {(['Office', 'Remote', 'Leave', 'Unknown'] as PresenceMode[]).map((m) => (
          <Metric key={m} label={tx('Bugün {0}', [presenceLabels[m].toLocaleLowerCase(appLocale)])} value={q.data?.today[m] ?? '—'} />
        ))}
      </div>
      {employeeId && (
        <Panel>
          <PanelHead title={tx('Bu hafta nerede çalışıyorum?')} note={tx('Bir güne tıklayıp çalışma yerinizi seçin. Masa ayırırsanız otomatik “Ofiste” olur.')}
            action={<Button size="sm" variant="outline" disabled={copyLastWeek.isPending} onClick={() => copyLastWeek.mutate(undefined)}><Copy className="size-4" />{tx('Geçen haftayı kopyala')}</Button>} />
          <PanelBody className="grid gap-3 sm:grid-cols-5">
            {q.data?.days.map((d) => {
              const cur = mine?.days.find((x) => x.date === d)?.mode ?? 'Unknown'
              return (
                <div key={d} className={cn('rounded-2xl border p-3', d === today ? 'border-primary/50 bg-primary/5' : 'border-border')}>
                  <p className="text-[12.5px] font-medium">{DAY.format(new Date(d))}</p>
                  <p className={cn('mb-2 text-[11px]', teamInOffice(d) ? 'text-primary' : 'text-muted-foreground')}>
                    {teamInOffice(d) ? tx('Ekipten {0} kişi ofiste', [teamInOffice(d)]) : tx('Ekipten ofiste kimse yok')}
                  </p>
                  {cur === 'Leave' ? <p className="text-[12.5px] text-amber-400">{tx('İzinli')}</p> : (
                    <div className="grid grid-cols-2 gap-1">
                      {(['Office', 'Remote', 'Travel', 'Off'] as PresenceMode[]).map((m) => {
                        const I = MODE_STYLE[m].icon
                        return (
                          <button key={m} type="button" title={presenceLabels[m]} aria-label={presenceLabels[m]} aria-pressed={cur === m} onClick={() => set.mutate({ date: d, mode: m })}
                            className={cn('flex cursor-pointer items-center justify-center gap-1 rounded-lg border px-1.5 py-1.5 text-[11px] transition', cur === m ? MODE_STYLE[m].cls : 'border-transparent text-muted-foreground hover:bg-accent')}>
                            <I aria-hidden="true" className="size-3.5" />
                          </button>
                        )
                      })}
                      {cur !== 'Unknown' && (
                        <button type="button" onClick={() => set.mutate({ date: d, mode: 'Unknown' })}
                          className="col-span-2 mt-0.5 cursor-pointer rounded-lg px-1.5 py-1 text-[11px] text-muted-foreground hover:bg-accent hover:text-foreground">
                          {tx('Bildirimi kaldır')}
                        </button>
                      )}
                    </div>
                  )}
                </div>
              )
            })}
          </PanelBody>
        </Panel>
      )}
      <Panel>
        <PanelHead
          title={tx('Kim nerede?')}
          action={
            <div className="flex items-center gap-1">
              {mine?.department && (
                <label className="mr-2 flex cursor-pointer items-center gap-1.5 text-[12.5px]">
                  <Checkbox checked={onlyTeam} onCheckedChange={(v) => setOnlyTeam(v === true)} />{tx('Yalnızca ekibim')}
                </label>
              )}
              <Button size="icon" variant="ghost" aria-label={tx('Önceki hafta')} onClick={() => setAnchor(new Date(anchor.getTime() - 7 * 86400000))}><ChevronLeft className="size-4" /></Button>
              <Button size="sm" variant="outline" onClick={() => setAnchor(startOfWeek(new Date()))}>{tx('Bu hafta')}</Button>
              <Button size="icon" variant="ghost" aria-label={tx('Sonraki hafta')} onClick={() => setAnchor(new Date(anchor.getTime() + 7 * 86400000))}><ChevronRight className="size-4" /></Button>
            </div>
          }
        />
        <PanelBody className="overflow-x-auto p-0">
          {q.isPending ? <div className="p-5"><RowsSkeleton /></div> : q.isError ? <ErrorState message={(q.error as Error).message} /> : (
            <table className="w-full min-w-[720px] text-[13px]">
              <thead>
                <tr className="border-b border-border text-left text-[11.5px] text-muted-foreground">
                  <th className="px-5 py-2.5 font-medium">{tx('Çalışan')}</th>
                  {q.data.days.map((d) => <th key={d} className={cn('px-2 py-2.5 font-medium', d === today && 'text-primary')}>{DAY.format(new Date(d))}</th>)}
                </tr>
              </thead>
              <tbody>
                {shown.map((p, i) => (
                  <motion.tr key={p.employeeId} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border/60 last:border-0">
                    <td className="px-5 py-2.5">
                      <div className="flex items-center gap-2.5"><EmployeeAvatar id={p.employeeId} name={p.name} size={28} /><div><p className="font-medium">{p.name}</p><p className="text-[11.5px] text-muted-foreground">{p.department ?? '—'}</p></div></div>
                    </td>
                    {p.days.map((d) => {
                      const S = MODE_STYLE[d.mode]
                      return (
                        <td key={d.date} className="px-2 py-2.5">
                          <span title={d.note ?? undefined} className={cn('inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11.5px]', S.cls)}>
                            <S.icon className="size-3" /> {presenceLabels[d.mode]}
                          </span>
                        </td>
                      )
                    })}
                  </motion.tr>
                ))}
              </tbody>
            </table>
          )}
        </PanelBody>
      </Panel>
    </div>
  )
}

const HOURS = Array.from({ length: 13 }, (_, i) => 8 + i) // 08–20

function BookingBoard() {
  const { roles } = useAuth()
  const [date, setDate] = useState(isoDate())
  const desks = useQuery({ queryKey: ['desks'], queryFn: ({ signal }) => engagementApi.desks(signal) })
  const bookings = useQuery({ queryKey: ['bookings', date], queryFn: ({ signal }) => engagementApi.bookings(date, signal) })
  const sample = useAction(() => engagementApi.sampleDesks(), { success: tx('Örnek ofis planı oluşturuldu'), invalidate: [['desks']] })
  // Oda çizelgesinde tıklanan saat diyaloğa başlangıç olarak iletilir.
  const [booking, setBooking] = useState<{ desk: Desk; hour?: number } | null>(null)
  // Kendi rezervasyonuna tıklayınca: ayrıntı + iptal penceresi.
  const [managing, setManaging] = useState<Desk | null>(null)

  const byDesk = useMemo(() => {
    const m = new Map<string, Booking[]>()
    bookings.data?.forEach((b) => m.set(b.deskId, [...(m.get(b.deskId) ?? []), b]))
    return m
  }, [bookings.data])

  if (desks.isPending) return <RowsSkeleton />
  if (desks.isError) return <ErrorState message={(desks.error as Error).message} />
  if (desks.data.length === 0)
    return <EmptyState icon={Armchair} title={tx('Ofis planı tanımlı değil')} detail={tx('İK, masa ve toplantı odalarını “Ofis planı” sekmesinden ekler.')} action={isHr(roles) && <Button onClick={() => sample.mutate(undefined)}>{tx('Örnek plan oluştur')}</Button>} />

  const floors = [...new Set(desks.data.filter((d) => d.kind === 'Desk').map((d) => d.floor ?? '—'))]
  const rooms = desks.data.filter((d) => d.kind === 'Room')
  const myBookings = (bookings.data ?? []).filter((b) => b.mine)

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-48"><TextField label={tx('Tarih')} type="date" value={date} min={isoDate()} max={isoDate(new Date(Date.now() + 30 * 86400000))} onChange={(e) => setDate(e.target.value)} /></div>
        {myBookings.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {myBookings.map((b) => (
              <span key={b.id} className="inline-flex items-center gap-2 rounded-full border border-primary/40 bg-primary/10 px-3 py-1.5 text-[12.5px]">
                {(() => { const d = desks.data.find((x) => x.id === b.deskId); return d ? deskLabel(d) : '—' })()} · {minutesToHHMM(b.startMinute)}–{minutesToHHMM(b.endMinute)}
                <button className="cursor-pointer text-muted-foreground hover:text-destructive" onClick={() => { const d = desks.data.find((x) => x.id === b.deskId); if (d) setManaging(d) }} aria-label={tx('İptal')}><Trash2 className="size-3.5" /></button>
              </span>
            ))}
          </div>
        )}
      </div>
      <div className="grid gap-5 xl:grid-cols-[1fr_1.1fr]">
        {floors.map((floor) => (
          <Panel key={floor}>
            <PanelHead title={<span className="flex items-center gap-2"><Armchair className="size-4 text-primary" /> {tx('{0} — masalar', [floor])}</span>} note={tx('Yeşil boş, kırmızı dolu, mavi sizin.')} />
            <PanelBody className="grid grid-cols-3 gap-3 sm:grid-cols-6 xl:grid-cols-3 2xl:grid-cols-6">
              {desks.data.filter((d) => d.kind === 'Desk' && (d.floor ?? '—') === floor).map((d, i) => {
                const bs = byDesk.get(d.id) ?? []
                const mine = bs.some((b) => b.mine)
                const taken = bs.length > 0
                return (
                  <motion.button
                    key={d.id}
                    type="button"
                    initial={{ opacity: 0, scale: 0.9 }}
                    animate={{ opacity: 1, scale: 1 }}
                    transition={{ delay: i * 0.03 }}
                    whileHover={{ y: -3 }}
                    disabled={taken && !mine}
                    onClick={() => (mine ? setManaging(d) : setBooking({ desk: d }))}
                    title={taken ? bs.map((b) => `${b.personName} ${minutesToHHMM(b.startMinute)}–${minutesToHHMM(b.endMinute)}`).join('\n') : `${d.zone ?? ''} ${d.features.join(', ')}`}
                    className={cn(
                      'relative flex cursor-pointer flex-col items-center gap-1 rounded-2xl border p-3 text-center transition disabled:cursor-not-allowed',
                      mine ? 'border-sky-400/60 bg-sky-500/15' : taken ? 'border-rose-500/40 bg-rose-500/10 opacity-80' : 'border-emerald-500/30 bg-emerald-500/5 hover:bg-emerald-500/15',
                    )}
                  >
                    <Armchair className={cn('size-5', mine ? 'text-sky-400' : taken ? 'text-rose-400' : 'text-emerald-400')} />
                    <span className="text-[12px] font-medium">{deskLabel(d)}</span>
                    <span className="max-w-full truncate text-[10.5px] text-muted-foreground">{taken ? bs[0].personName.split(' ')[0] : d.zone}</span>
                  </motion.button>
                )
              })}
            </PanelBody>
          </Panel>
        ))}
        {rooms.length > 0 && (
          <Panel className="min-w-0">
            <PanelHead title={<span className="flex items-center gap-2"><DoorOpen className="size-4 text-primary" />{' '}{tx('Toplantı odaları')}</span>} note={tx('Boş zaman dilimine tıklayın.')} />
            {/* min-w-0: geniş çizelge ızgara sütununu büyütüp paneli taşırmasın; kaydırma panelin içinde olur. */}
            <PanelBody className="overflow-x-auto">
              <div className="min-w-[640px] space-y-4">
              <div className="ml-28 flex text-[10.5px] text-muted-foreground">{HOURS.slice(0, -1).map((h) => <span key={h} className="flex-1">{h}:00</span>)}</div>
              {rooms.map((r) => (
                <div key={r.id} className="flex items-center gap-3">
                  <div className="w-25 shrink-0">
                    <p className="text-[13px] font-medium">{r.name}</p>
                    <p className="flex items-center gap-1 text-[11px] text-muted-foreground"><Users className="size-3" /> {r.capacity} · {r.floor}</p>
                  </div>
                  <div className="relative h-9 flex-1 rounded-xl bg-muted/40">
                    {HOURS.slice(0, -1).map((h, i) => (
                      <button key={h} type="button" onClick={() => setBooking({ desk: r, hour: h })} aria-label={`${r.name} ${h}:00`} className="absolute inset-y-0 cursor-pointer border-r border-border/40 hover:bg-primary/10" style={{ left: `${(i / 12) * 100}%`, width: `${100 / 12}%` }} />
                    ))}
                    {(byDesk.get(r.id) ?? []).map((b) => (
                      <motion.div key={b.id} initial={{ scaleX: 0 }} animate={{ scaleX: 1 }} style={{ left: `${((b.startMinute - 480) / 720) * 100}%`, width: `${((b.endMinute - b.startMinute) / 720) * 100}%`, transformOrigin: 'left' }}
                        className={cn('absolute inset-y-1 truncate rounded-lg px-2 text-[11px] leading-7', b.mine ? 'cursor-pointer bg-sky-500/80 text-white' : 'bg-rose-500/70 text-white')} title={`${b.title ?? ''} — ${b.personName}`}
                        onClick={b.mine ? () => setManaging(r) : undefined}>
                        {b.title ?? b.personName}
                      </motion.div>
                    ))}
                  </div>
                </div>
              ))}
              </div>
            </PanelBody>
          </Panel>
        )}
      </div>
      {booking && <BookModal desk={booking.desk} startHour={booking.hour} date={date} onClose={() => setBooking(null)} />}
      {managing && (
        <MyBookingModal
          desk={managing}
          date={date}
          bookings={myBookings.filter((b) => b.deskId === managing.id)}
          otherDeskToday={myBookings.some((b) => b.deskId !== managing.id && desks.data.find((x) => x.id === b.deskId)?.kind === 'Desk')}
          onBookAnother={() => { setBooking({ desk: managing }); setManaging(null) }}
          onClose={() => setManaging(null)}
        />
      )}
    </div>
  )
}

/**
 * Kendi rezervasyonunun ayrıntısı ve iptali. Masa ayırmak o günü otomatik "Ofiste" yaptığı için
 * masa iptalinde bu bildirimi de kaldırma ("Bildirmedi"ye dönüş) seçeneği sunulur.
 */
function MyBookingModal({ desk, date, bookings, otherDeskToday, onBookAnother, onClose }: {
  desk: Desk; date: string; bookings: Booking[]; otherDeskToday: boolean; onBookAnother: () => void; onClose: () => void
}) {
  const isDesk = desk.kind === 'Desk'
  const [clearPresence, setClearPresence] = useState(isDesk && !otherDeskToday)
  const cancel = useAction(
    async (id: string) => {
      await engagementApi.cancelBooking(id)
      // Aynı masada başka saat kalmadıysa ve kullanıcı istediyse günün "Ofiste" bildirimi kaldırılır.
      if (isDesk && clearPresence && bookings.length <= 1) await engagementApi.setPresence({ date, mode: 'Unknown' })
    },
    { success: tx('Rezervasyon iptal edildi'), invalidate: [['bookings'], ['presence']], onDone: () => { if (bookings.length <= 1) onClose() } },
  )
  return (
    <Modal open onClose={onClose} title={tx('{0} — rezervasyonunuz', [deskLabel(desk)])} note={[desk.kind === 'Desk' ? desk.name : null, desk.floor, desk.zone].filter(Boolean).join(' · ')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Kapat')}</Button><Button variant="outline" onClick={onBookAnother}>{tx('Başka saat ayır')}</Button></>}>
      <div className="space-y-3">
        {bookings.length === 0 ? <p className="text-[13px] text-muted-foreground">{tx('Bu gün için rezervasyonunuz yok.')}</p> : bookings.map((b) => (
          <div key={b.id} className="flex items-center justify-between gap-3 rounded-xl border border-border px-3 py-2.5 text-[13px]">
            <span>{minutesToHHMM(b.startMinute)}–{minutesToHHMM(b.endMinute)}{b.title ? ` · ${b.title}` : ''}</span>
            <Button size="sm" variant="outline" className="text-destructive" disabled={cancel.isPending} onClick={() => cancel.mutate(b.id)}>
              <Trash2 className="size-3.5" aria-hidden />{' '}{tx('Rezervasyonu iptal et')}
            </Button>
          </div>
        ))}
        {isDesk && bookings.length === 1 && (
          <label className="flex cursor-pointer items-start gap-2 text-[12.5px] text-muted-foreground">
            <input type="checkbox" className="mt-0.5" checked={clearPresence} onChange={(e) => setClearPresence(e.target.checked)} />
            <span>{tx('İptal edince o günkü “Ofiste” bildirimimi de kaldır (“Bildirmedi”ye dön).')}</span>
          </label>
        )}
      </div>
    </Modal>
  )
}

function BookModal({ desk, date, startHour, onClose }: { desk: Desk; date: string; startHour?: number; onClose: () => void }) {
  const hh = (h: number) => `${String(h).padStart(2, '0')}:00`
  const [start, setStart] = useState(startHour !== undefined ? hh(startHour) : desk.kind === 'Desk' ? '09:00' : '10:00')
  const [end, setEnd] = useState(startHour !== undefined ? hh(startHour + 1) : desk.kind === 'Desk' ? '18:00' : '11:00')
  const [title, setTitle] = useState('')
  const toMin = (s: string) => Number(s.slice(0, 2)) * 60 + Number(s.slice(3, 5))
  const book = useAction(() => engagementApi.book({ deskId: desk.id, date, startMinute: toMin(start), endMinute: toMin(end), title: title || undefined }), {
    success: tx('{0} ayrıldı', [deskLabel(desk)]), invalidate: [['bookings'], ['presence']], onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={tx('{0} — {1} ayır', [deskLabel(desk), desk.kind === 'Desk' ? tx('masa') : tx('oda')])} note={[desk.floor, desk.zone, ...desk.features].filter(Boolean).join(' · ')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => book.mutate(undefined)} disabled={book.isPending}>{tx('Ayır')}</Button></>}>
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label={tx('Başlangıç')} type="time" value={start} onChange={(e) => setStart(e.target.value)} />
        <TextField label={tx('Bitiş')} type="time" value={end} onChange={(e) => setEnd(e.target.value)} />
        {desk.kind === 'Room' && <div className="sm:col-span-2"><TextField label={tx('Toplantı adı')} value={title} onChange={(e) => setTitle(e.target.value)} /></div>}
      </div>
    </Modal>
  )
}

function OfficePlan() {
  const desks = useQuery({ queryKey: ['desks'], queryFn: ({ signal }) => engagementApi.desks(signal) })
  const [f, setF] = useState({ code: '', name: '', kind: 'Desk', floor: '1. kat', zone: '', capacity: '1', features: '' })
  const create = useAction(() => engagementApi.createDesk({ code: f.code, name: f.name, kind: f.kind as Desk['kind'], floor: f.floor, zone: f.zone || null, capacity: Number(f.capacity) || 1, features: f.features.split(',').map((s) => s.trim()).filter(Boolean) }), {
    success: tx('Eklendi'), invalidate: [['desks']], onDone: () => setF((x) => ({ ...x, code: '', name: '' })),
  })
  const del = useAction((id: string) => engagementApi.deleteDesk(id), { success: tx('Kaldırıldı'), invalidate: [['desks']] })
  const sample = useAction(() => engagementApi.sampleDesks(), { success: tx('Örnek plan oluşturuldu'), invalidate: [['desks']] })
  return (
    <div className="grid gap-5 lg:grid-cols-[360px_1fr]">
      <Panel>
        <PanelHead title={tx('Yeni masa / oda')} />
        <PanelBody className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <TextField label={tx('Kod')} value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} />
            <SelectField label={tx('Tür')} value={f.kind} onChange={(v) => setF({ ...f, kind: v })} options={[{ value: 'Desk', label: tx('Masa') }, { value: 'Room', label: tx('Toplantı odası') }]} />
          </div>
          <TextField label={tx('Ad')} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
          <div className="grid grid-cols-2 gap-3">
            <TextField label={tx('Kat')} value={f.floor} onChange={(e) => setF({ ...f, floor: e.target.value })} />
            <TextField label={tx('Kapasite')} type="number" min={1} value={f.capacity} onChange={(e) => setF({ ...f, capacity: e.target.value })} />
          </div>
          <TextField label={tx('Bölge')} value={f.zone} onChange={(e) => setF({ ...f, zone: e.target.value })} />
          <TextField label={tx('Özellikler (virgülle)')} value={f.features} onChange={(e) => setF({ ...f, features: e.target.value })} />
          <Button onClick={() => create.mutate(undefined)} disabled={!f.code || !f.name}><Plus className="size-4" />{' '}{tx('Ekle')}</Button>
        </PanelBody>
      </Panel>
      <Panel>
        <PanelHead title={tx('Ofis envanteri')} action={(desks.data?.length ?? 0) === 0 && <Button size="sm" variant="outline" onClick={() => sample.mutate(undefined)}>{tx('Örnek plan')}</Button>} />
        <PanelBody className="p-0">
          <ul className="divide-y divide-border">
            {desks.data?.map((d) => (
              <li key={d.id} className="flex items-center gap-3 px-5 py-2.5 text-[13px]">
                {d.kind === 'Desk' ? <Armchair className="size-4 text-muted-foreground" /> : <DoorOpen className="size-4 text-muted-foreground" />}
                <span className="w-24 font-mono text-[12px]">{d.code}</span>
                <span className="flex-1">{d.name} <span className="text-muted-foreground">· {d.floor} {d.zone ? `· ${d.zone}` : ''}</span></span>
                <span className="text-muted-foreground">{d.features.join(', ')}</span>
                <Button size="icon" variant="ghost" aria-label={tx('Kaldır')} onClick={() => del.mutate(d.id)}><Trash2 className="size-4" /></Button>
              </li>
            ))}
          </ul>
        </PanelBody>
      </Panel>
    </div>
  )
}

export function WorkplacePage() {
  const { roles } = useAuth()
  const hr = isHr(roles, 'ext-engagement-manage')
  const [tab, setTab] = useTabParam<'kim-nerede' | 'rezervasyon' | 'plan'>('sekme', 'kim-nerede')
  return (
    <TenantGate>
      <PageHeader title={tx('Ofis ve masa')} description={tx('Hibrit çalışmada kimin nerede olduğunu görün, masa ve toplantı odası ayırın.')} />
      <div className="mb-5">
        <Tabs label={tx('Ofis')} value={tab} onChange={setTab} tabs={[{ key: 'kim-nerede', label: tx('Kim nerede') }, { key: 'rezervasyon', label: tx('Masa & oda') }, ...(hr ? [{ key: 'plan' as const, label: tx('Ofis planı') }] : [])]} />
      </div>
      {tab === 'kim-nerede' && <PresenceBoard />}
      {tab === 'rezervasyon' && <BookingBoard />}
      {tab === 'plan' && hr && <OfficePlan />}
    </TenantGate>
  )
}
