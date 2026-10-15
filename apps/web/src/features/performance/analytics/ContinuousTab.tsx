/**
 * Sürekli izleme — "Son 3 ayda nasıl gidiyor?"
 * Ekip trendi, dağılım, sıralama (yayılım notuyla) ve puansızlar; bir kişi
 * seçilince onun trendi ve ekip ortalamasıyla karşılaştırması.
 */

import { AnimatePresence, motion } from 'motion/react'
import { Info, Users } from 'lucide-react'
import {
  PERIODS,
  formatScore,
  periodLabels,
  thresholdsOf,
  useEmployeeAnalytics,
  useScoringConfig,
  useTeamAnalytics,
  useTeamsByEmployee,
  useVsTeam,
  type AnalyticsPeriod,
} from '@/api/performance'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { CHART, ChartLegend, DualTrendChart, Histogram, TrendChart } from '../components/charts'
import { Chip, Segmented, errorText } from '../components/controls'
import { RankedMembers, SpreadNote, StatTile, UnscoredList } from '../components/insights'
import { PersonSelect, TeamSelect } from '../components/pickers'
import { ZONES, zoneOf } from '../components/ThresholdTrack'
import { usePeople } from '../hooks'
import { tx } from '@/lib/i18n'

export function ContinuousTab({
  teamId,
  onTeam,
  period,
  onPeriod,
  employeeId,
  onEmployee,
}: {
  teamId: string
  onTeam: (id: string) => void
  period: AnalyticsPeriod
  onPeriod: (p: AnalyticsPeriod) => void
  employeeId: string | null
  onEmployee: (id: string | null) => void
}) {
  const people = usePeople()
  const cfg = useScoringConfig()
  const thresholds = cfg.data ? thresholdsOf(cfg.data) : null
  const team = useTeamAnalytics(teamId || undefined, period)
  const t = team.data
  const memberIds = t ? [...t.members.map((m) => m.employeeId), ...t.unscored.map((u) => u.employeeId)] : undefined
  // Yeni oluşturulmuş, henüz üyesi olmayan ekip: boş grafikler yerine yönlendirici bir boş durum.
  const emptyTeam = Boolean(t && t.members.length === 0 && t.unscored.length === 0)

  const zoneColor = (b: { from?: number; to?: number }) => {
    if (!thresholds || b.from === undefined || b.to === undefined) return CHART.c2
    return ZONES.find((z) => z.key === zoneOf((b.from! + b.to!) / 2, thresholds))!.color
  }

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div className="flex flex-wrap items-end gap-3">
          <TeamSelect className="w-60" value={teamId} onChange={(v) => { onTeam(v); onEmployee(null) }} />
          <PersonSelect className="w-60" label={tx('Çalışan')} value={employeeId ?? '__all__'} onChange={(v) => onEmployee(v === '__all__' ? null : v)} allowAll allLabel={tx('Ekip geneli')} only={memberIds} />
        </div>
        <Segmented ariaLabel={tx('Dönem aralığı')} value={period} onChange={onPeriod} options={PERIODS.map((p) => ({ value: p, label: periodLabels[p] }))} size="sm" />
      </div>

      {team.isError && (
        <Panel>
          <ErrorState title={tx('Ekip analizi alınamadı')} message={errorText(team.error)} onRetry={() => void team.refetch()} />
        </Panel>
      )}
      {team.isPending && teamId && (
        <div className="grid gap-4 lg:grid-cols-3" aria-busy="true">
          <Skeleton className="h-72 rounded-2xl lg:col-span-2" />
          <Skeleton className="h-72 rounded-2xl" />
        </div>
      )}
      {!teamId && (
        <Panel>
          <EmptyState icon={Users} title={tx('Bir ekip seçin')} detail={tx('Sürekli izleme ekip bazında çalışır. Ekip yoksa önce Ekipler ekranından oluşturun.')} />
        </Panel>
      )}

      {emptyTeam && (
        <Panel>
          <EmptyState icon={Users} title={tx('Bu ekipte henüz üye yok')} detail={tx('Ekip analizi üyelerin puanlarından hesaplanır. Önce Ekipler ekranından bu ekibe üye ekleyin.')} />
        </Panel>
      )}

      {t && !emptyTeam && (
        <>
          <AnimatePresence mode="wait">
            {employeeId && <EmployeeBlock key={`${employeeId}-${period}`} employeeId={employeeId} teamId={teamId} period={period} />}
          </AnimatePresence>

          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <StatTile label={tx('Ekip ortalaması')} value={t.comparison.average} hint={t.periodLabel} />
            <StatTile label={tx('Medyan')} value={t.comparison.median} />
            <StatTile label={tx('Yayılım (std. sapma)')} value={t.comparison.spread} hint={tx('Küçükse sıralama zayıf bilgi taşır')} />
            <StatTile label={tx('Puanlı üye')} value={t.members.length} format={(v) => String(Math.round(v))} hint={tx('{0} üyenin puanı yok', [t.unscored.length])} />
          </div>

          <SpreadNote note={t.comparison.spreadNote} />

          <div className="grid gap-5 lg:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)]">
            <Panel className="p-5">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <h3 className="text-[14px] font-semibold">{tx('{0} — ortalama trend', [t.teamName ?? tx('Ekip')])}</h3>
                <ChartLegend items={[{ label: tx('Kesin'), color: CHART.c1 }, { label: tx('Geçici (az değerlendirme)'), color: CHART.c1, hollow: true }]} />
              </div>
              <TrendChart
                data={t.series}
                color={CHART.c1}
                label={tx('Ekip ortalaması')}
                thresholds={thresholds ? [{ value: thresholds.recognition, label: 'takdir' }, { value: thresholds.improvement, label: tx('gelişim') }] : undefined}
              />
            </Panel>
            <Panel className="p-5">
              <h3 className="mb-3 text-[14px] font-semibold">{tx('Puan dağılımı')}</h3>
              <Histogram data={t.distribution} colorOf={zoneColor} />
              <p className="mt-2 text-[11px] text-muted-foreground">{tx('Çubuk rengi aralığın düştüğü eşik bölgesini gösterir.')}</p>
            </Panel>
          </div>

          <div className="grid gap-5 lg:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)]">
            <Panel className="p-5">
              <div className="mb-2 flex items-center justify-between gap-2">
                <h3 className="text-[14px] font-semibold">{tx('Üyeler')}</h3>
                <span className="text-[11px] text-muted-foreground">{tx('Kişiye tıklayıp trendini açın')}</span>
              </div>
              {t.members.length ? (
                <RankedMembers members={t.members} thresholds={thresholds} nameOf={people.nameOf} selectedId={employeeId} onSelect={(id) => onEmployee(id === employeeId ? null : id)} />
              ) : (
                <p className="py-6 text-center text-[13px] text-muted-foreground">{tx('Bu aralıkta puanlı üye yok.')}</p>
              )}
            </Panel>
            <UnscoredList people={t.unscored} nameOf={people.nameOf} />
          </div>
        </>
      )}
    </div>
  )
}

