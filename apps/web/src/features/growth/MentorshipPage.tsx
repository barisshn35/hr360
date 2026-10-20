import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Handshake, Sparkles } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/modal'
import { TextAreaField, TextField } from '@/components/ui/field'
import { StatusBadge } from '@/components/ui/status-badge'
import { EmptyState, RowsSkeleton } from '@/components/ui/states'
import { Tabs, useTabParam } from '@/components/ui/tabs'
import { engagementApi, type MentorMatch, type MentorshipStatus } from '@/api/engagement'
import { formatDate } from '@/lib/format'
import { ChipInput, Initials, TenantGate, useAction } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

const statusTone: Record<MentorshipStatus, 'warning' | 'success' | 'info' | 'neutral'> = { Requested: 'warning', Active: 'success', Completed: 'info', Declined: 'neutral' }
const statusLabel: Record<MentorshipStatus, string> = { Requested: tx('Talep edildi'), Active: tx('Aktif'), Completed: tx('Tamamlandı'), Declined: tx('Reddedildi') }

function ProfileForm() {
  const q = useQuery({ queryKey: ['mentor', 'me'], queryFn: ({ signal }) => engagementApi.myMentorProfile(signal) })
  const [f, setF] = useState({ isMentor: false, isMentee: true, offers: [] as string[], wants: [] as string[], capacity: '2', bio: '' })
  useEffect(() => {
    if (q.data) setF({ isMentor: q.data.isMentor, isMentee: q.data.isMentee, offers: q.data.offers, wants: q.data.wants, capacity: String(q.data.capacity), bio: q.data.bio ?? '' })
  }, [q.data])
  const save = useAction(() => engagementApi.saveMentorProfile({ ...f, capacity: Number(f.capacity) || 1, bio: f.bio || null }), { success: tx('Mentorluk profiliniz kaydedildi'), invalidate: [['mentor']] })
  return (
    <Panel>
      <PanelHead title={tx('Mentorluk profilim')} note={tx('Neyi öğretebileceğinizi ve neyi öğrenmek istediğinizi yazın; eşleştirme buna göre yapılır.')} />
      <PanelBody className="space-y-4">
        <div className="flex flex-wrap gap-5 text-[13.5px]">
          <label className="flex items-center gap-2"><Checkbox checked={f.isMentee} onCheckedChange={(v) => setF({ ...f, isMentee: v === true })} />{' '}{tx('Mentor arıyorum')}</label>
          <label className="flex items-center gap-2"><Checkbox checked={f.isMentor} onCheckedChange={(v) => setF({ ...f, isMentor: v === true })} />{' '}{tx('Mentor olabilirim')}</label>
        </div>
        {f.isMentee && <ChipInput id="wants" label={tx('Öğrenmek istediklerim')} value={f.wants} onChange={(v) => setF({ ...f, wants: v })} suggestions={[tx('Liderlik'), tx('Kubernetes'), tx('Sunum'), tx('Mimari'), tx('Müzakere'), tx('Proje yönetimi')]} />}
        {f.isMentor && (
          <>
            <ChipInput id="offers" label={tx('Öğretebileceklerim')} value={f.offers} onChange={(v) => setF({ ...f, offers: v })} />
            <div className="w-40"><TextField label={tx('Aynı anda kaç kişi?')} type="number" min={1} max={10} value={f.capacity} onChange={(e) => setF({ ...f, capacity: e.target.value })} /></div>
          </>
        )}
        <TextAreaField label={tx('Kısa tanıtım')} rows={2} value={f.bio} onChange={(e) => setF({ ...f, bio: e.target.value })} />
        <Button onClick={() => save.mutate(undefined)} disabled={save.isPending}>{tx('Kaydet')}</Button>
      </PanelBody>
    </Panel>
  )
}

