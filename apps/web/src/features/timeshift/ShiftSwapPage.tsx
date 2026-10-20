/**
 * G6: vardiya tercihleri ve takas. Çalışan tercihini girer, vardiyasını bir ekip arkadaşıyla
 * değiştirmek/devretmek ister; karşı taraf kabul eder, bölüm başı ya da İK onaylar. Kurallar
 * (aynı ekip, çakışma yok, 11 saat dinlenme, haftalık 45 saat) sunucuda denetlenir. Planlayıcı
 * atama yaparken çalışanın tercihleriyle uyuşmazlıkları görür.
 */
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeftRight, CalendarCheck2, Check, Settings2, X } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { Tabs } from '@/components/ui/tabs'
import { Callout } from '@/components/ui/callout'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useMyEmployeeId, useShifts } from '@/api/queries'
import { isoDayLabels, opsApi, swapStatusView, type PreferenceCheck, type RosterAssignment, type ShiftPreference, type SwapRequest } from '@/api/opsPlus'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { PersonSelect, isoDate, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'
import { ShiftDefinitionsPanel } from './ShiftDefinitions'

const hhmm = (t?: string | null) => (t ? t.slice(0, 5) : '—')
const plusDays = (n: number) => isoDate(new Date(Date.now() + n * 864e5))
const shiftLabel = (a: RosterAssignment) => `${formatDate(a.date)} · ${a.shift?.name ?? '—'} ${hhmm(a.shift?.startTime)}–${hhmm(a.shift?.endTime)}`

/* ------------------------------------------------------------------ tercihler */

function PreferencesPanel() {
  const q = useQuery({ queryKey: ['shift-pref', 'me'], queryFn: ({ signal }) => opsApi.myPreference(signal), retry: false })
  const [d, setD] = useState<ShiftPreference | null>(null)
  const p = d ?? q.data ?? null
  const save = useAction(() => opsApi.savePreference({ preferredDays: p!.preferredDays, unavailableDays: p!.unavailableDays, preferredShiftTypes: p!.preferredShiftTypes,
    avoidShiftTypes: p!.avoidShiftTypes, maxNightsPerWeek: p!.maxNightsPerWeek, note: p!.note }), { success: tx('Tercihleriniz kaydedildi'), invalidate: [['shift-pref']], onDone: () => setD(null) })
  if (q.isPending) return <RowsSkeleton rows={2} />
  if (!p) return <p className="text-[13px] text-muted-foreground">{tx('Çalışan kaydınız olmadığı için tercih girilemez.')}</p>
  const cycleDay = (day: number) => {
    // boş → tercih → müsait değil → boş
    const pref = p.preferredDays.includes(day)
    const un = p.unavailableDays.includes(day)
    setD({ ...p,
      preferredDays: pref ? p.preferredDays.filter((x) => x !== day) : un ? p.preferredDays : [...p.preferredDays, day],
      unavailableDays: pref ? [...p.unavailableDays, day] : p.unavailableDays.filter((x) => x !== day) })
  }
  const toggleType = (list: 'preferredShiftTypes' | 'avoidShiftTypes', t: 'Day' | 'Night', on: boolean) => {
    const other = list === 'preferredShiftTypes' ? 'avoidShiftTypes' : 'preferredShiftTypes'
    setD({ ...p, [list]: on ? [...p[list].filter((x) => x !== t), t] : p[list].filter((x) => x !== t), [other]: on ? p[other].filter((x) => x !== t) : p[other] })
  }
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Settings2 className="size-4 text-primary" />{' '}{tx('Vardiya tercihlerim')}</span>}
        note={tx('Planlayıcı atama yaparken görür; bağlayıcı değildir. Sağlık gibi özel bilgileri yazmanız gerekmez.')} />
      <PanelBody className="space-y-4">
        <div>
          <p className="mb-1.5 text-[13px] font-medium">{tx('Günler (tıklayarak: tercih → müsait değil → boş)')}</p>
          <div className="flex flex-wrap gap-1.5">
            {[1, 2, 3, 4, 5, 6, 7].map((day) => {
              const pref = p.preferredDays.includes(day)
              const un = p.unavailableDays.includes(day)
              return (
                <button key={day} type="button" onClick={() => cycleDay(day)} aria-pressed={pref || un}
                  className={cn('h-9 w-14 cursor-pointer rounded-lg border text-[13px]', pref ? 'border-emerald-500 bg-emerald-500/15' : un ? 'border-rose-500 bg-rose-500/15 line-through' : 'border-border')}>
                  {isoDayLabels[day]}
                </button>
              )
            })}
          </div>
        </div>
        <div className="flex flex-wrap gap-5 text-[13px]">
          {(['Day', 'Night'] as const).map((t) => (
            <div key={t} className="space-y-1">
              <p className="font-medium">{t === 'Day' ? tx('Gündüz') : tx('Gece')}</p>
              <label className="flex items-center gap-2"><Checkbox checked={p.preferredShiftTypes.includes(t)} onCheckedChange={(v) => toggleType('preferredShiftTypes', t, v === true)} />{' '}{tx('Tercih ederim')}</label>
              <label className="flex items-center gap-2"><Checkbox checked={p.avoidShiftTypes.includes(t)} onCheckedChange={(v) => toggleType('avoidShiftTypes', t, v === true)} />{' '}{tx('İstemiyorum')}</label>
            </div>
          ))}
          <div className="w-44"><TextField label={tx('Haftada en çok gece')} type="number" min={0} max={7} value={p.maxNightsPerWeek ?? ''}
            onChange={(e) => setD({ ...p, maxNightsPerWeek: e.target.value === '' ? null : Number(e.target.value) })} /></div>
        </div>
        <TextAreaField label={tx('Not (isteğe bağlı)')} rows={2} maxLength={300} value={p.note ?? ''} onChange={(e) => setD({ ...p, note: e.target.value })} />
        <Button onClick={() => save.mutate(undefined)} disabled={save.isPending || d === null}>{tx('Kaydet')}</Button>
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ takas talebi */

function SwapModal({ mine, onClose }: { mine: RosterAssignment; onClose: () => void }) {
  const { employeeId: me } = useMyEmployeeId()
  const [target, setTarget] = useState('')
  const [mode, setMode] = useState<'swap' | 'give'>('swap')
  const [theirs, setTheirs] = useState('')
  const [note, setNote] = useState('')
  const peers = useQuery({ queryKey: ['shift-swap', 'peers'], queryFn: ({ signal }) => opsApi.swapPeers(signal) })
  const peerOptions = (peers.data ?? []).filter((p) => p.id !== me)
    .sort((a, b) => a.fullName.localeCompare(b.fullName, 'tr-TR')).map((p) => ({ value: p.id, label: p.fullName }))
  const roster = useQuery({ queryKey: ['shift-swap', 'roster', target], enabled: Boolean(target) && mode === 'swap',
    queryFn: ({ signal }) => opsApi.roster({ from: isoDate(), to: plusDays(60), employeeId: target }, signal) })
  const create = useAction(() => opsApi.createSwap({ myAssignmentId: mine.id, targetEmployeeId: target, targetAssignmentId: mode === 'swap' ? theirs : null, note: note.trim() || undefined }),
    { success: tx('Takas talebi gönderildi; karşı tarafın yanıtı bekleniyor'), invalidate: [['shift-swaps']], onDone: onClose })
  return (
    <Modal open onClose={onClose} title={tx('Vardiya takası')} note={shiftLabel(mine)}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button onClick={() => create.mutate(undefined)} disabled={create.isPending || !target || (mode === 'swap' && !theirs)}>{tx('Gönder')}</Button></>}>
      <div className="space-y-3">
        <SelectField label={tx('Ekip arkadaşı')} value={target} onChange={(v) => { setTarget(v); setTheirs('') }} options={peerOptions}
          hint={peers.isPending ? tx('Yükleniyor') : peerOptions.length === 0 ? tx('Takas yapabileceğiniz ekip arkadaşı yok (aynı vardiya ekibi ya da departman).') : undefined} />
        <SelectField label={tx('Tür')} value={mode} onChange={(v) => setMode(v as 'swap' | 'give')}
          options={[{ value: 'swap', label: tx('Onun bir vardiyasıyla değiştir') }, { value: 'give', label: tx('Vardiyamı devret') }]} />
        {mode === 'swap' && target && (
          roster.isPending ? <RowsSkeleton rows={1} /> : (
            <SelectField label={tx('Karşılığında alacağınız vardiya')} value={theirs} onChange={setTheirs}
              options={(roster.data ?? []).map((a) => ({ value: a.id, label: shiftLabel(a) }))}
              hint={(roster.data ?? []).length === 0 ? tx('Bu kişinin önümüzdeki 60 günde vardiyası yok.') : undefined} />
          )
        )}
        <TextAreaField label={tx('Not (isteğe bağlı)')} rows={2} maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} />
        <InfoNote>{tx('Kurallar: aynı ekip, çakışma yok, vardiyalar arası en az 11 saat dinlenme, haftalık en çok 45 saat (şirket ayarı farklı olabilir). Uymayan talep gerekçesiyle reddedilir; günlük süre, gece çalışması ve ardışık gün aşımları onaycıya uyarı olarak gösterilir.')}</InfoNote>
      </div>
    </Modal>
  )
}

