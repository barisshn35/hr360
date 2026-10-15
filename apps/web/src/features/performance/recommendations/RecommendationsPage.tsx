/**
 * Aksiyon önerileri — `/panel/performans/oneriler`.
 *
 * Liste sırası backend'den "dikkat gerektiren önce" gelir (Acil → Gelişim →
 * Terfi → İzle → Takdir) ve YENİDEN SIRALANMAZ; tür süzgeci yalnızca gizler.
 * Üstte önerileri üreten ayar sürümü ve eşikler küçük bir satırda yazar.
 * ML katmanı ayrı durur; çalışmadıysa gerekçesi sessizce yutulmaz.
 */

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { BrainCircuit, GitCommitVertical, Lightbulb } from 'lucide-react'
import { actionLabelsFallback, toneOfAction, useEmployeeRecommendation, useRecommendations } from '@/api/performance'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { cn } from '@/lib/utils'
import { errorText } from '../components/controls'
import { PerfPageHeader } from '../components/PerfPageHeader'
import { TeamSelect } from '../components/pickers'
import { ThresholdTrack } from '../components/ThresholdTrack'
import { usePeople } from '../hooks'
import { RecommendationCard } from './RecommendationCard'
import { tx } from '@/lib/i18n'

const TONE_BG = {
  danger: 'hsl(var(--destructive))',
  warning: 'hsl(var(--warning))',
  neutral: 'hsl(var(--muted-foreground))',
  success: 'hsl(var(--success))',
  info: 'hsl(var(--primary))',
}