function Matches() {
  const q = useQuery({ queryKey: ['mentor', 'matches'], queryFn: ({ signal }) => engagementApi.mentorMatches(signal) })
  const [pick, setPick] = useState<MentorMatch | null>(null)
  const [goal, setGoal] = useState('')
  const req = useAction(() => engagementApi.requestMentor({ mentorUserId: pick!.userId, goal, matchScore: pick!.score }), { success: tx('Talebiniz mentora iletildi'), invalidate: [['mentor']], onDone: () => setPick(null) })
  if (q.isPending) return <RowsSkeleton />
  if (!q.data?.length) return <EmptyState icon={Sparkles} title={tx('Henüz öneri yok')} detail={tx('Profilinizde öğrenmek istediğiniz konuları ekleyin; eşleşen mentorlar burada sıralanır.')} />
  return (
    <>
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {q.data.map((m, i) => (
          <motion.div key={m.userId} initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.06 }} className="surface relative overflow-hidden rounded-2xl border border-border p-5">
            <div className="flex items-center gap-3">
              <Initials name={m.personName} size={44} />
              <div className="min-w-0 flex-1">
                <p className="truncate text-[14.5px] font-semibold">{m.personName}</p>
                <p className="text-[12px] text-muted-foreground">{tx('{0} · {1} boş yer', [m.department ?? '—', m.freeSlots])}</p>
              </div>
              <div className="relative grid size-14 place-items-center">
                <svg viewBox="0 0 36 36" className="absolute inset-0 -rotate-90">
                  <circle cx="18" cy="18" r="15.5" fill="none" stroke="hsl(var(--muted))" strokeWidth="3" />
                  <motion.circle cx="18" cy="18" r="15.5" fill="none" stroke="hsl(var(--primary))" strokeWidth="3" strokeLinecap="round" strokeDasharray="97.4" initial={{ strokeDashoffset: 97.4 }} animate={{ strokeDashoffset: 97.4 * (1 - m.score / 100) }} transition={{ duration: 1, delay: 0.2 + i * 0.05 }} />
                </svg>
                <span className="tabular text-[13px] font-semibold">{m.score}</span>
              </div>
            </div>
            {m.bio && <p className="mt-3 line-clamp-2 text-[12.5px] text-muted-foreground">{m.bio}</p>}
            <p className="mt-3 text-[12px] text-muted-foreground">{tx('Ortak konular')}</p>
            <div className="mt-1 flex flex-wrap gap-1">{m.common.map((c) => <span key={c} className="rounded-full bg-primary/15 px-2 py-0.5 text-[11.5px] text-primary">{c}</span>)}</div>
            <Button className="mt-4 w-full" variant={m.pending ? 'outline' : 'default'} disabled={m.pending || m.freeSlots === 0} onClick={() => { setPick(m); setGoal('') }}>
              {m.pending ? tx('Talep gönderildi') : m.freeSlots === 0 ? tx('Kapasitesi dolu') : tx('Mentorluk iste')}
            </Button>
          </motion.div>
        ))}
      </div>
      {pick && (
        <Modal open onClose={() => setPick(null)} title={tx('{0} — mentorluk talebi', [pick.personName])} note={tx('Hedefinizi kısaca yazın; mentor kabul ederse eşleşme aktifleşir.')}
          footer={<><Button variant="outline" onClick={() => setPick(null)}>{tx('Vazgeç')}</Button><Button onClick={() => req.mutate(undefined)} disabled={req.isPending}>{tx('Gönder')}</Button></>}>
          <TextAreaField label={tx('Hedefim')} rows={3} value={goal} onChange={(e) => setGoal(e.target.value)} placeholder={tx('Örn. 6 ay içinde takım liderliğine hazırlanmak')} />
        </Modal>
      )}
    </>
  )
}

function MyMentorships() {
  const q = useQuery({ queryKey: ['mentor', 'list'], queryFn: ({ signal }) => engagementApi.mentorships(signal) })
  const act = useAction(({ id, a }: { id: string; a: 'accept' | 'decline' | 'complete' | 'cancel' }) => engagementApi.mentorshipAction(id, a), { success: tx('Güncellendi'), invalidate: [['mentor']] })
  if (q.isPending) return <RowsSkeleton />
  if (!q.data?.length) return <EmptyState icon={Handshake} title={tx('Henüz eşleşme yok')} detail={tx('Önerilen mentorlardan birine talep gönderin.')} />
  return (
    <Panel>
      <PanelBody className="p-0">
        <ul className="divide-y divide-border">
          {q.data.map((m) => (
            <li key={m.id} className="flex flex-wrap items-center gap-3 px-5 py-3.5">
              <div className="flex -space-x-2"><Initials name={m.mentorName} size={32} /><Initials name={m.menteeName} size={32} /></div>
              <div className="min-w-0 flex-1">
                <p className="text-[13.5px]"><b>{m.mentorName}</b> → {m.menteeName}</p>
                <p className="truncate text-[12px] text-muted-foreground">{tx('{0} · {1} · uyum {2}', [m.goal ?? tx('Hedef belirtilmedi'), formatDate(m.createdAt), m.matchScore])}</p>
              </div>
              <StatusBadge tone={statusTone[m.status]}>{statusLabel[m.status]}</StatusBadge>
              {m.iAmMentor && m.status === 'Requested' && (
                <>
                  <Button size="sm" onClick={() => act.mutate({ id: m.id, a: 'accept' })}>{tx('Kabul et')}</Button>
                  <Button size="sm" variant="outline" onClick={() => act.mutate({ id: m.id, a: 'decline' })}>{tx('Reddet')}</Button>
                </>
              )}
              {m.iAmMentee && m.status === 'Requested' && <Button size="sm" variant="outline" onClick={() => act.mutate({ id: m.id, a: 'cancel' })}>{tx('Geri çek')}</Button>}
              {m.status === 'Active' && (m.iAmMentor || m.iAmMentee) && <Button size="sm" variant="outline" onClick={() => act.mutate({ id: m.id, a: 'complete' })}>{tx('Tamamla')}</Button>}
            </li>
          ))}
        </ul>
      </PanelBody>
    </Panel>
  )
}

export function MentorshipPage() {
  const [tab, setTab] = useTabParam<'oneriler' | 'eslesmeler' | 'profil'>('sekme', 'oneriler')
  return (
    <TenantGate>
      <PageHeader title={tx('Mentorluk')} description={tx('Öğrenmek istediğiniz konuyu bilen bir çalışma arkadaşı bulun — eşleştirme puanı beceri kesişimi, kapasite ve departman çeşitliliğine göre.')} />
      <div className="mb-5"><Tabs label={tx('Mentorluk')} value={tab} onChange={setTab} tabs={[{ key: 'oneriler', label: tx('Önerilen mentorlar') }, { key: 'eslesmeler', label: tx('Eşleşmelerim') }, { key: 'profil', label: tx('Profilim') }]} /></div>
      {tab === 'oneriler' && <Matches />}
      {tab === 'eslesmeler' && <MyMentorships />}
      {tab === 'profil' && <ProfileForm />}
    </TenantGate>
  )
}
