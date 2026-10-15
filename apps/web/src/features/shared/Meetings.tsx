/**
 * Takvim bağlantıları (kişisel), toplantı oluşturma (Zoom / Teams / Google Meet)
 * ve uygun saat önerisi. 1:1 ve mülakat ekranlarında ortak kullanılır.
 */

import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { motion } from 'motion/react'
import { CalendarCheck, CalendarClock, ExternalLink, Link2, Sparkles, Trash2, Video } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField } from '@/components/ui/Field'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { InfoNote, RowsSkeleton } from '@/components/ui/States'
import { useToast } from '@/components/ui/Toast'
import { governanceApi, type MeetingInfo, type MeetingProvider } from '@/api/governance'
import { formatDateTime, formatRelativeToNow } from '@/lib/format'
import { cn } from '@/lib/utils'
import { errMsg, useAction } from './kit'
import { tx, appLocale } from '@/lib/i18n'

const PROVIDER_LABEL: Record<MeetingProvider, string> = { zoom: tx('Zoom'), teams: tx('Microsoft Teams'), google: tx('Google Meet'), none: tx('Bağlantısız (yalnızca takvim)') }
const ACCOUNT_LABEL = { Google: tx('Google Takvim'), Microsoft: tx('Outlook (Microsoft 365)') } as const

/* ------------------------------------------------------------------ kişisel takvim bağlantısı */

export function CalendarConnections() {
  const toast = useToast()
  const [params, setParams] = useSearchParams()
  const q = useQuery({ queryKey: ['calendar-connections'], queryFn: ({ signal }) => governanceApi.calendarConnections(signal) })
  const sync = useAction(({ id, on }: { id: string; on: boolean }) => governanceApi.patchCalendarConnection(id, on), { invalidate: [['calendar-connections']] })
  const remove = useAction((id: string) => governanceApi.disconnectCalendar(id), { success: tx('Takvim bağlantısı kaldırıldı'), invalidate: [['calendar-connections']] })

  // OAuth dönüşü: ?takvim=baglandi | hata&mesaj=…
  useEffect(() => {
    const r = params.get('takvim')
    if (!r) return
    if (r === 'baglandi') toast.ok(tx('Takvim bağlandı'))
    else toast.stop(params.get('mesaj') ?? tx('Takvim bağlanamadı'))
    params.delete('takvim')
    params.delete('mesaj')
    setParams(params, { replace: true })
  }, [params, setParams, toast])

  const connect = async (provider: 'Google' | 'Microsoft') => {
    try {
      const { authorizeUrl } = await governanceApi.connectCalendar(provider)
      window.location.assign(authorizeUrl)
    } catch (e) {
      toast.stop(errMsg(e))
    }
  }
  const data = q.data
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><CalendarCheck className="size-4 text-primary" />{' '}{tx('Takvim hesabı bağla')}</span>} note={tx('Onaylanan izinleriniz takviminize anında yazılır; 1:1 ve mülakat davetleri, Teams ve Google Meet bağlantıları takviminizden gider.')} />
      <PanelBody className="space-y-4">
        {q.isPending ? <RowsSkeleton rows={2} /> : !data?.linked ? (
          <p className="text-[13px] text-muted-foreground">{tx('Hesabınıza bağlı çalışan kaydı yok.')}</p>
        ) : (
          <>
            {data.connections.map((c) => (
              <motion.div key={c.id} layout className="flex flex-wrap items-center gap-3 rounded-xl border border-border p-3">
                <span className="grid size-9 place-items-center rounded-xl bg-primary/10 text-primary"><CalendarCheck className="size-4" /></span>
                <div className="min-w-0 flex-1">
                  <p className="text-[13.5px] font-medium">{ACCOUNT_LABEL[c.provider]} <span className="text-muted-foreground">· {c.accountEmail}</span></p>
                  <p className="text-[12px] text-muted-foreground">{c.lastSyncAt ? tx('Son eşitleme {0}', [formatRelativeToNow(c.lastSyncAt)]) : tx('Bağlandı {0}', [formatRelativeToNow(c.createdAt)])}</p>
                  {c.lastError && <p className="mt-1 text-[12px] text-destructive">{c.lastError}</p>}
                </div>
                <StatusBadge tone={c.status === 'Active' ? 'success' : 'danger'}>{c.status === 'Active' ? tx('Bağlı') : tx('Yeniden bağlayın')}</StatusBadge>
                <label className="flex items-center gap-2 text-[12.5px]"><Checkbox checked={c.syncLeaves} onCheckedChange={(v) => sync.mutate({ id: c.id, on: v === true })} />{' '}{tx('İzinlerimi ekle')}</label>
                {c.status !== 'Active' && <Button size="sm" variant="outline" onClick={() => connect(c.provider)}>{tx('Yeniden bağla')}</Button>}
                <Button size="icon" variant="ghost" aria-label={tx('Bağlantıyı kaldır')} onClick={() => remove.mutate(c.id)}><Trash2 className="size-4" /></Button>
              </motion.div>
            ))}
            <div className="flex flex-wrap gap-2">
              {(['Google', 'Microsoft'] as const).filter((p) => !data.connections.some((c) => c.provider === p)).map((p) => (
                <Button key={p} variant="outline" disabled={!data.available.includes(p)} onClick={() => connect(p)}>
                  <Link2 className="size-4" /> {ACCOUNT_LABEL[p]}
                </Button>
              ))}
            </div>
            {data.available.length === 0 && <InfoNote>{tx('Yöneticiniz henüz Google ya da Microsoft 365 entegrasyonunu açmadı (Entegrasyonlar › Takvim ve toplantı). Aşağıdaki .ics aboneliği her takvimle çalışır.')}</InfoNote>}
          </>
        )}
      </PanelBody>
    </Panel>
  )
}