export function RecommendationsPage() {
  const people = usePeople()
  const [params, setParams] = useSearchParams()
  const teamId = params.get('ekip') ?? '__all__'
  const focus = params.get('calisan')
  const recs = useRecommendations(teamId === '__all__' ? undefined : teamId)
  const single = useEmployeeRecommendation(focus ?? undefined)
  const [hidden, setHidden] = useState<Set<string>>(new Set())

  const data = recs.data
  const items = data?.items ?? []
  const thresholds = data?.thresholds ?? single.data?.thresholds ?? null

  // Türler, backend listesindeki ilk görünme sırasıyla (yeniden sıralama yok).
  const groups = useMemo(() => {
    const order: string[] = []
    const count = new Map<string, number>()
    const label = new Map<string, string>()
    for (const r of items) {
      if (!count.has(r.action)) order.push(r.action)
      count.set(r.action, (count.get(r.action) ?? 0) + 1)
      label.set(r.action, r.actionLabel || (actionLabelsFallback as Record<string, string>)[r.action] || tx('Öneri'))
    }
    return order.map((a) => ({ action: a, count: count.get(a)!, label: label.get(a)! }))
  }, [items])

  const visible = items.filter((r) => !hidden.has(r.action))
  const focusInList = focus ? items.some((r) => r.employeeId === focus) : false

  useEffect(() => {
    if (!focus || !data) return
    const t = window.setTimeout(() => document.getElementById(`rec-${focus}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' }), 400)
    return () => window.clearTimeout(t)
  }, [focus, data])

  const set = (k: string, v: string | null) => {
    const p = new URLSearchParams(params)
    if (v) p.set(k, v)
    else p.delete(k)
    setParams(p, { replace: true })
  }

  return (
    <div className="mx-auto w-full max-w-6xl">
      <PerfPageHeader
        eyebrow={tx('Performans')}
        title={tx('Aksiyon önerileri')}
        description={tx('Her öneri açıklanabilir: hangi etkenin öneriyi desteklediği, hangisinin zayıflattığı yazılı. Kararı kural motoru verir; ML sinyalleri yalnızca ek bilgidir.')}
      >
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[12px] text-muted-foreground">
            {data ? (
              <>
                <span className="inline-flex items-center gap-1.5">
                  <GitCommitVertical className="size-3.5" aria-hidden />
                  {tx('Kural sürümü')}{' '}<span className="tabular font-semibold text-foreground">{data.configVersion ?? '—'}</span>
                </span>
                {thresholds && (
                  <span>
                    {tx('Eşikler: kritik')}{' '}<b className="tabular text-foreground">{thresholds.critical}</b>{' '}{tx('· gelişim')}{' '}<b className="tabular text-foreground">{thresholds.improvement}</b>{' '}{tx('· takdir', [])}{' '}
                    <b className="tabular text-foreground">{thresholds.recognition}</b>{' '}{tx('· terfi')}{' '}<b className="tabular text-foreground">{thresholds.promotion}</b>
                    {data.promotionConsecutivePeriods ? tx(' (üst üste {0} dönem)', [data.promotionConsecutivePeriods]) : ''}
                  </span>
                )}
              </>
            ) : (
              <Skeleton className="h-4 w-80" />
            )}
          </div>
          {thresholds && (
            <div className="max-w-2xl">
              <ThresholdTrack value={thresholds} dots={items.filter((r) => r.currentScore !== null).map((r) => ({ id: r.employeeId, label: people.nameOf(r.employeeId, r.employeeName), score: r.currentScore as number }))} />
            </div>
          )}
        </div>
      </PerfPageHeader>

      <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <TeamSelect className="w-64" value={teamId} onChange={(v) => set('ekip', v === '__all__' ? null : v)} allowAll />
        <div className="flex flex-wrap gap-1.5" role="group" aria-label={tx('Öneri türleri')}>
          {groups.map((g) => {
            const off = hidden.has(g.action)
            const color = TONE_BG[toneOfAction(g.action)]
            return (
              <button
                key={g.action}
                type="button"
                aria-pressed={!off}
                onClick={() =>
                  setHidden((s) => {
                    const n = new Set(s)
                    if (n.has(g.action)) n.delete(g.action)
                    else n.add(g.action)
                    return n
                  })
                }
                className={cn('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px] font-medium transition-all', off ? 'border-border text-muted-foreground opacity-60' : 'border-transparent')}
                style={off ? undefined : { background: `color-mix(in oklab, ${color} 12%, transparent)`, color }}
              >
                <span className="size-2 rounded-full" style={{ background: color }} />
                {g.label}
                <span className="tabular">{g.count}</span>
              </button>
            )
          })}
        </div>
      </div>

      {data?.mlLayer && (
        <motion.div initial={{ opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }} className="mb-4 flex items-start gap-3 rounded-xl border border-dashed border-border bg-muted/30 px-4 py-3">
          <BrainCircuit className="mt-0.5 size-4.5 shrink-0 text-muted-foreground" aria-hidden />
          <div className="text-[13px]">
            <p className="font-medium">{data.mlLayer.note}</p>
            {data.mlLayer.skipReason && (
              <p className="mt-0.5 text-muted-foreground">
                <span className="font-medium text-foreground">{tx('ML sinyalleri bu listede çalışmadı:')}</span> {data.mlLayer.skipReason}
              </p>
            )}
          </div>
        </motion.div>
      )}

      {focus && !focusInList && single.data && (
        <div className="mb-5">
          <p className="mb-2 text-[12px] font-semibold text-muted-foreground">{tx('Seçili çalışan')}</p>
          <RecommendationCard rec={single.data} name={people.nameOf(single.data.employeeId, single.data.employeeName)} thresholds={thresholds} index={0} highlighted />
        </div>
      )}

      {recs.isError ? (
        <Panel>
          <ErrorState title={tx('Öneriler alınamadı')} message={errorText(recs.error)} onRetry={() => void recs.refetch()} />
        </Panel>
      ) : recs.isPending ? (
        <div className="grid gap-4 lg:grid-cols-2" aria-busy="true">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-96 rounded-2xl" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <Panel>
          <EmptyState icon={Lightbulb} title={tx('Öneri yok')} detail={tx('Öneriler puanı olan çalışanlar için üretilir. Açık dönemde değerlendirmeler gönderildikçe burada belirir.')} />
        </Panel>
      ) : (
        <div className="grid items-start gap-4 lg:grid-cols-2">
          <AnimatePresence initial={false}>
            {visible.map((r, i) => (
              <RecommendationCard key={r.employeeId} rec={r} name={people.nameOf(r.employeeId, r.employeeName)} thresholds={thresholds} index={i} highlighted={r.employeeId === focus} />
            ))}
          </AnimatePresence>
        </div>
      )}
      {!recs.isPending && items.length > 0 && <p className="mt-4 text-[12px] text-muted-foreground">{tx('Sıralama backend\'den gelir: dikkat gerektirenler önce. Tür süzgeci sırayı değiştirmez, yalnızca gizler.')}</p>}
    </div>
  )
}
