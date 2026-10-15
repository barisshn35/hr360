/**
 * Puan dökümü — `/panel/performans/puan?calisan=&donem=`.
 *
 * "Puanın nereden geldiğini göstermezsen kimse güvenmez." Üstte nihai puan,
 * bant ve bileşim çubuğu (her katmanın katkısı), altında katmanlı ağaç ve
 * dipnotlar. Geçici puanda net bir uyarı bandı çıkar. Çalışan yalnızca kendi
 * puanını görür.
 */

import { useMemo } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { motion } from 'motion/react'
import { ClipboardList, Gauge, Info, Lightbulb, Lock } from 'lucide-react'
import { categoryColor, categoryLabels, formatScore, reviewTypeLabels, thresholdsOf, useMyEmployeeId, useScore, useScoringConfig, useScoringHistory } from '@/api/performance'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { EmptyState, ErrorState } from '@/components/ui/States'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { AnimatedNumber } from '../components/AnimatedNumber'
import { Chip, errorText } from '../components/controls'
import { PerfPageHeader } from '../components/PerfPageHeader'
import { CyclePicker, PersonSelect } from '../components/pickers'
import { BandBadge, ProvisionalBanner, ScoreRing } from '../components/score'
import { useCurrentCycle, usePeople } from '../hooks'
import { explain } from './explain'
import { ScoreTree } from './ScoreTree'
import { tx, appLocale } from '@/lib/i18n'

