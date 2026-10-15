import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Briefcase, GraduationCap, Sparkles, Users } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { ml10Api, type Recommendation } from '@/api/wave10'
import { errMsg } from '@/features/shared/kit'
import { tx } from '@/lib/i18n'

/**
 * Dalga 10 (madde 49): "Sana uygun" — beceri vektörü ve yetkinlik açıklarına göre iç ilan, mentor ve eğitim
 * önerileri, gerekçeleriyle. Yalnızca kişinin kendisine gösterilir; başvuru, mentorluk talebi ya da eğitim kaydı
 * otomatik yapılmaz (ilgili ekrana bağlantı verilir).
 */
function Column<T>({ title, icon: Icon, items, empty, render, to, toLabel }: {
  title: string; icon: typeof Briefcase; items: Recommendation<T>[]; empty: string
  render: (r: Recommendation<T>) => ReactNode; to: string; toLabel: string
}) {
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Icon className="size-4 text-primary" />{' '}{title}</span>} action={<Link to={to} className="text-[12px] text-primary hover:underline">{toLabel}</Link>} />
      <PanelBody className="space-y-3">
        {items.length === 0 ? <p className="text-[13px] text-muted-foreground">{empty}</p> : items.map((r) => (
          <div key={r.id} className="rounded-xl border border-border p-3">
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">{render(r)}</div>
              <StatusBadge tone={r.score >= 70 ? 'success' : r.score >= 40 ? 'info' : 'neutral'}>{tx('%{0} uygun', [r.score])}</StatusBadge>
            </div>
            <ul className="mt-2 list-disc space-y-0.5 pl-4 text-[12px] text-muted-foreground">{r.reasons.map((x) => <li key={x}>{x}</li>)}</ul>
          </div>
        ))}
      </PanelBody>
    </Panel>
  )
}

export function ForYouPanel() {
  const q = useQuery({ queryKey: ['growth', 'recommendations'], queryFn: ({ signal }) => ml10Api.recommendations(signal), staleTime: 5 * 60_000 })
  if (q.isPending) return <RowsSkeleton rows={4} />
  if (q.isError) return <ErrorState message={errMsg(q.error)} onRetry={() => void q.refetch()} />
  const d = q.data
  if (!d.linked) return <EmptyState icon={Sparkles} title={tx('Öneri için çalışan kaydı gerekir')} detail={tx('Hesabınız bir çalışan kaydına bağlı değil.')} />
  return (
    <div className="space-y-4">
      <InfoNote>
        {d.note ?? tx('Öneridir; başvuru, mentorluk talebi ya da eğitim kaydı otomatik yapılmaz. Öneriler yalnızca size gösterilir.')}{' '}
        {d.skillsUsed && d.skillsUsed.length === 0 ? tx('Profilinize beceri eklerseniz öneriler isabetlenir.') : ''}
      </InfoNote>
      {d.gaps.length > 0 && (
        <div className="flex flex-wrap items-center gap-2 text-[12.5px]">
          <span className="text-muted-foreground">{tx('Gelişim alanlarınız:')}</span>
          {d.gaps.map((g) => <StatusBadge key={g.id} tone="warning">{tx('{0} ({1}/{2})', [g.name, g.current, g.required])}</StatusBadge>)}
        </div>
      )}
      <div className="grid gap-4 xl:grid-cols-3">
        <Column title={tx('İç ilanlar')} icon={Briefcase} items={d.postings} empty={tx('Becerilerinize uyan açık ilan yok.')} to="/panel/ic-ilanlar" toLabel={tx('Tüm ilanlar')}
          render={(r) => <><p className="text-[13.5px] font-medium">{r.info.title}</p><p className="text-[12px] text-muted-foreground">{r.info.department ?? tx('Genel')}{r.info.applied ? ` · ${tx('Başvurdunuz')}` : ''}</p></>} />
        <Column title={tx('Mentorlar')} icon={Users} items={d.mentors} empty={tx('Hedeflerinize uyan boş kapasiteli mentor yok.')} to="/panel/mentorluk" toLabel={tx('Mentorluk')}
          render={(r) => <><p className="text-[13.5px] font-medium">{r.info.name}</p><p className="text-[12px] text-muted-foreground">{r.info.department ?? '—'}</p></>} />
        <Column title={tx('Eğitimler')} icon={GraduationCap} items={d.courses} empty={tx('Gelişim alanlarınıza uyan eğitim yok.')} to="/panel/egitim" toLabel={tx('Eğitim kataloğu')}
          render={(r) => <><Link to={`/panel/egitim/${r.id}`} className="text-[13.5px] font-medium hover:text-primary hover:underline">{r.info.title}</Link><p className="text-[12px] text-muted-foreground">{r.info.category ?? ''}</p></>} />
      </div>
    </div>
  )
}