/* ------------------------------------------------------------------ uygun saat */

export function SlotFinder({ employeeIds, durationMinutes, onPick }: { employeeIds: string[]; durationMinutes: number; onPick: (localValue: string) => void }) {
  const [res, setRes] = useState<Awaited<ReturnType<typeof governanceApi.availability>> | null>(null)
  const find = useAction(() => {
    const from = new Date()
    const to = new Date(Date.now() + 10 * 86400000)
    return governanceApi.availability({ employeeIds, from: from.toISOString(), to: to.toISOString(), durationMinutes })
  }, { onDone: setRes })
  const local = (iso: string) => {
    const d = new Date(iso)
    const p = (n: number) => String(n).padStart(2, '0')
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`
  }
  return (
    <div className="space-y-2">
      <Button type="button" size="sm" variant="outline" disabled={employeeIds.length === 0 || find.isPending} onClick={() => find.mutate(undefined)}>
        <Sparkles className="size-4" /> {find.isPending ? tx('Bakılıyor…') : tx('Uygun saat öner')}
      </Button>
      {res && (
        <div className="space-y-2 rounded-xl border border-border p-3 text-[12.5px]">
          {res.suggestions.length === 0 ? <p className="text-muted-foreground">{tx('Önümüzdeki 10 iş gününde herkesin boş olduğu aralık bulunamadı.')}</p> : (
            <div className="flex flex-wrap gap-1.5">
              {res.suggestions.map((s) => (
                <button key={s} type="button" onClick={() => onPick(local(s))} className="cursor-pointer rounded-full border border-primary/40 bg-primary/10 px-2.5 py-1 hover:bg-primary/20">
                  {new Date(s).toLocaleString(appLocale, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}
                </button>
              ))}
            </div>
          )}
          <p className="text-[11.5px] text-muted-foreground">
            {res.people.map((p) => `${p.name}: ${p.calendarConnected ? tx('takvim bağlı') : tx('takvim bağlı değil (yalnızca HR360 izin ve 1:1 kayıtları)')}`).join(' · ')}
          </p>
        </div>
      )}
    </div>
  )
}

/* ------------------------------------------------------------------ toplantı */

function MeetingRow({ m, canCancel }: { m: MeetingInfo; canCancel: boolean }) {
  const toast = useToast()
  const cancel = useAction(() => governanceApi.cancelMeeting(m.id), { success: tx('Toplantı iptal edildi'), invalidate: [['meetings']] })
  return (
    <li className="flex flex-wrap items-center gap-2 rounded-xl border border-border p-3 text-[13px]">
      <Video className="size-4 text-primary" />
      <div className="min-w-0 flex-1">
        <p className="font-medium">{tx('{0} · {1} · {2} dk', [PROVIDER_LABEL[m.provider], formatDateTime(m.startsAt), m.durationMinutes])}</p>
        {m.warnings?.map((w) => <p key={w} className="text-[12px] text-[hsl(var(--warning))]">{w}</p>)}
      </div>
      {m.joinUrl && (
        <>
          <Button size="sm" onClick={() => window.open(m.joinUrl!, '_blank', 'noopener')}><ExternalLink className="size-4" />{' '}{tx('Katıl')}</Button>
          <Button size="sm" variant="outline" onClick={() => navigator.clipboard.writeText(m.joinUrl!).then(() => toast.ok(tx('Bağlantı kopyalandı')))}>{tx('Kopyala')}</Button>
        </>
      )}
      {canCancel && <Button size="icon" variant="ghost" aria-label={tx('Toplantıyı iptal et')} onClick={() => cancel.mutate(undefined)}><Trash2 className="size-4" /></Button>}
    </li>
  )
}

/** Bir 1:1 ya da mülakat için toplantı bağlantıları + yeni toplantı. */
export function MeetingPanel({ sourceType, sourceId, canCreate, candidate }: { sourceType: 'one-on-one' | 'interview'; sourceId: string; canCreate: boolean; candidate?: boolean }) {
  const list = useQuery({ queryKey: ['meetings', sourceType, sourceId], queryFn: ({ signal }) => governanceApi.meetings(sourceType, sourceId, signal) })
  const [open, setOpen] = useState(false)
  return (
    <div className="space-y-2">
      {(list.data ?? []).length > 0 && <ul className="space-y-2">{list.data!.map((m) => <MeetingRow key={m.id} m={m} canCancel={canCreate} />)}</ul>}
      {canCreate && <Button size="sm" variant="outline" onClick={() => setOpen(true)}><Video className="size-4" /> {(list.data ?? []).length ? tx('Başka toplantı bağlantısı') : tx('Toplantı bağlantısı oluştur')}</Button>}
      {open && <NewMeetingDialog sourceType={sourceType} sourceId={sourceId} candidate={candidate} onClose={() => setOpen(false)} />}
    </div>
  )
}

function NewMeetingDialog({ sourceType, sourceId, candidate, onClose }: { sourceType: 'one-on-one' | 'interview'; sourceId: string; candidate?: boolean; onClose: () => void }) {
  const opts = useQuery({ queryKey: ['meeting-options'], queryFn: ({ signal }) => governanceApi.meetingOptions(signal) })
  const [provider, setProvider] = useState<MeetingProvider | ''>('')
  const [duration, setDuration] = useState('30')
  const [addToCalendars, setAdd] = useState(true)
  const [includeCandidate, setIncludeCandidate] = useState(true)
  const o = opts.data
  const choices: { value: MeetingProvider; label: string; enabled: boolean; why?: string }[] = [
    { value: 'zoom', label: tx('Zoom'), enabled: !!o?.zoom, why: tx('Yönetici Zoom entegrasyonunu açmadı') },
    { value: 'teams', label: tx('Microsoft Teams'), enabled: !!o?.teams, why: tx('Outlook (Microsoft 365) takviminizi bağlayın') },
    { value: 'google', label: tx('Google Meet'), enabled: !!o?.google, why: tx('Google Takvim\'inizi bağlayın') },
    { value: 'none', label: tx('Bağlantısız — yalnızca takvim daveti'), enabled: !!o?.calendar, why: tx('Takvim bağlı değil') },
  ]
  useEffect(() => { if (o && !provider) setProvider(choices.find((c) => c.enabled)?.value ?? '') }, [o]) // eslint-disable-line react-hooks/exhaustive-deps
  const create = useAction(() => governanceApi.createMeeting({ sourceType, sourceId, durationMinutes: Number(duration), provider: provider as MeetingProvider, addToCalendars, includeCandidate }), {
    success: (m) => (m.warnings?.length ? tx('Toplantı oluştu (uyarılar var)') : tx('Toplantı oluştu; davetler gönderildi')),
    invalidate: [['meetings']],
    onDone: onClose,
  })
  return (
    <Modal open onClose={onClose} title={tx('Toplantı bağlantısı')} note={tx('Görüşme saati kaydın kendisinden alınır.')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!provider || create.isPending} onClick={() => create.mutate(undefined)}>{create.isPending ? tx('Oluşturuluyor…') : tx('Oluştur')}</Button></>}>
      {opts.isPending ? <RowsSkeleton rows={3} /> : (
        <div className="space-y-4">
          <div className="grid gap-2">
            {choices.map((c) => (
              <button key={c.value} type="button" disabled={!c.enabled} onClick={() => setProvider(c.value)}
                className={cn('flex cursor-pointer items-center justify-between rounded-xl border px-3 py-2.5 text-left text-[13px] transition disabled:cursor-not-allowed disabled:opacity-50',
                  provider === c.value ? 'border-primary bg-primary/10' : 'border-border hover:bg-accent/40')}>
                <span className="font-medium">{c.label}</span>
                {!c.enabled && <span className="text-[11.5px] text-muted-foreground">{c.why}</span>}
              </button>
            ))}
          </div>
          <SelectField label={tx('Süre')} value={duration} onChange={setDuration} options={['15', '30', '45', '60', '90'].map((d) => ({ value: d, label: tx('{0} dakika', [d]) }))} />
          <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={addToCalendars} onCheckedChange={(v) => setAdd(v === true)} />{' '}{tx('Takvim daveti gönder')}</label>
          {candidate && <label className="flex items-center gap-2 text-[13px]"><Checkbox checked={includeCandidate} onCheckedChange={(v) => setIncludeCandidate(v === true)} />{' '}{tx('Adayı da davet et (e-postasına)')}</label>}
          {!o?.calendar && <InfoNote><CalendarClock className="mr-1 inline size-3.5" />{' '}{tx('Takvim davetleri için Profil › Takvim\'den Google ya da Outlook hesabınızı bağlayın.')}</InfoNote>}
        </div>
      )}
    </Modal>
  )
}
