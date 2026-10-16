import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CalendarCheck, ListPlus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { RowsSkeleton } from '@/components/ui/States'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { apiFetch } from '@/api/client'
import { useAction } from '@/features/shared/kit'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

export interface Checkin { id: string; employeeId: string; name?: string | null; weekStart: string; progress: string | null; blockers: string | null; nextWeek: string | null; mood: number; addedToOneOnOne: boolean; updatedAt: string }

export const checkinApi = {
  mine: (signal?: AbortSignal) => apiFetch<{ week: string; items: Checkin[] }>('/api/engagement/checkins/me', { signal }),
  save: (b: { progress?: string; blockers?: string; nextWeek?: string; mood: number }) => apiFetch<Checkin>('/api/engagement/checkins/me', { method: 'PUT', body: b }),
  team: (signal?: AbortSignal) => apiFetch<{ weeks: string[]; people: { employeeId: string; name: string; position: string | null; checkins: (Checkin | null)[] }[] }>('/api/engagement/checkins/team', { signal }),
  toOneOnOne: (id: string) => apiFetch<{ oneOnOneId: string; scheduledAt: string }>(`/api/engagement/checkins/${id}/to-one-on-one`, { method: 'POST' }),
}

export const MOODS = ['😣', '😕', '😐', '🙂', '😄']

function MyWeek({ current, week }: { current: Checkin | undefined; week: string }) {
  const [progress, setProgress] = useState(current?.progress ?? '')
  const [blockers, setBlockers] = useState(current?.blockers ?? '')
  const [nextWeek, setNextWeek] = useState(current?.nextWeek ?? '')
  const [mood, setMood] = useState(current?.mood ?? 0)
  const save = useAction(() => checkinApi.save({ progress: progress.trim() || undefined, blockers: blockers.trim() || undefined, nextWeek: nextWeek.trim() || undefined, mood }),
    { success: tx('Check-in kaydedildi.'), invalidate: [['checkins']] })
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><CalendarCheck className="size-4 text-primary" />{tx('{0} haftası', [formatDate(week)])}</span>}
        note={tx('İki dakikalık not; yöneticin görür. Hafta boyunca güncelleyebilirsin.')} />
      <PanelBody className="space-y-4">
        <div className="grid gap-4 lg:grid-cols-3">
          <TextAreaField label={tx('Bu hafta neler yaptın?')} rows={4} maxLength={1000} value={progress} onChange={(e) => setProgress(e.target.value)} />
          <TextAreaField label={tx('Seni yavaşlatan bir engel var mı?')} rows={4} maxLength={1000} value={blockers} onChange={(e) => setBlockers(e.target.value)} />
          <TextAreaField label={tx('Gelecek hafta odağın ne?')} rows={4} maxLength={1000} value={nextWeek} onChange={(e) => setNextWeek(e.target.value)} />
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <span className="text-[13px] text-muted-foreground">{tx('Bu hafta nasıldın?')}</span>
            <div role="radiogroup" aria-label={tx('Ruh hâli')} className="flex gap-1">
              {MOODS.map((m, i) => (
                <button key={m} type="button" role="radio" aria-checked={mood === i + 1} aria-label={String(i + 1)} onClick={() => setMood(i + 1)}
                  className={cn('size-10 cursor-pointer rounded-xl border text-[20px] transition', mood === i + 1 ? 'border-primary bg-primary/15 scale-110' : 'border-transparent opacity-60 hover:opacity-100')}>{m}</button>
              ))}
            </div>
          </div>
          <Button disabled={!mood || !(progress.trim() || blockers.trim() || nextWeek.trim()) || save.isPending} onClick={() => save.mutate(undefined)}>{current ? tx('Güncelle') : tx('Kaydet')}</Button>
        </div>
      </PanelBody>
    </Panel>
  )
}

