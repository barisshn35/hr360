/**
 * 3B ekip ağı (Ekipler → Ekip ağı; yalnızca İK): ekipler arası etkileşim yoğunluğu —
 * takdir, birebir görüşme ve ortak hedef. Sunucu yalnızca EKİP düzeyinde veri döndürür;
 * 5 kişiden küçük ekipler "Diğer"de birleşir ya da gizlenir, 3'ten küçük sayılar gösterilmez
 * ve görüntüleme denetim kaydına yazılır. Konum istemcide kuvvet simülasyonuyla hesaplanır.
 * Bağ tablosu erişilebilir karşılıktır; 3B açılamazsa tek başına kalır.
 */

import { lazy, useCallback, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Network, ShieldCheck } from 'lucide-react'
import { governanceApi } from '@/api/governance'
import { Panel } from '@/components/ui/Panel'
import { EmptyState, ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { Button } from '@/components/ui/button'
import { formatDate, formatNumber } from '@/lib/format'
import { tx } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { Segmented, errorText } from '@/features/performance/components/controls'
import { deptColor, NEUTRAL } from './orgEncoding'
import { labelCandidates, type Node3D } from './org3d'
import { edgeKinds, neighborsOf, OTHER_ID, shapeTeamNetwork } from './teamNetworkModel'
import { ThreeDGate, ThreeDOffButton } from './ThreeDGate'

const Scene3D = lazy(() => import('./Scene3D'))

type Period = '30' | '90' | '180'

export function TeamNetworkView() {
  const [period, setPeriod] = useState<Period>('90')
  const [minWeight, setMinWeight] = useState(0)
  const [selected, setSelected] = useState<string | null>(null)
  const [center, setCenter] = useState<{ id: string; seq: number } | null>(null)
  const q = useQuery({
    queryKey: ['team-network', period],
    queryFn: ({ signal }) => governanceApi.teamNetwork(Number(period), signal),
    staleTime: 5 * 60_000,
  })
  const graph = useMemo(() => (q.data ? shapeTeamNetwork(q.data, minWeight) : null), [q.data, minWeight])
  const maxAll = useMemo(() => (q.data?.edges ?? []).reduce((m, e) => Math.max(m, e.weight), 0), [q.data])

  const nameOf = useCallback((id: string) => graph?.nodeById.get(id)?.name ?? id, [graph])
  const colorOf = useCallback((n: Node3D) => (n.id === OTHER_ID ? NEUTRAL : deptColor(n.colorIndex)), [])
  const labelOf = useCallback(
    (n: Node3D) => {
      const t = graph?.nodeById.get(n.id)
      if (!t) return { title: n.name }
      const sub =
        n.id === OTHER_ID
          ? tx('{0} küçük ekip birleşik · {1} kişi', [t.mergedTeams, formatNumber(t.members)])
          : tx('{0} üye{1}', [formatNumber(t.members), t.department ? ` · ${t.department}` : ''])
      return { title: t.name, sub }
    },
    [graph],
  )
  const neighbors = useMemo(() => (graph && selected ? neighborsOf(graph, selected) : []), [graph, selected])
  const highlight = useMemo(() => new Set(neighbors.map((x) => x.other)), [neighbors])
  const labelIds = useMemo(() => (graph ? labelCandidates(graph.scene.nodes, { selected, highlight }) : []), [graph, selected, highlight])

  const pick = (id: string, fly = false) => {
    setSelected((s) => (s === id ? null : id))
    if (fly) setCenter({ id, seq: Date.now() })
  }

  if (q.isPending) return <Panel><RowsSkeleton rows={6} columns={4} /></Panel>
  if (q.isError) return <Panel><ErrorState title={tx('Ekip ağı alınamadı')} message={errorText(q.error)} onRetry={() => void q.refetch()} /></Panel>
  const data = q.data
  if (!graph || data.nodes.length === 0) {
    return (
      <Panel>
        <EmptyState icon={Network} title={tx('Gösterilecek ekip yok')} detail={tx('Ağda yalnızca en az 5 üyeli etkin ekipler (ya da birleşince 5 kişiye ulaşan küçük ekipler) görünür.')} />
      </Panel>
    )
  }

  const sel = selected ? graph.nodeById.get(selected) : undefined

  const table = (
    <div className="max-h-[60vh] overflow-auto">
      <table className="w-full text-[12.5px]">
        <caption className="sr-only">{tx('Ekipler arası bağlar (en güçlüden)')}</caption>
        <thead className="sticky top-0 bg-card text-left text-[11.5px] text-muted-foreground">
          <tr>
            <th scope="col" className="px-2 py-1.5 font-medium">{tx('Ekip')}</th>
            <th scope="col" className="px-2 py-1.5 font-medium">{tx('Bağlı ekip')}</th>
            <th scope="col" className="px-2 py-1.5 text-right font-medium">{tx('Takdir')}</th>
            <th scope="col" className="px-2 py-1.5 text-right font-medium">{tx('1:1')}</th>
            <th scope="col" className="px-2 py-1.5 text-right font-medium">{tx('Ortak hedef')}</th>
            <th scope="col" className="px-2 py-1.5 text-right font-medium">{tx('Toplam')}</th>
          </tr>
        </thead>
        <tbody>
          {graph.edges.slice(0, 100).map((e) => {
            const on = selected !== null && (e.source === selected || e.target === selected)
            return (
              <tr key={`${e.source}>${e.target}`} className={cn('border-t border-border', on && 'bg-primary/5')}>
                <td className="px-2 py-1"><button type="button" className="text-left hover:underline" onClick={() => pick(e.source, true)}>{nameOf(e.source)}</button></td>
                <td className="px-2 py-1"><button type="button" className="text-left hover:underline" onClick={() => pick(e.target, true)}>{nameOf(e.target)}</button></td>
                <td className="tabular px-2 py-1 text-right">{e.kudos || '–'}</td>
                <td className="tabular px-2 py-1 text-right">{e.oneOnOnes || '–'}</td>
                <td className="tabular px-2 py-1 text-right">{e.sharedGoals || '–'}</td>
                <td className="tabular px-2 py-1 text-right font-medium">{e.weight}</td>
              </tr>
            )
          })}
          {graph.edges.length === 0 && (
            <tr>
              <td colSpan={6} className="px-3 py-3 text-muted-foreground">{tx('Bu dönemde gösterilebilecek ekipler arası bağ yok.')}</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <Segmented
          ariaLabel={tx('Dönem')}
          size="sm"
          value={period}
          onChange={(v) => {
            setPeriod(v)
            setSelected(null)
          }}
          options={[
            { value: '30', label: tx('Son 30 gün') },
            { value: '90', label: tx('Son 90 gün') },
            { value: '180', label: tx('Son 180 gün') },
          ]}
        />
        {maxAll > data.minEdgeCount && (
          <label className="flex items-center gap-2 text-[12.5px] text-muted-foreground">
            {tx('En az bağ gücü')}
            <input
              type="range"
              min={0}
              max={maxAll}
              value={minWeight}
              onChange={(e) => setMinWeight(Number(e.target.value))}
              className="w-32 accent-[hsl(var(--primary))]"
              aria-valuetext={String(Math.max(minWeight, data.minEdgeCount))}
            />
            <span className="tabular w-6">{Math.max(minWeight, data.minEdgeCount)}</span>
          </label>
        )}
        <span className="text-[12px] text-muted-foreground sm:ml-auto">
          {tx('{0} – bugün · {1} ekip · {2} bağ', [formatDate(data.since), formatNumber(data.nodes.length), formatNumber(graph.edges.length)])}
        </span>
      </div>

      <InfoNote>
        <ShieldCheck className="mr-1 inline size-3.5 align-[-2px]" aria-hidden />
        {tx('Yalnızca ekip düzeyinde toplu veri: {0} kişiden küçük ekipler "Diğer"de birleşir (o da küçükse gizlenir), {1}\'ten küçük sayılar gösterilmez. Görüntüleme denetim kaydına yazılır.', [data.minTeamSize, data.minEdgeCount])}
        {(data.hiddenTeams > 0 || data.hiddenEdges > 0) && ` ${tx('Gizlenen: {0} ekip, {1} bağ.', [data.hiddenTeams, data.hiddenEdges])}`}
      </InfoNote>

      <div className="overflow-hidden rounded-xl border border-border bg-card">
        <ThreeDGate fallback={table}>
          <div className="grid lg:grid-cols-[minmax(0,1fr)_400px]">
            <div className="relative">
              <Scene3D
                className="h-[62vh] min-h-[400px] bg-[radial-gradient(hsl(var(--border))_1px,transparent_1px)] [background-size:18px_18px]"
                data={graph.scene}
                colorOf={colorOf}
                labelOf={labelOf}
                labelIds={labelIds}
                selected={selected}
                highlight={highlight}
                linkStyle="tube"
                onPick={(id) => pick(id)}
                centerRequest={center}
                autoRotate={!selected}
                ariaLabel={tx('3B ekip ağı: küreler ekipler (boyut üye sayısı), çubuklar ekipler arası etkileşim (kalınlık yoğunluk). Ok tuşlarıyla döndürün. Bağlar yandaki tabloda da listelenir.')}
              />
              {sel && (
                <div className="absolute top-3 left-3 z-10 max-w-[min(360px,calc(100%-24px))] space-y-1 rounded-lg border border-border bg-card/95 p-2.5 text-[12px] shadow-md backdrop-blur">
                  <p className="font-semibold">{sel.name}</p>
                  <p className="text-muted-foreground">
                    {sel.id === OTHER_ID
                      ? tx('{0} küçük ekip birleşik · {1} kişi', [sel.mergedTeams, formatNumber(sel.members)])
                      : tx('{0} üye{1}', [formatNumber(sel.members), sel.department ? ` · ${sel.department}` : ''])}
                  </p>
                  {edgeKinds({ kudos: sel.internal.kudos, oneOnOnes: sel.internal.oneOnOnes, sharedGoals: sel.internal.sharedGoals }) && (
                    <p>
                      {tx('Ekip içi:')}{' '}
                      {edgeKinds({ kudos: sel.internal.kudos, oneOnOnes: sel.internal.oneOnOnes, sharedGoals: sel.internal.sharedGoals })}
                    </p>
                  )}
                  {neighbors.length > 0 ? (
                    <ul className="space-y-0.5">
                      {neighbors.slice(0, 5).map(({ other, edge }) => (
                        <li key={other}>
                          <button type="button" className="text-left text-primary hover:underline" onClick={() => pick(other, true)}>
                            {nameOf(other)}
                          </button>
                          <span className="text-muted-foreground"> · {edgeKinds(edge)}</span>
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p className="text-muted-foreground">{tx('Bu dönemde gösterilebilecek bağı yok.')}</p>
                  )}
                  <Button size="xs" variant="ghost" onClick={() => setSelected(null)}>{tx('Seçimi temizle')}</Button>
                </div>
              )}
              <div className="pointer-events-none absolute right-2 bottom-2 left-2 flex flex-wrap items-end justify-between gap-2 text-[11.5px] text-muted-foreground">
                <span className="flex flex-wrap gap-x-3 gap-y-1 rounded bg-card/85 px-1.5 py-0.5">
                  {graph.departments.slice(0, 8).map((d) => (
                    <span key={d.name} className="flex items-center gap-1">
                      <span aria-hidden className="size-2 rounded-full" style={{ background: deptColor(d.colorIndex) }} />
                      {d.name}
                    </span>
                  ))}
                </span>
                <span className="pointer-events-auto rounded bg-card/85">
                  <ThreeDOffButton />
                </span>
              </div>
            </div>
            <aside className="border-t border-border lg:border-t-0 lg:border-l" aria-label={tx('Ekipler arası bağlar')}>
              {table}
            </aside>
          </div>
        </ThreeDGate>
      </div>
    </div>
  )
}

export default TeamNetworkView
