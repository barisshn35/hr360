/**
 * Metrik yönetimi — `/panel/performans/metrikler`.
 *
 * Şirket, çalışanlarını neye göre değerlendireceğini burada tanımlar.
 * Hiç metrik yoksa boş durum yerine şablon seçici çıkar. Metrikler
 * kategoriye göre gruplu; her birinin yanında kategori içindeki payı yazar.
 * "Sil" yok — arşivleme var ve geçmiş değerlendirmelerin korunduğu söylenir.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { Archive, LayoutTemplate, Plus, Search, Sparkles, X } from 'lucide-react'
import {
  CATEGORIES,
  categoryColor,
  categoryLabels,
  categoryWeightKey,
  shareOf,
  templateInfo,
  useApplyTemplate,
  useArchiveMetric,
  useCreateMetric,
  useMetrics,
  useScoringConfig,
  type Metric,
  type MetricCategory,
  type MetricInput,
  type MetricTemplate,
} from '@/api/performance'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/Modal'
import { Panel } from '@/components/ui/Panel'
import { SelectField } from '@/components/ui/Field'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorState, InfoNote } from '@/components/ui/States'
import { useAuth } from '@/auth/useAuth'
import { isHr } from '@/auth/roles'
import { useToast } from '@/components/ui/Toast'
import { CountUp, EASE } from '@/motion/primitives'
import { Switch, errorText } from '../components/controls'
import { PerfPageHeader, SetupTrail } from '../components/PerfPageHeader'
import { useDepartments } from '../hooks'
import { ArchiveDialog } from './ArchiveDialog'
import { CategorySection } from './CategorySection'
import { MetricDialog, type MetricDialogMode } from './MetricDialog'
import type { Scope } from './share'
import { TemplatePicker } from './TemplatePicker'
import { tx, appLocale } from '@/lib/i18n'

const normalize = (s: string) => s.toLocaleLowerCase(appLocale)

export function MetricsPage() {
  const toast = useToast()
  const metrics = useMetrics({ includeArchived: true })
  const scoring = useScoringConfig()
  const depts = useDepartments()
  const applyTemplate = useApplyTemplate()
  const archiveMetric = useArchiveMetric()
  const createMetric = useCreateMetric()
  // Metrikler şirket geneli ayardır: yalnızca İK düzenler, yönetici salt okunur görür.
  const { roles } = useAuth()
  const canEdit = isHr(roles, 'ext-performance-manage')

  const [scope, setScope] = useState<Scope>('all')
  const [showArchived, setShowArchived] = useState(false)
  const [query, setQuery] = useState('')
  const [dialog, setDialog] = useState<MetricDialogMode | null>(null)
  const [archiveTarget, setArchiveTarget] = useState<Metric | null>(null)
  const [templateOpen, setTemplateOpen] = useState(false)
  const [pendingTemplate, setPendingTemplate] = useState<MetricTemplate | null>(null)
  const [recentIds, setRecentIds] = useState<Set<string>>(new Set())
  const recentTimer = useRef<number | undefined>(undefined)

  const all = metrics.data ?? []
  const active = useMemo(() => all.filter((m) => m.isActive), [all])
  const archived = useMemo(() => all.filter((m) => !m.isActive), [all])

  const flashRecent = (ids: string[]) => {
    setRecentIds(new Set(ids))
    window.clearTimeout(recentTimer.current)
    recentTimer.current = window.setTimeout(() => setRecentIds(new Set()), 2600)
  }
  useEffect(() => () => window.clearTimeout(recentTimer.current), [])

  /* ---- kategori ağırlıkları (puanlama ayarından) ---- */
  const cfg = scoring.data
  const catWeight = (c: MetricCategory) => (cfg ? (cfg[categoryWeightKey[c]] as number) : null)
  const catTotal = cfg ? CATEGORIES.reduce((a, c) => a + Math.max(0, cfg[categoryWeightKey[c]] as number), 0) : 0

  /* ---- görünür liste ---- */
  const visible = useMemo(() => {
    const q = normalize(query.trim())
    return all
      .filter((m) => showArchived || m.isActive)
      .filter((m) => (scope === 'all' ? true : scope === 'global' ? m.departmentId === null : m.departmentId === null || m.departmentId === scope))
      .filter((m) => !q || normalize(`${m.name} ${m.code} ${m.description ?? ''}`).includes(q))
  }, [all, showArchived, scope, query])

  const byCategory = CATEGORIES.map((c) => ({
    category: c,
    items: visible
      .filter((m) => m.category === c)
      .sort((a, b) => Number(b.isActive) - Number(a.isActive) || a.sortOrder - b.sortOrder),
  }))
  const shownCategories = byCategory.filter((g) => g.items.length > 0)
  const emptyCategories = CATEGORIES.filter((c) => !active.some((m) => m.category === c))

  /* ---- eylemler ---- */
  const runTemplate = (t: MetricTemplate) => {
    setPendingTemplate(t)
    const before = new Set(active.map((m) => m.id))
    applyTemplate.mutate(t, {
      onSuccess: (created) => {
        const added = created.filter((m) => !before.has(m.id))
        toast.ok(
          added.length
            ? tx('{0} şablonundan {1} metrik eklendi. Dilediğiniz gibi düzenleyebilirsiniz.', [templateInfo[t].title, added.length])
            : tx('Şablondaki metriklerin hepsi zaten tanımlı; yeni metrik eklenmedi.'),
        )
        flashRecent(added.map((m) => m.id))
        setTemplateOpen(false)
      },
      onError: (e) => toast.stop(errorText(e)),
      onSettled: () => setPendingTemplate(null),
    })
  }

  const archiveAndRecreate = (metric: Metric, input: MetricInput) => {
    archiveMetric.mutate(metric.id, {
      onSuccess: () => {
        createMetric.mutate(input, {
          onSuccess: (created) => {
            toast.ok(tx('«{0}» arşivlendi; yeni ölçekle yeniden oluşturuldu.', [metric.name]))
            flashRecent([created.id])
            setDialog(null)
          },
          onError: (e) => {
            toast.stop(tx('Eski metrik arşivlendi ama yenisi oluşturulamadı: {0}', [errorText(e)]))
            setDialog({ kind: 'create', preset: input })
          },
        })
      },
      onError: (e) => toast.stop(errorText(e)),
    })
  }

  const loading = metrics.isPending
  const noMetrics = !loading && !metrics.isError && active.length === 0

  const stats = [
    { label: tx('Etkin metrik'), value: active.length },
    { label: tx('Zorunlu'), value: active.filter((m) => m.isRequired).length },
    { label: tx('Kategori'), value: new Set(active.map((m) => m.category)).size },
    { label: tx('Arşivde'), value: archived.length },
  ]

  return (
    <div className="mx-auto w-full max-w-6xl">
      <PerfPageHeader
        eyebrow={tx('Performans kurulumu · 1. adım')}
        title={tx('Metrikler')}
        description={
          <>
            {tx('Çalışanların neye göre değerlendirileceğini tanımlayın. Ağırlıklar')}{' '}<strong className="font-medium text-foreground">{tx('oransaldır')}</strong>{tx(', yüzde değil: her metriğin payı kategorisindeki diğer metriklere göre hesaplanır.')}
          </>
        }
        actions={
          canEdit && !noMetrics && !metrics.isError && (
            <>
              <Button variant="outline" onClick={() => setTemplateOpen(true)} disabled={loading}>
                <LayoutTemplate aria-hidden />
                {tx('Şablondan ekle')}
              </Button>
              <Button onClick={() => setDialog({ kind: 'create' })} disabled={loading}>
                <Plus aria-hidden />
                {tx('Yeni metrik')}
              </Button>
            </>
          )
        }
      >
        <div className="flex flex-col gap-4 xl:flex-row xl:items-center xl:justify-between">
          <SetupTrail current="metrics" />
          {!noMetrics && !metrics.isError && (
            <dl className="grid max-w-md grid-cols-4 gap-4 sm:gap-6">
              {stats.map((s, i) => (
                <div key={s.label} className="min-w-0">
                  <dt className="truncate text-[11px] text-muted-foreground">{s.label}</dt>
                  <dd className="text-[20px] leading-tight font-semibold">
                    {loading ? <Skeleton className="mt-1 h-5 w-8" /> : <CountUp to={s.value} duration={0.9} delay={0.1 * i} />}
                  </dd>
                </div>
              ))}
            </dl>
          )}
        </div>
      </PerfPageHeader>

      {!canEdit && (
        <div className="mb-4">
          <InfoNote>{tx('Metrikler şirket geneli ayardır; yalnızca İK düzenleyebilir. Bu sayfayı salt okunur görüyorsunuz.')}</InfoNote>
        </div>
      )}

      {/* ----------------------------------- hata ----------------------------------- */}
      {metrics.isError && (
        <Panel>
          <ErrorState title={tx('Metrikler alınamadı')} message={errorText(metrics.error)} onRetry={() => void metrics.refetch()} />
        </Panel>
      )}

      {/* --------------------------------- yükleniyor -------------------------------- */}
      {loading && (
        <div className="flex flex-col gap-4" aria-busy="true">
          <span className="sr-only">{tx('Metrikler yükleniyor')}</span>
          {[0, 1, 2].map((i) => (
            <Panel key={i} className="p-4">
              <div className="flex items-center gap-3">
                <Skeleton className="size-8 rounded-lg" />
                <div className="flex-1">
                  <Skeleton className="h-4 w-32" />
                  <Skeleton className="mt-1.5 h-3 w-56" />
                </div>
              </div>
              <Skeleton className="mt-4 h-2 w-full rounded-full" />
              {[0, 1, 2].map((r) => (
                <div key={r} className="mt-4 flex items-center gap-4">
                  <Skeleton className="h-4 flex-1" />
                  <Skeleton className="h-4 w-14" />
                  <Skeleton className="h-4 w-20" />
                </div>
              ))}
            </Panel>
          ))}
        </div>
      )}

      {/* ------------------------ hiç metrik yok: şablon seç ------------------------ */}
      {noMetrics && (
        <motion.section
          initial={{ opacity: 0, y: 12 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.6, ease: EASE }}
          aria-labelledby="template-title"
        >
          <div className="mb-5 text-center">
            <span className="mx-auto mb-3 flex size-11 items-center justify-center rounded-full bg-primary/10 text-primary">
              <Sparkles className="size-5" aria-hidden />
            </span>
            <h2 id="template-title" className="text-[18px] font-semibold">
              {tx('Henüz metrik tanımlanmamış — bir şablonla başlayın')}
            </h2>
            <p className="mx-auto mt-1 max-w-xl text-[13px] leading-relaxed text-muted-foreground">
              {tx('Şablon, rolünüze uygun beş metriği kategori ve ağırlıklarıyla birlikte ekler. Beş dakikada değerlendirmeye hazır olursunuz.')}
            </p>
            {archived.length > 0 && (
              <p className="mt-2 text-[12px] text-muted-foreground">
                {tx('{0} arşivlenmiş metrik var; geçmiş değerlendirmelerde puanları korunuyor.', [archived.length])}</p>
            )}
          </div>
          {canEdit && <TemplatePicker onApply={runTemplate} pending={pendingTemplate} onStartBlank={() => setDialog({ kind: 'create' })} />}
        </motion.section>
      )}

      {/* ---------------------------------- liste ---------------------------------- */}
      {!loading && !metrics.isError && active.length > 0 && (
        <>
          <div className="mb-4 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
            <div className="flex flex-1 flex-col gap-3 sm:flex-row sm:items-end">
              <label className="relative block w-full sm:max-w-xs">
                <span className="sr-only">{tx('Metrik ara')}</span>
                <Search className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder={tx('Ad, kod ya da açıklama ara')}
                  className="h-9 w-full rounded-md border border-input bg-background pr-8 pl-9 text-[13px] outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
                />
                {query && (
                  <button
                    type="button"
                    onClick={() => setQuery('')}
                    className="absolute top-1/2 right-2 -translate-y-1/2 rounded p-0.5 text-muted-foreground hover:bg-muted"
                    aria-label={tx('Aramayı temizle')}
                  >
                    <X className="size-3.5" />
                  </button>
                )}
              </label>
              <div className="w-full sm:w-56">
                <SelectField
                  label={tx('Kapsam')}
                  value={scope}
                  onChange={(v) => setScope(v)}
                  options={[
                    { value: 'all', label: tx('Tüm metrikler') },
                    { value: 'global', label: tx('Yalnızca genel metrikler') },
                    ...depts.list.map((d) => ({ value: d.id, label: tx('{0} çalışanları', [d.path]) })),
                  ]}
                />
              </div>
            </div>
            <Switch
              className="md:w-64"
              checked={showArchived}
              onChange={setShowArchived}
              label={
                <span className="inline-flex items-center gap-1.5">
                  <Archive className="size-3.5 text-muted-foreground" aria-hidden />{tx('Arşivi göster{0}', [archived.length ? ` (${archived.length})` : ''])}
                </span>
              }
              hint={tx('Arşivdeki metrikler yeni değerlendirmede sorulmaz.')}
            />
          </div>

          <AnimatePresence>
            {scope !== 'all' && (
              <motion.p
                initial={{ opacity: 0, height: 0 }}
                animate={{ opacity: 1, height: 'auto' }}
                exit={{ opacity: 0, height: 0 }}
                className="mb-4 overflow-hidden text-[13px] text-muted-foreground"
              >
                {scope === 'global'
                  ? tx('Tüm departmanlara uygulanan metrikler gösteriliyor. Paylar yalnızca bu metriklere göre hesaplandı.')
                  : tx('{0} çalışanları bu metriklerle değerlendirilir. Paylar bu kümeye göre hesaplandı.', [depts.nameOf(scope)])}
              </motion.p>
            )}
          </AnimatePresence>

          {shownCategories.length === 0 ? (
            <Panel>
              <div className="px-4 py-12 text-center">
                <p className="text-[15px] font-semibold">{tx('Aramanızla eşleşen metrik yok')}</p>
                <p className="mt-1 text-[13px] text-muted-foreground">{tx('Farklı bir kelime deneyin ya da kapsamı genişletin.')}</p>
                <Button
                  variant="outline"
                  size="sm"
                  className="mt-4"
                  onClick={() => {
                    setQuery('')
                    setScope('all')
                  }}
                >
                  {tx('Filtreleri temizle')}
                </Button>
              </div>
            </Panel>
          ) : (
            <div className="flex flex-col gap-4">
              {shownCategories.map((g, i) => {
                const w = catWeight(g.category)
                return (
                  <CategorySection
                    key={g.category}
                    index={i}
                    category={g.category}
                    metrics={g.items}
                    active={active}
                    scope={scope}
                    categoryWeight={w}
                    categoryShare={w !== null && w > 0 ? shareOf(w, catTotal) : null}
                    deptName={depts.nameOf}
                    recentIds={recentIds}
                    onEdit={(m) => setDialog({ kind: 'edit', metric: m })}
                    onArchive={setArchiveTarget}
                    onAdd={(category) => setDialog({ kind: 'create', preset: { category, departmentId: scope !== 'all' && scope !== 'global' ? scope : null } })}
                    onScope={setScope}
                    readOnly={!canEdit}
                  />
                )
              })}
            </div>
          )}

          {canEdit && emptyCategories.length > 0 && !query && (
            <motion.div
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              transition={{ delay: 0.35 }}
              className="mt-4 flex flex-wrap items-center gap-2 rounded-xl border border-dashed border-border px-4 py-3"
            >
              <span className="text-[13px] text-muted-foreground">{tx('Metriği olmayan kategoriler:')}</span>
              {emptyCategories.map((c) => {
                const w = catWeight(c)
                return (
                  <button
                    key={c}
                    type="button"
                    onClick={() => setDialog({ kind: 'create', preset: { category: c } })}
                    className="inline-flex items-center gap-1.5 rounded-full border border-border bg-background px-2.5 py-1 text-[12px] font-medium transition-colors hover:border-primary/40"
                    title={w && w > 0 ? tx('Puanlama ayarında ağırlığı var ama metriği yok — bu kategori puana katkı vermiyor.') : undefined}
                  >
                    <span className="size-2 rounded-full" style={{ background: categoryColor[c] }} />
                    {categoryLabels[c]}
                    {w !== null && w > 0 && <span className="text-[hsl(var(--warning))]">{tx('· ağırlığı var, metriği yok')}</span>}
                    <Plus className="size-3 text-muted-foreground" aria-hidden />
                  </button>
                )
              })}
            </motion.div>
          )}
        </>
      )}

      {/* --------------------------------- diyaloglar -------------------------------- */}
      {dialog && canEdit && (
        <MetricDialog
          key={dialog.kind === 'edit' ? dialog.metric.id : `new-${JSON.stringify(dialog.preset ?? {})}`}
          mode={dialog}
          onClose={() => setDialog(null)}
          active={active}
          departments={depts.list}
          scope={scope}
          onArchiveAndRecreate={archiveAndRecreate}
        />
      )}

      {archiveTarget && <ArchiveDialog metric={archiveTarget} onClose={() => setArchiveTarget(null)} />}

      <Modal
        open={templateOpen}
        onClose={() => setTemplateOpen(false)}
        size="xl"
        title={tx('Şablondan metrik ekle')}
        note={tx('Şablondaki metriklerden sizde henüz olmayanlar eklenir; mevcut metrikleriniz değişmez.')}
      >
        <TemplatePicker onApply={runTemplate} pending={pendingTemplate} compact />
      </Modal>
    </div>
  )
}