function TeamView() {
  const q = useQuery({ queryKey: ['checkins', 'team'], queryFn: ({ signal }) => checkinApi.team(signal) })
  const add = useAction((id: string) => checkinApi.toOneOnOne(id), { success: (r) => tx('Engel {0} tarihli 1:1 gündemine eklendi.', [formatDate(r.scheduledAt)]), invalidate: [['checkins']] })
  if (!q.data?.people.length) return null
  return (
    <Panel>
      <PanelHead title={tx('Ekibimin check-in\'leri')} note={tx('Son dört hafta; engeli sıradaki 1:1 gündemine ekleyebilirsiniz.')} />
      <PanelBody className="overflow-x-auto p-0">
        <table className="w-full min-w-[720px] text-[13px]">
          <thead className="text-left text-[11.5px] text-muted-foreground">
            <tr><th className="px-5 py-2 font-normal">{tx('Çalışan')}</th>{q.data.weeks.map((w) => <th key={w} className="px-3 py-2 font-normal">{formatDate(w)}</th>)}</tr>
          </thead>
          <tbody>
            {q.data.people.map((p) => (
              <tr key={p.employeeId} className="border-t border-border align-top">
                <td className="px-5 py-2.5"><span className="flex items-center gap-2"><EmployeeAvatar id={p.employeeId} name={p.name} size={26} /><span className="font-medium">{p.name}</span></span></td>
                {p.checkins.map((c, i) => (
                  <td key={i} className="max-w-56 px-3 py-2.5">
                    {!c ? <span className="text-muted-foreground">—</span> : (
                      <div className="space-y-1">
                        <span className="text-[18px]" title={tx('Ruh hâli {0}/5', [c.mood])}>{MOODS[c.mood - 1]}</span>
                        {c.progress && <p className="line-clamp-3 text-[12.5px]">{c.progress}</p>}
                        {c.blockers && (
                          <div className="rounded-lg bg-amber-400/10 px-2 py-1 text-[12px]">
                            <p className="line-clamp-3"><b>{tx('Engel:')}</b> {c.blockers}</p>
                            {c.addedToOneOnOne ? <p className="mt-0.5 text-[11px] text-muted-foreground">{tx('1:1 gündeminde')}</p> : (
                              <button type="button" className="mt-0.5 flex cursor-pointer items-center gap-1 text-[11.5px] text-primary hover:underline" onClick={() => add.mutate(c.id)}>
                                <ListPlus className="size-3.5" />{tx('1:1 gündemine ekle')}
                              </button>
                            )}
                          </div>
                        )}
                      </div>
                    )}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </PanelBody>
    </Panel>
  )
}

/** Haftalık check-in (yenilik haritası #9). */
export function CheckinPage() {
  const q = useQuery({ queryKey: ['checkins', 'me'], queryFn: ({ signal }) => checkinApi.mine(signal) })
  const current = q.data?.items.find((c) => c.weekStart === q.data?.week)
  const history = (q.data?.items ?? []).filter((c) => c.weekStart !== q.data?.week)
  return (
    <div className="space-y-5">
      <PageHeader title={tx('Haftalık check-in')} eyebrow={[tx('Gelişim')]} description={tx('Haftada bir kısa not: yaptıkların, engellerin, gelecek haftan. Hedefler çeyrek sonunu beklemeden konuşulur.')} />
      {q.isPending ? <RowsSkeleton /> : <MyWeek key={current?.id ?? 'new'} current={current} week={q.data!.week} />}
      <TeamView />
      {history.length > 0 && (
        <Panel>
          <PanelHead title={tx('Önceki haftalarım')} />
          <PanelBody className="space-y-3">
            {history.map((c) => (
              <div key={c.id} className="rounded-xl border border-border p-3 text-[13px]">
                <p className="mb-1 flex items-center gap-2 font-medium">{MOODS[c.mood - 1]} {tx('{0} haftası', [formatDate(c.weekStart)])}</p>
                {c.progress && <p>{c.progress}</p>}
                {c.blockers && <p className="text-muted-foreground"><b>{tx('Engel:')}</b> {c.blockers}</p>}
              </div>
            ))}
          </PanelBody>
        </Panel>
      )}
    </div>
  )
}
