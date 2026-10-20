import { Link, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Crown, MessageSquareQuote, Scale } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { Callout } from '@/components/ui/callout'
import { EmployeeAvatar } from '@/components/people/EmployeeAvatar'
import { useAction } from '@/features/shared/kit'
import { eomApi, eomKeys, periodLabel, unitLabel, visibilityNotice, type EomResultUnit } from '@/api/eom'
import { formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'

function ResolveTie({ unit }: { unit: EomResultUnit }) {
  const resolve = useAction((id: string) => eomApi.resolve(unit.id, id), { success: tx('Beraberlik çözüldü.'), invalidate: [eomKeys.all] })
  return (
    <Callout tone="warning" icon={Scale} title={tx('Beraberlik: kazananı seçin')}>
      <div className="mt-2 flex flex-wrap gap-2">
        {(unit.tied ?? []).map((p) => (
          <Button key={p.id} size="sm" variant="outline" disabled={resolve.isPending} onClick={() => resolve.mutate(p.id)}>
            <EmployeeAvatar id={p.id} name={p.name} version={p.photoVersion} size={20} />{p.name}
          </Button>
        ))}
      </div>
    </Callout>
  )
}

function UnitResult({ u }: { u: EomResultUnit }) {
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2">{unitLabel(u)}{u.stage === 2 && <span className="rounded-full bg-primary/10 px-2 text-[11px] text-primary">{tx('Final')}</span>}</span>}
        note={u.participation ? tx('Katılım: {0}/{1}', [u.participation.voted, u.participation.members]) : undefined} />
      <PanelBody className="space-y-4">
        {u.winner ? (
          <div className="flex items-center gap-3">
            <EmployeeAvatar id={u.winner.id} name={u.winner.name} version={u.winner.photoVersion} size={52} />
            <div>
              <p className="flex items-center gap-1.5 text-[15px] font-semibold"><Crown className="size-4 text-amber-400" />{u.winner.name}</p>
              <p className="text-[12.5px] text-muted-foreground">{u.winner.position ?? '—'}</p>
            </div>
          </div>
        ) : (
          <p className="text-[13px] text-muted-foreground">
            {u.status === 'NoVotes' ? tx('Bu birimde oy kullanılmadı.') : u.status === 'Tie' ? tx('Beraberlik var.') : tx('Oylama sürüyor.')}
          </p>
        )}
        {u.status === 'Tie' && u.tied && <ResolveTie unit={u} />}

        {u.ranking && u.ranking.length > 0 && (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-muted-foreground">
              <tr><th className="py-1.5 font-normal">#</th><th className="py-1.5 font-normal">{tx('Kişi')}</th><th className="py-1.5 text-right font-normal">{tx('Puan')}</th><th className="py-1.5 text-right font-normal">{tx('Oy')}</th></tr>
            </thead>
            <tbody>
              {u.ranking.map((r, i) => (
                <tr key={r.person.id} className="border-t border-border">
                  <td className="py-2 text-muted-foreground">{i + 1}</td>
                  <td className="py-2"><span className="flex items-center gap-2"><EmployeeAvatar id={r.person.id} name={r.person.name} version={r.person.photoVersion} size={24} />{r.person.name}</span></td>
                  <td className="py-2 text-right tabular-nums">{formatNumber(r.score)}</td>
                  <td className="py-2 text-right tabular-nums">{r.votes}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {u.votes && u.votes.length > 0 && (
          <div>
            <p className="mb-1.5 text-[12px] font-medium text-muted-foreground">{tx('Kim kime oy verdi')}</p>
            <ul className="divide-y divide-border rounded-xl border border-border text-[13px]">
              {u.votes.map((v, i) => (
                <li key={i} className="flex flex-wrap items-center gap-x-2 gap-y-1 px-3 py-2">
                  <span className="font-medium">{v.voter.name}</span><span className="text-muted-foreground">→</span><span>{v.nominee.name}</span>
                  {v.weight !== 1 && <span className="rounded bg-muted px-1.5 text-[11px] text-muted-foreground">×{formatNumber(v.weight)}</span>}
                  {v.comment && <span className="w-full text-[12.5px] text-muted-foreground">“{v.comment}”</span>}
                </li>
              ))}
            </ul>
          </div>
        )}

        {u.comments && u.comments.length > 0 && (
          <div>
            <p className="mb-1.5 flex items-center gap-1.5 text-[12px] font-medium text-muted-foreground"><MessageSquareQuote className="size-3.5" />{tx('Gerekçeler')}</p>
            <ul className="space-y-1.5">
              {u.comments.map((c, i) => (
                <li key={i} className="rounded-xl bg-muted/50 px-3 py-2 text-[13px]">
                  “{c.text}”{c.nominee && <span className="text-muted-foreground"> — {c.nominee.name}</span>}
                </li>
              ))}
            </ul>
          </div>
        )}
      </PanelBody>
    </Panel>
  )
}

export function EomResultsPage() {
  const { periodId = '' } = useParams()
  const q = useQuery({ queryKey: eomKeys.results(periodId), queryFn: ({ signal }) => eomApi.results(periodId, signal) })
  const p = q.data?.period
  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2" asChild>
        <Link to="/panel/ayin-calisani"><ArrowLeft className="size-4" />{tx('Ayın çalışanı')}</Link>
      </Button>
      <PageHeader title={p ? tx('{0} sonuçları', [periodLabel(p.key)]) : tx('Sonuçlar')} />
      {q.isPending ? <RowsSkeleton /> : q.isError ? <ErrorState message={q.error instanceof Error ? q.error.message : undefined} onRetry={() => void q.refetch()} /> : (
        <>
          {p && <InfoNote>{visibilityNotice(p)}</InfoNote>}
          <div className="grid gap-4 lg:grid-cols-2">
            {q.data.units.map((u) => <UnitResult key={u.id} u={u} />)}
          </div>
        </>
      )}
    </div>
  )
}