export function ScorePage() {
  const { can } = useAuth()
  const manager = can('performance:manage')
  const me = useMyEmployeeId()
  const people = usePeople()
  const { cycles, current } = useCurrentCycle()
  const [params, setParams] = useSearchParams()
  const employeeId = manager ? (params.get('calisan') ?? me.employeeId ?? '') : (me.employeeId ?? '')
  const cycleId = params.get('donem') ?? current?.id ?? ''
  const cycle = cycles.find((c) => c.id === cycleId)

  const score = useScore(employeeId || undefined, cycleId || undefined)
  // Ayar geçmişi yönetici verisi; çalışan için istenmez (403 dönüyordu).
  const history = useScoringHistory(can('performance:manage'))
  const currentCfg = useScoringConfig()

  // Eşikler ve (yanıtta yoksa) paylar için puanın hesaplandığı ayar sürümü.
  const cfg = useMemo(() => {
    const v = score.data?.configVersion
    return history.data?.find((h) => h.version === v) ?? (currentCfg.data?.version === v ? currentCfg.data : null)
  }, [history.data, currentCfg.data, score.data?.configVersion])

  const thresholds = cfg ? thresholdsOf(cfg) : currentCfg.data ? thresholdsOf(currentCfg.data) : null
  const ex = score.data ? explain(score.data, cfg ?? null) : null
  const setParam = (k: string, v: string) => {
    const p = new URLSearchParams(params)
    p.set(k, v)
    setParams(p, { replace: true })
  }

  if (!manager && me.notLinked) {
    return (
      <div className="mx-auto w-full max-w-5xl">
        <Panel>
          <EmptyState icon={Lock} title={tx('Hesabınıza bağlı çalışan kaydı bulunamadı')} detail={tx('Puanınızı görebilmeniz için hesabınızın bir çalışan kaydıyla eşleşmesi gerekiyor. İK yöneticinize başvurun.')} />
        </Panel>
      </div>
    )
  }

  const s = score.data
  const composition = ex
    ? [
        ...(ex.hasGoals ? [{ key: 'goals', label: tx('Hedefler'), value: ex.goalContribution, color: 'hsl(var(--foreground) / 0.75)' }] : []),
        ...ex.categories.filter((c) => c.counted && c.contribution).map((c) => ({ key: c.category, label: categoryLabels[c.category], value: c.contribution as number, color: categoryColor[c.category] })),
      ]
    : []

  const notes: string[] = []
  if (ex && s) {
    if (!ex.hasGoals && ex.hasMetrics) notes.push(tx('Bu dönemde hedef tanımlı değil; hedef payı (%{0}) kaybolmadı, metrik puanı %100 ağırlıkla kullanıldı.', [Math.round(ex.goalPct)]))
    if (ex.hasGoals && !ex.hasMetrics) notes.push(tx('Henüz metrik puanı yok; metrik payı (%{0}) hedeflere aktarıldı, hedef puanı %100 ağırlıkla kullanıldı.', [Math.round(ex.metricPct)]))
    const zero = ex.categories.filter((c) => c.weight === 0)
    if (zero.length) notes.push(tx('{0} kategorisinin ağırlığı bu sürümde 0; puanlandı ama hesaba girmedi.', [zero.map((c) => categoryLabels[c.category]).join(', ')]))
    notes.push(tx('Metrik puanları 0–100 aralığına çevrilir: 1–5 ölçekte 4 → 75, 1–10 ölçekte 8 → 77,78, yüzde olduğu gibi.'))
    if (cfg)
      notes.push(
        tx('Aynı metriğe gelen puanlar değerlendirici katsayılarıyla ortalandı: {0}.', [(['Manager', 'TeamLead', 'Peer', 'Upward', 'Self'] as const)
          .map((t) => `${reviewTypeLabels[t].toLocaleLowerCase(appLocale)} ${String(cfg[({ Manager: 'managerReviewWeight', TeamLead: 'teamLeadReviewWeight', Peer: 'peerReviewWeight', Upward: 'upwardReviewWeight', Self: 'selfReviewWeight' } as const)[t]]).replace('.', ',')}`)
          .join(', ')]),
      )
    notes.push(tx('Sayısal hedeflerde gerçekleşme %100’de kırpılır; aşım puana yansımaz.'))
    if (ex.inferred) notes.push(tx('Bu puanın hesaplandığı ayar sürümüne ulaşılamadı; hedef/metrik payları puanlardan çıkarıldı.'))
  }

  return (
    <div className="mx-auto w-full max-w-6xl">
      <PerfPageHeader
        eyebrow={tx('Performans')}
        title={manager ? tx('Puan dökümü') : tx('Puan dökümüm')}
        description={tx('Puanın nereden geldiği, katman katman: hedefler ve metrikler, kategoriler, tek tek metrikler — ve her birinin nihai puana katkısı.')}
      >
        <div className="flex flex-wrap items-end gap-3">
          {manager && <PersonSelect className="w-64" value={employeeId} onChange={(v) => setParam('calisan', v)} />}
          <CyclePicker className="w-64" cycles={cycles} value={cycleId} onChange={(v) => setParam('donem', v)} />
          {cycle && <Chip className="mb-2">{cycle.status === 'Closed' ? tx('Kapanmış dönem — puan sabit') : tx('Açık dönem — puan değişebilir')}</Chip>}
        </div>
      </PerfPageHeader>

      {score.isError && (
        <Panel>
          <ErrorState title={tx('Puan alınamadı')} message={errorText(score.error)} onRetry={() => void score.refetch()} />
        </Panel>
      )}

      {(score.isPending || (!employeeId && me.isPending)) && employeeId !== '' && (
        <div className="flex flex-col gap-5" aria-busy="true">
          <Skeleton className="h-52 w-full rounded-2xl" />
          <Skeleton className="h-96 w-full rounded-2xl" />
        </div>
      )}

      {s && ex && s.score === null && (
        <Panel>
          <EmptyState
            icon={Gauge}
            title={tx('Bu dönemde puan yok')}
            detail={tx('Henüz gönderilmiş değerlendirme yok. İlk değerlendirme gönderildiğinde puan burada katman katman görünür.')}
            action={
              <Button asChild variant="outline">
                <Link to="/panel/performans/degerlendirme">
                  <ClipboardList aria-hidden />
                  {tx('Değerlendirmeler')}
                </Link>
              </Button>
            }
          />
        </Panel>
      )}

      {s && ex && s.score !== null && (
        <div className="flex flex-col gap-5">
          {s.isProvisional && <ProvisionalBanner reason={s.provisionalReason} />}

          {/* ------------------------------- özet ------------------------------- */}
          <motion.section
            initial={{ opacity: 0, y: 12 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.55, ease: EASE }}
            className="grid gap-6 rounded-2xl border border-border bg-card p-5 sm:p-6 lg:grid-cols-[auto_minmax(0,1fr)]"
          >
            <div className="flex flex-col items-center gap-2">
              <ScoreRing score={s.score} thresholds={thresholds} provisional={s.isProvisional} />
              <BandBadge score={s.score} thresholds={thresholds} />
            </div>

            <div className="flex min-w-0 flex-col justify-center gap-5">
              <div>
                <p className="text-[18px] font-semibold">{people.nameOf(employeeId)}</p>
                <p className="text-[12px] text-muted-foreground">
                  {tx('{0} · Sürüm {1} ile hesaplandı · {2} değerlendirmeye dayanıyor', [cycle?.name, s.configVersion, s.reviewCount])}</p>
              </div>

              <div className="grid grid-cols-2 gap-3 sm:max-w-md">
                <div className={cn('rounded-xl border border-border p-3', !ex.hasGoals && 'border-dashed opacity-70')}>
                  <p className="text-[11px] text-muted-foreground">{tx('Hedef ayağı · %{0}', [Math.round(ex.effGoalPct)])}</p>
                  <p className="text-[22px] leading-tight font-semibold">{ex.hasGoals ? <AnimatedNumber value={s.goalScore ?? 0} format={(v) => formatScore(v)} /> : '—'}</p>
                </div>
                <div className={cn('rounded-xl border border-border p-3', !ex.hasMetrics && 'border-dashed opacity-70')}>
                  <p className="text-[11px] text-muted-foreground">{tx('Metrik ayağı · %{0}', [Math.round(ex.effMetricPct)])}</p>
                  <p className="text-[22px] leading-tight font-semibold">{ex.hasMetrics ? <AnimatedNumber value={s.metricScore ?? 0} format={(v) => formatScore(v)} /> : '—'}</p>
                </div>
              </div>

              <div>
                <p className="mb-1.5 text-[12px] font-medium text-muted-foreground">{tx('Puan nasıl oluştu? (100 üzerinden katkılar)')}</p>
                <div className="flex h-5 w-full overflow-hidden rounded-full bg-muted">
                  {composition.map((c, i) => (
                    <motion.div
                      key={c.key}
                      initial={{ width: 0 }}
                      animate={{ width: `${c.value}%` }}
                      transition={{ duration: 0.9, ease: EASE, delay: 0.2 + i * 0.08 }}
                      className="h-full border-r-2 border-card last:border-r-0"
                      style={{ background: c.color }}
                      title={`${c.label}: +${formatScore(c.value)}`}
                    />
                  ))}
                </div>
                <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
                  {composition.map((c) => (
                    <span key={c.key} className="inline-flex items-center gap-1.5">
                      <span className="size-2 rounded-full" style={{ background: c.color }} />
                      {c.label}
                      <span className="tabular font-semibold text-foreground">+{formatScore(c.value)}</span>
                    </span>
                  ))}
                  <span className="inline-flex items-center gap-1.5">
                    <span className="size-2 rounded-full bg-muted-foreground/30" />
                    {tx('Kalan')}
                    <span className="tabular font-semibold text-foreground">{formatScore(Math.max(0, 100 - (s.score ?? 0)))}</span>
                  </span>
                </div>
              </div>
            </div>
          </motion.section>

          {/* ------------------------------- ağaç ------------------------------- */}
          <motion.section initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.55, ease: EASE, delay: 0.1 }}>
            <Panel className="p-4 sm:p-5">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <h2 className="text-[15px] font-semibold">{tx('Katmanlı döküm')}</h2>
                <p className="text-[12px] text-muted-foreground">{tx('Satırlara tıklayıp açın. Katkıların toplamı nihai puanı verir.')}</p>
              </div>
              <ScoreTree score={s} ex={ex} thresholds={thresholds} />
            </Panel>
          </motion.section>

          <Panel className="p-5">
            <h2 className="mb-2 flex items-center gap-1.5 text-[13px] font-semibold">
              <Info className="size-4 text-primary" aria-hidden />
              {tx('Dipnotlar')}
            </h2>
            <ol className="flex list-decimal flex-col gap-1.5 pl-5 text-[12px] leading-relaxed text-muted-foreground">
              {notes.map((n) => (
                <li key={n}>{n}</li>
              ))}
            </ol>
          </Panel>

          {manager && (
            <div className="flex flex-wrap gap-2">
              <Button asChild variant="outline" size="sm">
                <Link to={`/panel/performans/oneriler?calisan=${employeeId}`}>
                  <Lightbulb aria-hidden />
                  {tx('Aksiyon önerisi')}
                </Link>
              </Button>
              <Button asChild variant="ghost" size="sm">
                <Link to={`/panel/performans/analiz?calisan=${employeeId}`}>{tx('Zaman içindeki seyri')}</Link>
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
