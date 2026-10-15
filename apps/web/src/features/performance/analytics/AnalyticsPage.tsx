/**
 * Analiz — `/panel/performans/analiz`.
 *
 * İki farklı soruya iki sekme:
 *   Sürekli izleme  → "Son 3 ayda nasıl gidiyor?" (zaman serisi)
 *   Dönem sonuçları → "Q3'te ne aldı, Q2'ye göre nasıl?" (resmi sonuç)
 * Seçimler adreste tutulur; puan dökümünden "zaman içindeki seyri" ile
 * gelindiğinde ilgili kişi açık gelir.
 */

import { useEffect, useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { Activity, CalendarCheck } from 'lucide-react'
import { PERIODS, useTeams, useTeamsByEmployee, type AnalyticsPeriod } from '@/api/performance'
import { EASE } from '@/motion/primitives'
import { Segmented } from '../components/controls'
import { PerfPageHeader } from '../components/PerfPageHeader'
import { useCurrentCycle } from '../hooks'
import { ContinuousTab } from './ContinuousTab'
import { CycleTab } from './CycleTab'
import { tx } from '@/lib/i18n'

type Tab = 'izleme' | 'donem'

export function AnalyticsPage() {
  const [params, setParams] = useSearchParams()
  const tab: Tab = params.get('sekme') === 'donem' ? 'donem' : 'izleme'
  const teams = useTeams()
  const { cycles, current, isPending: cyclesPending } = useCurrentCycle()
  const employeeId = params.get('calisan')
  const empTeams = useTeamsByEmployee(employeeId ?? undefined)

  const set = (next: Record<string, string | null>) => {
    const p = new URLSearchParams(params)
    for (const [k, v] of Object.entries(next)) {
      if (v) p.set(k, v)
      else p.delete(k)
    }
    setParams(p, { replace: true })
  }

  const defaultTeam = useMemo(() => [...(teams.data ?? [])].filter((t) => t.isActive).sort((a, b) => b.memberCount - a.memberCount)[0]?.id ?? '', [teams.data])
  const teamParam = params.get('ekip')
  const teamId = teamParam ?? empTeams.data?.[0]?.id ?? defaultTeam
  const periodParam = params.get('aralik') as AnalyticsPeriod | null
  const period: AnalyticsPeriod = periodParam && PERIODS.includes(periodParam) ? periodParam : 'quarter'
  const cycleId = params.get('donem') ?? current?.id ?? ''
  const nonDraft = cycles.filter((c) => c.status !== 'Planned').sort((a, b) => a.startDate.localeCompare(b.startDate))
  const compareParam = params.get('karsilastir')
  const compareIds = compareParam ? compareParam.split(',').filter(Boolean) : nonDraft.slice(-3).map((c) => c.id)

  // Dönem sekmesinde ekip filtresi opsiyonel (varsayılan tüm şirket).
  const cycleTeam = params.get('donemEkip')

  useEffect(() => {
    if (tab === 'izleme' && !teamParam && teamId) set({ ekip: teamId })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, teamParam, teamId])

  return (
    <div className="mx-auto w-full max-w-7xl">
      <PerfPageHeader
        eyebrow={tx('Performans')}
        title={tx('Analiz')}
        description={tx('Sürekli izleme zaman içindeki gidişatı, dönem sonuçları resmi dönem puanlarını gösterir. Geçici noktalar kesik çizgi ve içi boş daireyle çizilir.')}
      >
        <Segmented
          ariaLabel={tx('Analiz türü')}
          value={tab}
          onChange={(v) => set({ sekme: v === 'donem' ? 'donem' : null })}
          options={[
            { value: 'izleme', label: <span className="inline-flex items-center gap-1.5"><Activity className="size-3.5" aria-hidden />{tx('Sürekli izleme')}</span> },
            { value: 'donem', label: <span className="inline-flex items-center gap-1.5"><CalendarCheck className="size-3.5" aria-hidden />{tx('Dönem sonuçları')}</span> },
          ]}
        />
      </PerfPageHeader>

      <AnimatePresence mode="wait">
        <motion.div key={tab} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -6 }} transition={{ duration: 0.35, ease: EASE }}>
          {tab === 'izleme' ? (
            <ContinuousTab
              teamId={teamId}
              onTeam={(id) => set({ ekip: id })}
              period={period}
              onPeriod={(p) => set({ aralik: p === 'quarter' ? null : p })}
              employeeId={employeeId}
              onEmployee={(id) => set({ calisan: id })}
            />
          ) : (
            <CycleTab
              cycles={cycles}
              cyclesPending={cyclesPending}
              cycleId={cycleId}
              onCycle={(id) => set({ donem: id })}
              teamId={cycleTeam}
              onTeam={(id) => set({ donemEkip: id })}
              compareIds={compareIds}
              onCompare={(ids) => set({ karsilastir: ids.join(',') || null })}
              employeeId={employeeId}
              onEmployee={(id) => set({ calisan: id })}
            />
          )}
        </motion.div>
      </AnimatePresence>
    </div>
  )
}
