/**
 * Dönem sonuçları — "Q3'te ne aldı, Q2'ye göre nasıl?"
 * Seçili dönemin resmi sonuçları (dağılım, sıralama, puansızlar) ve
 * birden çok dönemin karşılaştırması (gruplu çubuk + yükselen/düşenler).
 * Bir kişi seçilince onun dönem geçmişi.
 */

import { useMemo } from 'react'
import { motion } from 'motion/react'
import { CalendarRange, Lock } from 'lucide-react'
import {
  thresholdsOf,
  useCycleCompare,
  useCycleResult,
  useEmployeeCycles,
  useScoringConfig,
  type ReviewCycle,
} from '@/api/performance'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { CHART, ChartLegend, GroupedBars, Histogram, TrendChart } from '../components/charts'
import { Chip, errorText } from '../components/controls'
import { MovementList, RankedMembers, SpreadNote, StatTile, UnscoredList } from '../components/insights'
import { CyclePicker, PersonSelect, TeamSelect } from '../components/pickers'
import { ZONES, zoneOf } from '../components/ThresholdTrack'
import { usePeople } from '../hooks'
import { tx, appLocale } from '@/lib/i18n'

const SERIES_COLORS = [CHART.c1, CHART.c2, CHART.c3, CHART.c4, CHART.c5]

