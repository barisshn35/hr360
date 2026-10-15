import { useSearchParams } from 'react-router-dom'
import { PageHeader } from '@/components/layout/PageHeader'
import { Tabs, useTabParam } from '@/components/ui/Tabs'
import { useShiftPatterns, useShiftTeams } from '@/api/queries-shift-engine'
import { PatternsView } from './PatternsView'
import { RosterView } from './RosterView'
import { TeamsView } from './TeamsView'
import { OptimizerView } from './OptimizerView'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { tx } from '@/lib/i18n'

type Tab = 'takvim' | 'ekipler' | 'desenler' | 'oneri'

/**
 * Vardiya motoru: döngüsel vardiya desenleri, bu desenleri takip eden ekipler
 * ve ekiplerin hesaplanmış takvimi.
 *
 * Mevcut Puantaj akışının (sabit vardiya + elle atama) yerine geçmez, yanına
 * eklenir: hiç desen tanımlamayan şirket bu sayfayı kullanmak zorunda değil.
 */
export function ShiftEnginePage() {
  const [tab, setTab] = useTabParam<Tab>('sekme', 'takvim')
  const [params, setParams] = useSearchParams()
  const teamId = params.get('ekip')
  const teams = useShiftTeams()
  const patterns = useShiftPatterns()
  const { roles, hasRole } = useAuth()
  // Öneri (madde 44): yalnızca planlayıcılar (yönetici ya da İK).
  const planner = hasRole('manager') || isHr(roles, 'ext-timeshift-manage')

  const selectTeam = (id: string, nextTab?: Tab) => {
    const next = new URLSearchParams(params)
    next.set('ekip', id)
    if (nextTab) {
      if (nextTab === 'takvim') next.delete('sekme')
      else next.set('sekme', nextTab)
    }
    setParams(next, { replace: true })
  }

  return (
    <div className="space-y-5">
      <PageHeader
        title={tx('Vardiya planı')}
        description={tx('Döngüsel vardiya desenlerini tanımlayın, ekipleri desenin farklı günlerinden başlatarak 7/24 kapsama kurun. Onaylanan izinler takvime kendiliğinden işlenir.')}
      />

      <Tabs<Tab>
        label={tx('Vardiya planı bölümleri')}
        value={tab}
        onChange={setTab}
        tabs={[
          { key: 'takvim', label: tx('Takvim') },
          { key: 'ekipler', label: tx('Ekipler'), count: teams.data?.length },
          { key: 'desenler', label: tx('Desenler'), count: patterns.data?.length },
          ...(planner ? [{ key: 'oneri' as const, label: tx('Öneri') }] : []),
        ]}
      />

      {tab === 'takvim' && <RosterView teamId={teamId} onTeamChange={(id) => selectTeam(id)} />}
      {tab === 'ekipler' && (
        <TeamsView
          selectedId={teamId}
          onSelect={(id) => selectTeam(id)}
          onOpenRoster={(id) => selectTeam(id, 'takvim')}
        />
      )}
      {tab === 'desenler' && <PatternsView onOpenTeam={(id) => selectTeam(id, 'ekipler')} />}
      {tab === 'oneri' && planner && <OptimizerView />}
    </div>
  )
}
