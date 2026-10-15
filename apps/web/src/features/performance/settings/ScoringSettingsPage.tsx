/**
 * Puanlama ayarı — `/panel/performans/ayarlar`.
 *
 * Dört bölüm: (A) hedef/metrik payı, (B) kategori ağırlıkları,
 * (C) değerlendirici katsayıları ve geçerlilik, (D) aksiyon eşikleri.
 *
 * Ayar SÜRÜMLENİR: kaydetmek üzerine yazmaz, yeni sürüm oluşturur. PUT
 * kısmi güncelleme kabul etmez; yürürlükteki ayar GET ile alınır, form
 * onun kopyası üzerinde çalışır ve kaydederken TÜM alanlar gönderilir.
 */

import { useEffect, useMemo, useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { GitCommitVertical, RotateCcw, TriangleAlert } from 'lucide-react'
import {
  CATEGORIES,
  REVIEW_TYPES,
  categoryWeightKey,
  reviewWeightKey,
  thresholdsOf,
  useCycleResult,
  useMetrics,
  useScoringConfig,
  useScoringHistory,
  useUpdateScoringConfig,
  type ScoringConfig,
  type ScoringConfigInput,
} from '@/api/performance'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/Panel'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorState, InfoNote } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useToast } from '@/components/ui/Toast'
import { formatDateTime } from '@/lib/format'
import { EASE } from '@/motion/primitives'
import { Chip, errorText } from '../components/controls'
import { PerfPageHeader, SetupTrail } from '../components/PerfPageHeader'
import { SplitSlider } from '../components/SplitSlider'
import { thresholdIssues } from '../components/ThresholdTrack'
import { useCurrentCycle, usePeople } from '../hooks'
import { CategoryWeights } from './CategoryWeights'
import { CompositionDonut, GOAL_COLOR, METRIC_COLOR } from './CompositionDonut'
import { diff, toInput, type FieldKey } from './fields'
import { RaterWeights } from './RaterWeights'
import { SettingsSection } from './SettingsSection'
import { ThresholdSection } from './ThresholdSection'
import { VersionConfirmDialog } from './VersionConfirmDialog'
import { VersionHistory } from './VersionHistory'
import { tx } from '@/lib/i18n'

const CATEGORY_KEYS = CATEGORIES.map((c) => categoryWeightKey[c])
const RATER_KEYS = [...REVIEW_TYPES.map((t) => reviewWeightKey[t]), 'minReviewsForValidScore', 'allowSelfOnlyScore'] as FieldKey[]
const THRESHOLD_KEYS: FieldKey[] = ['criticalThreshold', 'improvementThreshold', 'recognitionThreshold', 'promotionThreshold', 'promotionConsecutivePeriods']

export function ScoringSettingsPage() {
  const toast = useToast()
  const config = useScoringConfig()
  const history = useScoringHistory()
  const metrics = useMetrics()
  const update = useUpdateScoringConfig()
  const people = usePeople()
  const { current: cycle } = useCurrentCycle()
  const result = useCycleResult(cycle?.id)
  // Şirket geneli puanlama ayarı yalnızca İK; yönetici salt okunur görür (sunucu da 403 döner).
  const { roles } = useAuth()
  const canEdit = isHr(roles, 'ext-performance-manage')

  const [draft, setDraft] = useState<ScoringConfigInput | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const saved = useMemo(() => (config.data ? toInput(config.data) : null), [config.data])

  // Yeni sürüm geldiğinde (ilk yükleme ya da kayıt sonrası) form sıfırdan kurulur.
  useEffect(() => {
    if (saved) setDraft(saved)
  }, [saved])

  const changes = useMemo(() => (saved && draft ? diff(saved, draft) : []), [saved, draft])
  const dirty = changes.length > 0

  // Sekme kapanırken kaydedilmemiş değişiklik uyarısı.
  useEffect(() => {
    if (!dirty) return
    const onUnload = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', onUnload)
    return () => window.removeEventListener('beforeunload', onUnload)
  }, [dirty])

  const set = <K extends keyof ScoringConfigInput>(key: K, value: ScoringConfigInput[K]) => {
    if (!canEdit) return
    setSaveError(null)
    setDraft((d) => (d ? { ...d, [key]: value } : d))
  }
  const resetKeys = (keys: FieldKey[]) =>
    setDraft((d) => (d && saved ? ({ ...d, ...Object.fromEntries(keys.map((k) => [k, saved[k]])) } as ScoringConfigInput) : d))
  const changedIn = (keys: FieldKey[]) => changes.some((c) => keys.includes(c.key) || (c.key === 'goalWeightPercent' && keys.includes('goalWeightPercent')))

  /* ---- doğrulama (backend de kontrol ediyor; burada anında göstermek için) ---- */
  const problems = useMemo(() => {
    if (!draft) return []
    const out: string[] = []
    if (draft.goalWeightPercent + draft.metricWeightPercent !== 100) out.push(tx('Hedef ve metrik payları toplamı 100 olmalı.'))
    if (CATEGORY_KEYS.every((k) => (draft[k] as number) <= 0)) out.push(tx('En az bir kategorinin ağırlığı sıfırdan büyük olmalı.'))
    for (const i of thresholdIssues(thresholdsOf(draft))) out.push(i.message)
    return out
  }, [draft])

  const dots = useMemo(
    () =>
      (result.data?.members ?? []).map((m) => ({
        id: m.employeeId,
        label: m.name ?? people.nameOf(m.employeeId),
        score: m.score,
      })),
    [result.data, people],
  )

  const save = () => {
    if (!draft) return
    setSaveError(null)
    update.mutate(draft, {
      onSuccess: (next) => {
        setConfirmOpen(false)
        toast.ok(tx('Sürüm {0} yürürlüğe girdi. Geçmiş dönemlerin puanları değişmedi.', [next.version]))
      },
      onError: (e) => setSaveError(errorText(e)),
    })
  }

  const loadVersion = (v: ScoringConfig) => {
    setDraft(toInput(v))
    toast.info(tx('Sürüm {0} değerleri forma yüklendi. Kaydederseniz yeni bir sürüm olarak yürürlüğe girer.', [v.version]))
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }

  const activeCycle = cycle?.status === 'Open' ? cycle : null

  return (
    <div className="mx-auto w-full max-w-7xl">
      <PerfPageHeader
        eyebrow={tx('Performans kurulumu · 2. adım')}
        title={tx('Puanlama ayarı')}
        description={tx('Nihai puanın nasıl hesaplanacağını belirleyin. Her kayıt yeni bir sürüm oluşturur; kapanmış dönemlerin puanları asla değişmez.')}
      >
        <div className="flex flex-col gap-3 xl:flex-row xl:items-center xl:justify-between">
          <SetupTrail current="scoring" />
          {config.data && (
            <p className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
              <Chip tone="success">
                <GitCommitVertical className="size-3" aria-hidden />{tx('Yürürlükte: Sürüm {0}', [config.data.version])}
              </Chip>
              {config.data.createdAt && <span>{formatDateTime(config.data.createdAt)}</span>}
              {config.data.createdByName && <span>· {config.data.createdByName}</span>}
            </p>
          )}
        </div>
      </PerfPageHeader>

      {!canEdit && (
        <div className="mb-4">
          <InfoNote>{tx('Puanlama ayarı şirket geneli ayardır; yalnızca İK değiştirebilir. Bu sayfayı salt okunur görüyorsunuz.')}</InfoNote>
        </div>
      )}

      {config.isError && (
        <Panel>
          <ErrorState title={tx('Puanlama ayarı alınamadı')} message={errorText(config.error)} onRetry={() => void config.refetch()} />
        </Panel>
      )}

      {config.isPending && (
        <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_320px]" aria-busy="true">
          <span className="sr-only">{tx('Puanlama ayarı yükleniyor')}</span>
          <div className="flex flex-col gap-5">
            {[0, 1, 2].map((i) => (
              <Panel key={i} className="p-5">
                <Skeleton className="h-5 w-48" />
                <Skeleton className="mt-2 h-3 w-80 max-w-full" />
                <Skeleton className="mt-6 h-4 w-full rounded-full" />
                <Skeleton className="mt-4 h-10 w-full" />
              </Panel>
            ))}
          </div>
          <Panel className="h-80 p-5">
            <Skeleton className="mx-auto size-48 rounded-full" />
          </Panel>
        </div>
      )}

      {draft && saved && (
        <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_320px]">
          <fieldset disabled={!canEdit} className="m-0 flex min-w-0 flex-col gap-5 border-0 p-0">
            <SettingsSection
              id="pay"
              index={0}
              letter="A"
              title={tx('Hedef / metrik dağılımı')}
              description={tx('Nihai puanın ne kadarı hedef gerçekleşmesinden, ne kadarı metrik değerlendirmelerinden gelsin? Toplam her zaman 100.')}
              changed={changedIn(['goalWeightPercent'])}
              onReset={() => resetKeys(['goalWeightPercent', 'metricWeightPercent'])}
            >
              <SplitSlider
                value={draft.goalWeightPercent}
                onChange={(v) => canEdit && setDraft((d) => (d ? { ...d, goalWeightPercent: v, metricWeightPercent: 100 - v } : d))}
                left="Hedefler"
                right="Metrikler"
                leftColor={GOAL_COLOR}
                rightColor={METRIC_COLOR}
                ariaLabel={tx('Hedef payı')}
              />
            </SettingsSection>

            <SettingsSection
              id="kategori"
              index={1}
              letter="B"
              title={tx('Kategori ağırlıkları')}
              description={
                <>{tx('Metrik ayağının kategorilere dağılımı. Ağırlıklar oransaldır: 2,5 ağırlıklı kategori 1 ağırlıklıdan 2,5 kat fazla etkiler.', [])}{' '}
                  <strong className="font-medium text-foreground">{tx('Sıfır verilen kategori hesaba hiç girmez.')}</strong>
                </>
              }
              changed={changedIn(CATEGORY_KEYS)}
              onReset={() => resetKeys(CATEGORY_KEYS)}
            >
              <CategoryWeights draft={draft} onChange={(k, v) => set(k, v)} metrics={metrics.data} />
            </SettingsSection>

            <SettingsSection
              id="degerlendirici"
              index={2}
              letter="C"
              title={tx('Değerlendirici katsayıları')}
              description={tx('Aynı metriğe farklı kişilerden gelen puanlar bu katsayılarla ağırlıklı ortalanır. Katsayı 0 olan değerlendirme türü puana katılmaz.')}
              changed={changedIn(RATER_KEYS)}
              onReset={() => resetKeys(RATER_KEYS)}
            >
              <RaterWeights draft={draft} onChange={set} />
            </SettingsSection>

            <SettingsSection
              id="esik"
              index={3}
              letter="D"
              title={tx('Aksiyon eşikleri')}
              description={tx('Aksiyon önerileri bu eşiklere göre üretilir. Sıra her zaman kritik < gelişim < takdir < terfi olmalı; işaretçileri sürükleyin ya da değer girin.')}
              changed={changedIn(THRESHOLD_KEYS)}
              onReset={() => resetKeys(THRESHOLD_KEYS)}
            >
              <ThresholdSection draft={draft} saved={saved} onChange={set} dots={dots} cycleName={cycle?.name ?? null} />
            </SettingsSection>

            {/* --------------------------- kaydetme çubuğu --------------------------- */}
            <AnimatePresence>
              {dirty && canEdit && (
                <motion.div
                  initial={{ opacity: 0, y: 24 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={{ opacity: 0, y: 24 }}
                  transition={{ duration: 0.35, ease: EASE }}
                  className="sticky bottom-4 z-30"
                >
                  <div className="flex flex-col gap-3 rounded-xl border border-primary/30 bg-card/95 p-3 shadow-xl backdrop-blur sm:flex-row sm:items-center sm:justify-between sm:px-4">
                    <div className="min-w-0">
                      <p className="text-[13px] font-semibold">{tx('Kaydedilmemiş {0} değişiklik', [changes.length])}<span className="ml-1 font-normal text-muted-foreground">{tx('· kaydedince Sürüm {0} oluşur', [saved && config.data ? config.data.version + 1 : ''])}</span>
                      </p>
                      {problems.length > 0 ? (
                        <p className="mt-0.5 flex items-center gap-1.5 text-[12px] text-destructive">
                          <TriangleAlert className="size-3.5 shrink-0" aria-hidden />
                          {problems[0]}
                          {problems.length > 1 && ` (+${problems.length - 1})`}
                        </p>
                      ) : (
                        <p className="mt-0.5 text-[12px] text-muted-foreground">{tx('Geçmiş dönemlerin puanları değişmez.')}</p>
                      )}
                    </div>
                    <div className="flex shrink-0 gap-2">
                      <Button variant="outline" onClick={() => setDraft(saved)} disabled={update.isPending}>
                        <RotateCcw aria-hidden />
                        {tx('Tümünü geri al')}
                      </Button>
                      <Button
                        onClick={() => {
                          setSaveError(null)
                          setConfirmOpen(true)
                        }}
                        disabled={problems.length > 0}
                      >
                        <GitCommitVertical aria-hidden />
                        {tx('Yeni sürüm olarak kaydet')}
                      </Button>
                    </div>
                  </div>
                </motion.div>
              )}
            </AnimatePresence>
          </fieldset>

          {/* ------------------------------- yan sütun ------------------------------- */}
          <aside className="flex flex-col gap-5 xl:sticky xl:top-20">
            <motion.div initial={{ opacity: 0, y: 18 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: EASE, delay: 0.1 }}>
              <Panel className="p-5">
                <h2 className="text-[14px] font-semibold">{tx('Nihai puan nasıl oluşur?')}</h2>
                <p className="mt-0.5 mb-4 text-[12px] text-muted-foreground">{tx('Formdaki değerlerle canlı hesaplanır.')}</p>
                <CompositionDonut draft={draft} />
              </Panel>
            </motion.div>
            <motion.div initial={{ opacity: 0, y: 18 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: EASE, delay: 0.2 }}>
              <Panel className="p-5">
                <VersionHistory
                  versions={history.data}
                  isPending={history.isPending}
                  error={history.error}
                  currentVersion={config.data?.version ?? null}
                  onLoad={canEdit ? loadVersion : undefined}
                />
              </Panel>
            </motion.div>
          </aside>
        </div>
      )}

      {draft && config.data && (
        <VersionConfirmDialog
          open={confirmOpen}
          onClose={() => setConfirmOpen(false)}
          onConfirm={save}
          changes={changes}
          nextVersion={config.data.version + 1}
          activeCycleName={activeCycle?.name ?? null}
          pending={update.isPending}
          error={saveError}
        />
      )}
    </div>
  )
}
