import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, CheckCircle2, Sprout } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/field'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { apiFetch } from '@/api/client'
import { useAction } from '@/features/shared/kit'
import { formatDate } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

export interface NewHireCheckin {
  id: string; employeeId: string; name: string; milestone: number; dueOn: string; sentAt: string; answeredAt: string | null
  belonging: number | null; clarity: number | null; support: number | null; comment: string | null; hireDate: string; department: string | null; lowScore: boolean
}

export const newHireApi = {
  mine: (signal?: AbortSignal) => apiFetch<NewHireCheckin[]>('/api/engagement/newhire/me', { signal }),
  team: (signal?: AbortSignal) => apiFetch<NewHireCheckin[]>('/api/engagement/newhire/team', { signal }),
  answer: (id: string, b: { belonging: number; clarity: number; support: number; comment?: string }) =>
    apiFetch<void>(`/api/engagement/newhire/${id}/answer`, { method: 'POST', body: b }),
}

const QUESTIONS = () => [
  { key: 'belonging' as const, text: tx('İşine ve ekibine ne kadar alıştın?') },
  { key: 'clarity' as const, text: tx('Rolün ve senden beklenenler net mi?') },
  { key: 'support' as const, text: tx('İhtiyacın olan destek ve araçlara sahip misin?') },
]

function Scale({ value, onChange, label }: { value: number; onChange: (n: number) => void; label: string }) {
  return (
    <div role="radiogroup" aria-label={label} className="flex gap-1.5">
      {[1, 2, 3, 4, 5].map((n) => (
        <button key={n} type="button" role="radio" aria-checked={value === n} onClick={() => onChange(n)}
          className={cn('size-10 cursor-pointer rounded-xl border text-[14px] font-semibold tabular-nums transition-colors',
            value === n ? 'border-primary bg-primary text-primary-foreground' : 'border-border hover:border-primary/50')}>{n}</button>
      ))}
    </div>
  )
}

function CheckinForm({ c }: { c: NewHireCheckin }) {
  const [v, setV] = useState({ belonging: 0, clarity: 0, support: 0 })
  const [comment, setComment] = useState('')
  const send = useAction(() => newHireApi.answer(c.id, { ...v, comment: comment.trim() || undefined }), { success: tx('Teşekkürler, yanıtın yöneticine iletildi.'), invalidate: [['newhire']] })
  const ok = v.belonging && v.clarity && v.support
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Sprout className="size-4 text-primary" />{tx('{0}. gün uyum formu', [c.milestone])}</span>}
        note={tx('1 = hiç, 5 = tamamen. Yanıtın yöneticine ve İK\'ya iletilir; anonim değildir.')} />
      <PanelBody className="space-y-5">
        {QUESTIONS().map((q) => (
          <div key={q.key} className="space-y-2">
            <p className="text-[14px] font-medium">{q.text}</p>
            <Scale label={q.text} value={v[q.key]} onChange={(n) => setV({ ...v, [q.key]: n })} />
          </div>
        ))}
        <TextAreaField label={tx('Yöneticine iletmek istediğin bir şey var mı? (isteğe bağlı)')} rows={3} maxLength={1000} value={comment} onChange={(e) => setComment(e.target.value)} />
        <div className="flex justify-end"><Button disabled={!ok || send.isPending} onClick={() => send.mutate(undefined)}>{tx('Gönder')}</Button></div>
      </PanelBody>
    </Panel>
  )
}

const score = (n: number | null) => (n == null ? '—' : <span className={cn('tabular-nums', n <= 2 && 'font-semibold text-destructive')}>{n}</span>)

/** İlk 90 gün takibi (yenilik haritası #8). */
export function NewHirePage() {
  const mine = useQuery({ queryKey: ['newhire', 'me'], queryFn: ({ signal }) => newHireApi.mine(signal) })
  const team = useQuery({ queryKey: ['newhire', 'team'], queryFn: ({ signal }) => newHireApi.team(signal) })
  const pending = (mine.data ?? []).filter((c) => !c.answeredAt)
  return (
    <div className="space-y-5">
      <PageHeader title={tx('İlk 90 gün')} eyebrow={[tx('Gelişim')]}
        description={tx('Yeni çalışanlar 30., 60. ve 90. günlerinde kısa bir uyum formu doldurur; yöneticileri hatırlatılır ve yanıtları görür.')} />
      {pending.map((c) => <CheckinForm key={c.id} c={c} />)}
      {(mine.data ?? []).some((c) => c.answeredAt) && !pending.length && (
        <InfoNote><span className="flex items-center gap-2"><CheckCircle2 className="size-4 text-primary" />{tx('Bekleyen formun yok; teşekkürler.')}</span></InfoNote>
      )}
      {team.isPending ? <RowsSkeleton /> : (team.data ?? []).length > 0 ? (
        <Panel>
          <PanelHead title={tx('Ekibimdeki yeni çalışanlar')} note={tx('Son 180 gün. 2 ve altı puanlar işaretlenir.')} />
          <PanelBody className="overflow-x-auto p-0">
            <table className="w-full text-[13px]">
              <thead className="text-left text-[11.5px] text-muted-foreground">
                <tr><th className="px-5 py-2 font-normal">{tx('Çalışan')}</th><th className="px-3 py-2 font-normal">{tx('Gün')}</th><th className="px-3 py-2 font-normal">{tx('Alışma')}</th><th className="px-3 py-2 font-normal">{tx('Netlik')}</th><th className="px-3 py-2 font-normal">{tx('Destek')}</th><th className="px-5 py-2 font-normal">{tx('Not')}</th></tr>
              </thead>
              <tbody>
                {team.data!.map((c) => (
                  <tr key={c.id} className={cn('border-t border-border', c.lowScore && 'bg-destructive/5')}>
                    <td className="px-5 py-2"><span className="flex items-center gap-2"><EmployeeAvatar id={c.employeeId} name={c.name} size={26} />
                      <span><span className="block font-medium">{c.name}</span><span className="block text-[11.5px] text-muted-foreground">{c.department ?? '—'} · {tx('işe giriş {0}', [formatDate(c.hireDate)])}</span></span></span></td>
                    <td className="px-3 py-2 tabular-nums">{c.milestone}</td>
                    {c.answeredAt ? <>
                      <td className="px-3 py-2">{score(c.belonging)}</td><td className="px-3 py-2">{score(c.clarity)}</td><td className="px-3 py-2">{score(c.support)}</td>
                      <td className="px-5 py-2 text-muted-foreground">{c.lowScore && <AlertTriangle className="mr-1 inline size-3.5 text-destructive" />}{c.comment ?? '—'}</td>
                    </> : <td colSpan={4} className="px-3 py-2 text-muted-foreground">{tx('Henüz yanıtlanmadı')}</td>}
                  </tr>
                ))}
              </tbody>
            </table>
          </PanelBody>
        </Panel>
      ) : !pending.length && !(mine.data ?? []).length && (
        <EmptyState icon={Sprout} title={tx('Şu an takip edilen yeni çalışan yok')} detail={tx('İşe girişinin 30., 60. ya da 90. gününe gelen çalışanlar burada görünür.')} />
      )}
    </div>
  )
}