function SwapList({ rows, scope }: { rows: SwapRequest[]; scope: 'mine' | 'approvals' }) {
  const [rej, setRej] = useState<SwapRequest | null>(null)
  const [reason, setReason] = useState('')
  const inv = [['shift-swaps']]
  const respond = useAction(({ id, ok }: { id: string; ok: boolean }) => opsApi.respondSwap(id, ok), { success: tx('Yanıtınız iletildi'), invalidate: inv })
  const cancel = useAction((id: string) => opsApi.cancelSwap(id), { success: tx('Talep iptal edildi'), invalidate: inv })
  const approve = useAction((id: string) => opsApi.decideSwap(id, true), { success: tx('Takas onaylandı; vardiyalar değişti'), invalidate: inv })
  const reject = useAction(() => opsApi.decideSwap(rej!.id, false, reason.trim() || undefined), { success: tx('Takas reddedildi'), invalidate: inv, onDone: () => setRej(null) })
  // Boş metin role göre: çalışana talep başlatma yolu, onaycıya onay kuyruğunun anlamı.
  if (rows.length === 0) return scope === 'mine'
    ? <EmptyState icon={ArrowLeftRight} title={tx('Takas talebi yok')} detail={tx('Vardiyalarınızdan birinde “Takas iste” ile başlayın.')} />
    : <EmptyState icon={ArrowLeftRight} title={tx('Onay bekleyen takas yok')} detail={tx('Ekip arkadaşının kabul ettiği takas talepleri onayınız için burada listelenir.')} />
  return (
    <ul className="divide-y divide-border">
      {rows.map((s) => (
        <li key={s.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
          <span className="min-w-0 flex-1">
            <b>{s.requesterName ?? '—'}</b> → <b>{s.targetName ?? '—'}</b>
            <span className="block text-[12px] text-muted-foreground">
              {s.requesterShift ? `${formatDate(s.requesterShift.date)} ${s.requesterShift.name}` : '—'}
              {s.giveAway ? ` · ${tx('devir')}` : s.targetShift ? ` ⇄ ${formatDate(s.targetShift.date)} ${s.targetShift.name}` : ''}
              {s.note ? ` · ${s.note}` : ''}{s.rejectReason ? ` · ${s.rejectReason}` : ''}
            </span>
          </span>
          {/* Madde 66: onaycıya kural uyarıları; engelleyici olanlar onayda takası reddeder. */}
          {s.ruleWarnings && s.ruleWarnings.length > 0 && (
            <span className="basis-full space-y-0.5 text-[12px]">
              {s.ruleWarnings.map((w, i) => (
                <span key={`${w.code}-${i}`} className={cn('block', w.blocking ? 'text-destructive' : 'text-[hsl(var(--warning))]')}>
                  {w.blocking ? tx('Engel: {0}', [txServer(w.message)]) : tx('Uyarı: {0}', [txServer(w.message)])}
                </span>
              ))}
            </span>
          )}
          <StatusBadge tone={swapStatusView[s.status].tone}>{swapStatusView[s.status].label}</StatusBadge>
          {s.canRespond && <><Button size="sm" onClick={() => respond.mutate({ id: s.id, ok: true })}><Check className="size-4" />{' '}{tx('Kabul')}</Button><Button size="sm" variant="outline" onClick={() => respond.mutate({ id: s.id, ok: false })}><X className="size-4" />{' '}{tx('Reddet')}</Button></>}
          {s.canApprove && <><Button size="sm" onClick={() => approve.mutate(s.id)}><Check className="size-4" />{' '}{tx('Onayla')}</Button><Button size="sm" variant="outline" onClick={() => { setRej(s); setReason('') }}>{tx('Reddet')}</Button></>}
          {s.canCancel && <Button size="sm" variant="ghost" onClick={() => cancel.mutate(s.id)}>{tx('İptal')}</Button>}
        </li>
      ))}
      {rej && (
        <Modal open onClose={() => setRej(null)} title={tx('Takası reddet')} footer={<><Button variant="outline" onClick={() => setRej(null)}>{tx('Vazgeç')}</Button><Button variant="destructive" onClick={() => reject.mutate(undefined)} disabled={reject.isPending}>{tx('Reddet')}</Button></>}>
          <TextAreaField label={tx('Gerekçe')} rows={3} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Modal>
      )}
    </ul>
  )
}

/* ------------------------------------------------------------------ planlayıcı: tercihe göre atama */

function PlannerAssignPanel() {
  const shifts = useShifts()
  const [emp, setEmp] = useState('')
  const [shift, setShift] = useState('')
  const [date, setDate] = useState(plusDays(1))
  const [check, setCheck] = useState<PreferenceCheck | null>(null)
  const doCheck = useAction(() => opsApi.checkPreference({ employeeId: emp, shiftId: shift, date }), { onDone: setCheck })
  const assign = useAction(() => opsApi.assignShift(shift, emp, date), { success: tx('Vardiya atandı'), invalidate: [['shift-swap'], ['timeshift']], onDone: () => setCheck(null) })
  const unassign = useAction((id: string) => opsApi.unassignShift(id), { success: tx('Atama kaldırıldı'), invalidate: [['shift-swap'], ['timeshift']] })
  const upcoming = useQuery({ queryKey: ['shift-swap', 'roster', emp], enabled: Boolean(emp),
    queryFn: ({ signal }) => opsApi.roster({ from: isoDate(), to: plusDays(60), employeeId: emp }, signal) })
  const p = check?.preference
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><CalendarCheck2 className="size-4 text-primary" />{' '}{tx('Tercihe göre vardiya ata')}</span>}
        note={tx('Atamadan önce çalışanın tercihleriyle uyuşmazlıklar gösterilir; karar sizindir.')} />
      <PanelBody className="space-y-3">
        <div className="grid gap-3 md:grid-cols-3">
          <PersonSelect label={tx('Çalışan')} value={emp} onChange={(v) => { setEmp(v); setCheck(null) }} />
          <SelectField label={tx('Vardiya')} value={shift} onChange={(v) => { setShift(v); setCheck(null) }}
            options={(shifts.data ?? []).map((s) => ({ value: s.id, label: `${s.name} ${hhmm(s.startTime)}–${hhmm(s.endTime)}` }))}
            hint={shifts.data && shifts.data.length === 0 ? tx('Henüz vardiya tanımı yok; aşağıdaki “Vardiya tanımları” bölümünden ekleyin.') : undefined} />
          <TextField label={tx('Tarih')} type="date" min={isoDate()} value={date} onChange={(e) => { setDate(e.target.value); setCheck(null) }} />
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={() => doCheck.mutate(undefined)} disabled={!emp || !shift || doCheck.isPending}>{tx('Tercihleri denetle')}</Button>
          <Button onClick={() => assign.mutate(undefined)} disabled={!emp || !shift || !check || assign.isPending}>{tx('Ata')}</Button>
        </div>
        {check && (
          <div className="space-y-2 rounded-xl border border-border p-3 text-[13px]">
            {check.conflicts.length === 0 ? <p className="text-[hsl(var(--success))]">{tx('Tercihlerle uyuşmazlık yok.')}</p> : (
              <ul className="list-disc space-y-0.5 pl-5 text-[hsl(var(--warning))]">{check.conflicts.map((c) => <li key={c.code}>{txServer(c.message)}</li>)}</ul>
            )}
            {/* Madde 66: çalışma süresi kuralları (uyarı; atama yine yapılabilir). */}
            {check.ruleWarnings && (check.ruleWarnings.length === 0
              ? <p className="text-[hsl(var(--success))]">{tx('Çalışma süresi kurallarına uygun (dinlenme, günlük/haftalık süre, gece çalışması, ardışık gün).')}</p>
              : (
                <div role="alert">
                  <Callout tone="danger" title={tx('Çalışma süresi kuralı uyarısı')}>
                    <ul className="list-disc space-y-0.5 pl-5">{check.ruleWarnings.map((w, i) => <li key={`${w.code}-${i}`}>{txServer(w.message)}</li>)}</ul>
                  </Callout>
                </div>
              ))}
            {p && p.updatedAt && (
              <p className="text-[12px] text-muted-foreground">
                {tx('Tercih: {0} · müsait değil: {1} · gece en çok {2}', [p.preferredDays.map((d) => isoDayLabels[d]).join(', ') || '—',
                  p.unavailableDays.map((d) => isoDayLabels[d]).join(', ') || '—', p.maxNightsPerWeek ?? '—'])}{p.note ? ` · “${p.note}”` : ''}
              </p>
            )}
          </div>
        )}
        {emp && (
          <div className="rounded-xl border border-border">
            <p className="border-b border-border px-3 py-2 text-[12px] font-medium text-muted-foreground">{tx('Çalışanın önümüzdeki vardiyaları (60 gün)')}</p>
            {upcoming.isPending ? <div className="p-3"><RowsSkeleton rows={1} /></div> : upcoming.isError ? (
              <p className="p-3 text-[13px] text-muted-foreground">{tx('Bu çalışanın vardiyalarını görme yetkiniz yok.')}</p>
            ) : !upcoming.data?.length ? (
              <p className="p-3 text-[13px] text-muted-foreground">{tx('Atanmış vardiya yok.')}</p>
            ) : (
              <ul className="divide-y divide-border">
                {upcoming.data.map((a) => (
                  <li key={a.id} className="flex items-center gap-3 px-3 py-2 text-[13px]">
                    <span className="flex-1">{shiftLabel(a)}</span>
                    <Button size="sm" variant="outline" onClick={() => unassign.mutate(a.id)} disabled={unassign.isPending}>{tx('Atamayı kaldır')}</Button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ sayfa */

export function ShiftSwapPage() {
  const { roles, hasRole } = useAuth()
  const planner = hasRole('manager') || isHr(roles, 'ext-timeshift-manage')
  const { employeeId: me } = useMyEmployeeId()
  const [tab, setTab] = useState<'mine' | 'approvals' | 'plan'>('mine')
  const [swapFor, setSwapFor] = useState<RosterAssignment | null>(null)
  const myShifts = useQuery({ queryKey: ['shift-swap', 'roster', me], enabled: Boolean(me), queryFn: ({ signal }) => opsApi.roster({ from: isoDate(), to: plusDays(60), employeeId: me! }, signal) })
  const mine = useQuery({ queryKey: ['shift-swaps', 'mine'], queryFn: ({ signal }) => opsApi.swaps('mine', signal) })
  const approvals = useQuery({ queryKey: ['shift-swaps', 'approvals'], enabled: planner, queryFn: ({ signal }) => opsApi.swaps('approvals', signal) })
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Vardiya takası ve tercihler')} description={tx('Vardiyanızı ekip arkadaşınızla değiştirin, tercihlerinizi planlayıcıya bildirin.')} />
      {planner && (
        <Tabs label={tx('Görünüm')} value={tab} onChange={setTab} tabs={[
          { key: 'mine', label: tx('Benim') },
          { key: 'approvals', label: tx('Takas onayları'), count: approvals.data?.filter((x) => x.canApprove).length },
          { key: 'plan', label: tx('Atama') },
        ]} />
      )}
      {tab === 'mine' && (
        <>
          {me && (
            <Panel>
              <PanelHead title={tx('Önümüzdeki vardiyalarım')} note={tx('60 gün')} />
              <PanelBody className="p-0">
                {myShifts.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : !myShifts.data?.length ? (
                  <p className="p-5 text-[13px] text-muted-foreground">{tx('Atanmış vardiyanız yok.')}</p>
                ) : (
                  <ul className="divide-y divide-border">
                    {myShifts.data.map((a) => (
                      <li key={a.id} className="flex items-center gap-3 px-5 py-2.5 text-[13px]">
                        <span className="flex-1">{shiftLabel(a)}</span>
                        <Button size="sm" variant="outline" onClick={() => setSwapFor(a)}><ArrowLeftRight className="size-4" />{' '}{tx('Takas iste')}</Button>
                      </li>
                    ))}
                  </ul>
                )}
              </PanelBody>
            </Panel>
          )}
          <Panel>
            <PanelHead title={tx('Takas taleplerim')} />
            <PanelBody className="p-0">{mine.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : <SwapList rows={mine.data ?? []} scope="mine" />}</PanelBody>
          </Panel>
          {me && <PreferencesPanel />}
        </>
      )}
      {tab === 'approvals' && planner && (
        <Panel>
          <PanelHead title={tx('Onay bekleyen takaslar')} note={tx('Onayda kurallar yeniden denetlenir; ihlal varsa takas gerekçesiyle reddedilir.')} />
          <PanelBody className="p-0">{approvals.isPending ? <div className="p-5"><RowsSkeleton rows={2} /></div> : <SwapList rows={approvals.data ?? []} scope="approvals" />}</PanelBody>
        </Panel>
      )}
      {tab === 'plan' && planner && <><PlannerAssignPanel /><ShiftDefinitionsPanel /></>}
      {swapFor && <SwapModal mine={swapFor} onClose={() => setSwapFor(null)} />}
    </div>
  )
}