export function CycleTab({
  cycles,
  cyclesPending = false,
  cycleId,
  onCycle,
  teamId,
  onTeam,
  compareIds,
  onCompare,
  employeeId,
  onEmployee,
}: {
  cycles: ReviewCycle[]
  /** Dönemler yüklenirken "dönem yok" boş durumu yerine yükleme göstergesi. */
  cyclesPending?: boolean
  cycleId: string
  onCycle: (id: string) => void
  teamId: string | null
  onTeam: (id: string | null) => void
  compareIds: string[]
  onCompare: (ids: string[]) => void
  employeeId: string | null
  onEmployee: (id: string | null) => void
}) {
  const people = usePeople()
  const cfg = useScoringConfig()
  const thresholds = cfg.data ? thresholdsOf(cfg.data) : null
  const cycle = cycles.find((c) => c.id === cycleId)
  const result = useCycleResult(cycleId || undefined, teamId ?? undefined)
  const selectable = cycles.filter((c) => c.status !== 'Planned').sort((a, b) => a.startDate.localeCompare(b.startDate))
  const compare = useCycleCompare(compareIds, teamId ?? undefined)
  const history = useEmployeeCycles(employeeId ?? undefined)
  const r = result.data

  const zoneColor = (b: { from?: number; to?: number }) =>
    !thresholds || b.from === undefined || b.to === undefined ? CHART.c2 : ZONES.find((z) => z.key === zoneOf((b.from! + b.to!) / 2, thresholds))!.color

  const bars = useMemo(() => {
    const c = compare.data
    if (!c) return null
    return {
      series: c.cycles.map((x, i) => ({ key: x.cycleId, label: x.cycleName, color: SERIES_COLORS[i % SERIES_COLORS.length] })),
      rows: c.rows.map((row) => {
        const full = people.nameOf(row.employeeId, row.name)
        return { name: full.split(' ')[0], fullName: full, ...row.scores }
      }),
    }
  }, [compare.data, people])

  const toggleCompare = (id: string) => {
    const next = compareIds.includes(id) ? compareIds.filter((x) => x !== id) : [...compareIds, id]
    onCompare(selectable.filter((c) => next.includes(c.id)).map((c) => c.id))
  }

  if (cyclesPending) {
    return (
      <div className="flex flex-col gap-5" aria-busy="true">
        <Skeleton className="h-10 w-full max-w-3xl rounded-xl" />
        <Skeleton className="h-80 rounded-2xl" />
      </div>
    )
  }

  if (!selectable.length) {
    return (
      <Panel>
        <EmptyState icon={CalendarRange} title={tx('Henüz sonuçlanan dönem yok')} detail={tx('Dönem sonuçları açık ya da kapanmış dönemler için görünür.')} />
      </Panel>
    )
  }

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-end gap-3">
        <CyclePicker className="w-60" cycles={cycles} value={cycleId} onChange={onCycle} />
        <TeamSelect className="w-60" value={teamId ?? '__all__'} onChange={(v) => onTeam(v === '__all__' ? null : v)} allowAll />
        <PersonSelect className="w-60" label={tx('Çalışan geçmişi')} value={employeeId ?? '__all__'} onChange={(v) => onEmployee(v === '__all__' ? null : v)} allowAll allLabel={tx('Seçilmedi')} />
        {cycle && (
          <Chip className="mb-2" tone={cycle.status === 'Closed' ? 'success' : 'warning'}>
            {cycle.status === 'Closed' ? (
              <>
                <Lock className="size-3" aria-hidden />
                {tx('Kesin sonuç')}
              </>
            ) : (
              tx('Dönem açık — sonuçlar değişebilir')
            )}
          </Chip>
        )}
      </div>

      {employeeId && (
        <motion.section initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.45, ease: EASE }}>
          <Panel className="p-5">
            <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-[14px] font-semibold">{tx('{0} — dönem geçmişi', [people.nameOf(employeeId)])}</h3>
              <ChartLegend items={[{ label: tx('Kesin'), color: CHART.c1 }, { label: tx('Geçici'), color: CHART.c1, hollow: true }]} />
            </div>
            {history.isPending ? (
              <Skeleton className="h-56 rounded-xl" />
            ) : history.isError ? (
              <p className="text-[12px] text-destructive">{errorText(history.error)}</p>
            ) : history.data!.cycles.length === 0 ? (
              <p className="py-6 text-center text-[13px] text-muted-foreground">{tx('Bu kişi için dönem sonucu yok.')}</p>
            ) : (
              <>
                <TrendChart data={history.data!.cycles.map((c) => ({ bucket: c.cycleName, score: c.score, isProvisional: c.isProvisional || !c.isFinal }))} color={CHART.c1} height={220} />
                <ul className="mt-3 flex flex-wrap gap-2">
                  {history.data!.cycles.map((c) => (
                    <li key={c.cycleId} className="rounded-lg border border-border px-2.5 py-1.5 text-[12px]">
                      <span className="text-muted-foreground">{c.cycleName}</span>{' '}
                      <span className="tabular font-semibold">{c.score === null ? '—' : c.score.toLocaleString(appLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</span>
                      {!c.isFinal && <span className="ml-1 text-[hsl(var(--warning))]">{tx('açık')}</span>}
                      {c.actionLabel && <span className="ml-1 text-muted-foreground">· {c.actionLabel}</span>}
                    </li>
                  ))}
                </ul>
              </>
            )}
          </Panel>
        </motion.section>
      )}

      {result.isError && (
        <Panel>
          <ErrorState title={tx('Dönem sonucu alınamadı')} message={errorText(result.error)} onRetry={() => void result.refetch()} />
        </Panel>
      )}
      {result.isPending && cycleId && <Skeleton className="h-80 rounded-2xl" aria-busy="true" />}

      {/* Puan yokken (ör. yeni açılmış dönem) 0'lı kutucuklar, "0–0" dağılım ve çelişen iletiler yerine tek boş durum. */}
      {r && r.members.length === 0 && (
        <Panel>
          <EmptyState
            icon={CalendarRange}
            title={tx('Bu dönemde henüz puan yok')}
            detail={r.unscored.length
              ? tx('{0} kişinin değerlendirmesi bekleniyor; puanlar geldikçe dağılım ve sıralama burada görünür.', [r.unscored.length])
              : tx('Dönem kapsamında puanlanacak çalışan bulunamadı.')}
          />
          {r.unscored.length > 0 && <div className="px-5 pb-5"><UnscoredList people={r.unscored} nameOf={people.nameOf} /></div>}
        </Panel>
      )}

      {r && r.members.length > 0 && (
        <>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <StatTile label={tx('Ortalama')} value={r.comparison.average} hint={r.cycleName} />
            <StatTile label={tx('Medyan')} value={r.comparison.median} />
            <StatTile label={tx('Yayılım (std. sapma)')} value={r.comparison.spread} />
            <StatTile label={tx('Puanlı çalışan')} value={r.members.length} format={(v) => String(Math.round(v))} hint={tx('{0} kişinin puanı yok', [r.unscored.length])} />
          </div>
          <SpreadNote note={r.comparison.spreadNote} />
          <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)]">
            <Panel className="p-5">
              <h3 className="mb-3 text-[14px] font-semibold">{tx('Dağılım')}</h3>
              <Histogram data={r.distribution} colorOf={zoneColor} />
            </Panel>
            <Panel className="p-5">
              <h3 className="mb-2 text-[14px] font-semibold">{tx('Sonuçlar')}</h3>
              <div className="max-h-80 overflow-y-auto pr-1">
                {r.members.length ? (
                  <RankedMembers members={r.members} thresholds={thresholds} nameOf={people.nameOf} selectedId={employeeId} onSelect={(id) => onEmployee(id === employeeId ? null : id)} />
                ) : (
                  <p className="py-6 text-center text-[13px] text-muted-foreground">{tx('Bu dönemde puan yok.')}</p>
                )}
              </div>
            </Panel>
          </div>
          <UnscoredList people={r.unscored} nameOf={people.nameOf} />
        </>
      )}

      {/* ------------------------------ karşılaştırma ------------------------------ */}
      <Panel className="p-5">
        <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
          <div>
            <h3 className="text-[15px] font-semibold">{tx('Dönem karşılaştırması')}</h3>
            <p className="text-[12px] text-muted-foreground">{tx('En az iki dönem seçin. Yükselen ve düşenler ilk ve son dönem arasında hesaplanır.')}</p>
          </div>
          <div className="flex flex-wrap gap-1.5">
            {selectable.map((c) => {
              const on = compareIds.includes(c.id)
              const idx = compareIds.indexOf(c.id)
              return (
                <button
                  key={c.id}
                  type="button"
                  onClick={() => toggleCompare(c.id)}
                  aria-pressed={on}
                  className={cn('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px] font-medium transition-colors', on ? 'border-transparent bg-foreground text-background' : 'border-border hover:border-primary/40')}
                >
                  {on && <span className="size-2 rounded-full" style={{ background: SERIES_COLORS[idx % SERIES_COLORS.length] }} />}
                  {c.name}
                </button>
              )
            })}
          </div>
        </div>

        {compareIds.length < 2 ? (
          <p className="rounded-lg border border-dashed border-border py-8 text-center text-[13px] text-muted-foreground">{tx('Karşılaştırmak için en az iki dönem seçin.')}</p>
        ) : compare.isPending ? (
          <Skeleton className="h-72 rounded-xl" />
        ) : compare.isError ? (
          <ErrorState title={tx('Karşılaştırma alınamadı')} message={errorText(compare.error)} onRetry={() => void compare.refetch()} />
        ) : (
          bars && (
            <>
              <div className="mb-3 flex flex-wrap gap-2">
                {compare.data!.cycles.map((c, i) => (
                  <span key={c.cycleId} className="inline-flex items-center gap-1.5 rounded-lg border border-border px-2.5 py-1 text-[12px]">
                    <span className="size-2 rounded-full" style={{ background: SERIES_COLORS[i % SERIES_COLORS.length] }} />
                    {tx('{0} ortalaması', [c.cycleName])}<span className="tabular font-semibold">{compare.data!.averages[c.cycleId]?.toLocaleString(appLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) ?? '—'}</span>
                  </span>
                ))}
              </div>
              <div className="-mx-2 overflow-x-auto px-2">
                <div style={{ minWidth: Math.max(560, bars.rows.length * 64) }}>
                  <GroupedBars rows={bars.rows} series={bars.series} height={300} />
                </div>
              </div>
              <div className="mt-5 grid gap-4 md:grid-cols-2">
                <MovementList title={tx('Yükselenler')} items={compare.data!.improved} kind="up" nameOf={people.nameOf} />
                <MovementList title={tx('Düşenler')} items={compare.data!.declined} kind="down" nameOf={people.nameOf} />
              </div>
            </>
          )
        )}
      </Panel>
    </div>
  )
}