function EmployeeBlock({ employeeId, teamId, period }: { employeeId: string; teamId: string; period: AnalyticsPeriod }) {
  const people = usePeople()
  const emp = useEmployeeAnalytics(employeeId, period)
  const vs = useVsTeam(employeeId, period, teamId)
  const teams = useTeamsByEmployee(employeeId)
  const e = emp.data
  const name = people.nameOf(employeeId)
  const comparedTeam = vs.data?.teamName ?? teams.data?.find((x) => x.id === (vs.data?.teamId ?? teamId))?.name ?? null

  return (
    <motion.section initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -8 }} transition={{ duration: 0.45, ease: EASE }} className="rounded-2xl border border-primary/25 bg-card p-5">
      <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
        <div>
          <p className="text-[12px] font-semibold text-primary">{tx('Çalışan')}</p>
          <h3 className="text-[18px] font-semibold">{name}</h3>
        </div>
        {e && (
          <div className="flex items-end gap-5">
            <div className="text-right">
              <p className="text-[11px] text-muted-foreground">{tx('Şu an')}</p>
              <p className="tabular text-[22px] leading-none font-semibold">{formatScore(e.current)}</p>
            </div>
            <div className="text-right">
              <p className="text-[11px] text-muted-foreground">{tx('Değişim')}</p>
              <p className={cn('tabular text-[16px] font-semibold', (e.change ?? 0) > 0 ? 'text-[hsl(var(--success))]' : (e.change ?? 0) < 0 ? 'text-destructive' : 'text-muted-foreground')}>
                {e.change === null ? '—' : `${e.change > 0 ? '+' : e.change < 0 ? '−' : ''}${formatScore(Math.abs(e.change))}`}
              </p>
            </div>
            {e.trendLabel && <Chip tone={e.trend?.includes('Down') ? 'danger' : e.trend?.includes('Up') ? 'success' : 'muted'}>{e.trendLabel}</Chip>}
          </div>
        )}
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <div>
          <p className="mb-2 text-[12px] font-medium text-muted-foreground">{tx('Kişisel trend · {0}', [e?.periodLabel])}</p>
          {emp.isPending ? <Skeleton className="h-60 rounded-xl" /> : emp.isError ? <p className="text-[12px] text-destructive">{errorText(emp.error)}</p> : <TrendChart data={e!.series} color={CHART.c1} label={name} />}
        </div>
        <div>
          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
            <p className="text-[12px] font-medium text-muted-foreground">{tx('Ekip ortalamasıyla')}</p>
            <ChartLegend items={[{ label: name, color: CHART.c1 }, { label: tx('Ekip ortalaması'), color: CHART.c2 }]} />
          </div>
          {vs.isPending ? (
            <Skeleton className="h-60 rounded-xl" />
          ) : vs.isError ? (
            <p className="text-[12px] text-destructive">{errorText(vs.error)}</p>
          ) : (
            <DualTrendChart data={vs.data!.series.map((p) => ({ bucket: p.bucket, a: p.employee, b: p.team, isProvisional: p.isProvisional }))} aLabel={name} bLabel={tx('Ekip ortalaması')} height={240} />
          )}
        </div>
      </div>

      <div className="mt-4 flex flex-wrap items-start gap-2 rounded-lg bg-muted/50 px-3 py-2 text-[12px]">
        <Info className="mt-0.5 size-3.5 shrink-0 text-primary" aria-hidden />
        <span className="text-muted-foreground">
          {tx('Karşılaştırma')}{' '}<strong className="font-medium text-foreground">{comparedTeam ?? tx('çalışanın ekibi')}</strong>{' '}{tx('ortalamasıyla yapıldı.')}
          {(teams.data?.length ?? 0) > 1 && tx(' Çalışan birden fazla ekipte; analiz kaydı yalnızca ilk ekibi tutuyor, diğer ekiplerle kıyaslanmıyor.')}
        </span>
        {teams.data && teams.data.length > 0 && (
          <span className="flex flex-wrap gap-1">
            {teams.data.map((x) => (
              <Chip key={x.id} tone={x.name === comparedTeam ? 'primary' : 'muted'}>
                {x.name}
              </Chip>
            ))}
          </span>
        )}
      </div>
    </motion.section>
  )
}
