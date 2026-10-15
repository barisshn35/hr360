/**
 * Bir kategori ve metrikleri.
 *
 * Üstte kategori şeridi: metriklerin kategori içindeki payı tek bakışta.
 * Satırın üzerine gelince şeritte ilgili dilim öne çıkar (ve tersi).
 * Başlık, kategorinin puanlama ayarındaki ağırlığını da söyler; ağırlık 0
 * ise kategori puana girmez ve bu açıkça yazılır.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AnimatePresence, motion } from 'motion/react'
import { Archive, ArrowRight, Pencil, Plus, TriangleAlert } from 'lucide-react'
import {
  categoryColor,
  categoryHints,
  categoryLabels,
  formatShareOf,
  formatWeight,
  shareOf,
  type Metric,
  type MetricCategory,
} from '@/api/performance'
import { Panel } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { EASE } from '@/motion/primitives'
import { Chip } from '../components/controls'
import { ScaleBadge } from '../components/ScaleInput'
import { ShareBar, ShareMeter } from '../components/WeightShare'
import { basisFor, categoryBasis, type Scope } from './share'
import { tx } from '@/lib/i18n'

export function CategorySection({
  category,
  metrics,
  active,
  scope,
  categoryWeight,
  categoryShare,
  deptName,
  recentIds,
  onEdit,
  onArchive,
  onAdd,
  onScope,
  index,
  readOnly = false,
}: {
  category: MetricCategory
  /** Bu kategoride listelenecek metrikler (arşiv açıksa arşivdekiler dahil). */
  metrics: Metric[]
  /** Tüm etkin metrikler — pay hesabı için. */
  active: Metric[]
  scope: Scope
  /** Puanlama ayarındaki oransal kategori ağırlığı (bilinmiyorsa null). */
  categoryWeight: number | null
  /** Kategorinin metrik ayağındaki payı (0–100). */
  categoryShare: number | null
  deptName: (id: string | null) => string
  recentIds: Set<string>
  onEdit: (m: Metric) => void
  onArchive: (m: Metric) => void
  onAdd: (category: MetricCategory) => void
  onScope: (scope: Scope) => void
  index: number
  /** Yalnızca İK düzenler; yöneticide ekle/düzenle/arşivle gizli. */
  readOnly?: boolean
}) {
  const [hover, setHover] = useState<string | null>(null)
  const color = categoryColor[category]
  const basis = categoryBasis(category, active, scope)
  const excluded = categoryWeight === 0

  return (
    <motion.section
      initial={{ opacity: 0, y: 16 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.55, ease: EASE, delay: 0.06 * index }}
      aria-labelledby={`cat-${category}`}
    >
      <Panel className={cn(excluded && 'border-dashed')}>
        <div className="border-b border-border px-4 pt-4 pb-3.5">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="flex min-w-0 items-start gap-3">
              <span
                aria-hidden
                className="mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-lg text-[13px] font-bold"
                style={{ background: `color-mix(in oklab, ${color} 14%, transparent)`, color }}
              >
                {categoryLabels[category].charAt(0)}
              </span>
              <div className="min-w-0">
                <h2 id={`cat-${category}`} className="text-[15px] font-semibold">
                  {categoryLabels[category]}
                  <span className="ml-2 text-[12px] font-normal text-muted-foreground">
                    {tx('{0} metrik', [metrics.filter((m) => m.isActive).length])}</span>
                </h2>
                <p className="text-[12px] text-muted-foreground">{categoryHints[category]}</p>
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-1.5">
              {categoryWeight !== null &&
                (excluded ? (
                  <Link to="/panel/performans/ayarlar" className="group">
                    <Chip tone="warning">
                      <TriangleAlert className="size-3" aria-hidden />
                      {tx('Ağırlık 0 · puana girmiyor')}
                      <ArrowRight className="size-3 transition-transform group-hover:translate-x-0.5" aria-hidden />
                    </Chip>
                  </Link>
                ) : (
                  <Link to="/panel/performans/ayarlar" title={tx('Puanlama ayarında değiştir')}>
                    <Chip>
                      {tx('Ağırlık')}{' '}<span className="tabular font-semibold text-foreground">{formatWeight(categoryWeight)}</span>
                      {categoryShare !== null && (
                        <>
                          {' '}{tx('· metrik ayağının', [])}{' '}<span className="tabular font-semibold text-foreground">{formatShareOf(categoryShare)}</span>
                        </>
                      )}
                    </Chip>
                  </Link>
                ))}
              {!readOnly && (
                <Button size="xs" variant="ghost" onClick={() => onAdd(category)}>
                  <Plus aria-hidden />
                  {tx('Ekle')}
                </Button>
              )}
            </div>
          </div>

          {basis.metrics.length > 0 && (
            <div className="mt-3.5">
              <ShareBar
                items={basis.metrics.map((m) => ({ id: m.id, label: m.name, weight: m.weight }))}
                color={color}
                highlightId={hover}
                onHover={setHover}
                height={8}
              />
              <p className="mt-1.5 text-[11px] text-muted-foreground">{tx('Kategori içi pay dağılımı · {0}', [basis.departmentId ? tx('{0} çalışanları', [deptName(basis.departmentId)]) : tx('tüm departmanlar')])}
              </p>
            </div>
          )}

          {excluded && (
            <p className="mt-2.5 rounded-md border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-2.5 py-1.5 text-[12px] leading-relaxed text-foreground">
              {tx('Puanlama ayarında bu kategorinin ağırlığı')}{' '}<strong className="font-semibold">0</strong>{tx('. Buradaki metrikler değerlendirmede sorulur ve puan dökümünde görünür, ama nihai puana katkı vermez.')}
            </p>
          )}

          {basis.mixed && scope === 'all' && (
            <p className="mt-2.5 flex flex-wrap items-center gap-x-2 gap-y-1 rounded-md bg-muted/60 px-2.5 py-1.5 text-[12px] text-muted-foreground">
              {tx('Departmana özgü metrikler, o departmanda genel metriklerin payını düşürür.')}
              {[...new Set(metrics.map((m) => m.departmentId).filter((d): d is string => d !== null))].map((d) => (
                <button key={d} type="button" onClick={() => onScope(d)} className="font-medium text-primary hover:underline">
                  {tx('{0} için göster', [deptName(d)])}</button>
              ))}
            </p>
          )}
        </div>

        <ul className="divide-y divide-border" onMouseLeave={() => setHover(null)}>
          <AnimatePresence initial={false}>
            {metrics.map((m) => {
              const b = basisFor(m, active, scope)
              const total = b.reduce((a, x) => a + x.weight, 0)
              const share = m.isActive ? shareOf(m.weight, total) : null
              const isRecent = recentIds.has(m.id)
              return (
                <motion.li
                  key={m.id}
                  layout
                  initial={{ opacity: 0, height: 0 }}
                  animate={{ opacity: 1, height: 'auto' }}
                  exit={{ opacity: 0, height: 0 }}
                  transition={{ duration: 0.35, ease: EASE }}
                  onMouseEnter={() => m.isActive && setHover(m.id)}
                  className={cn('group relative overflow-hidden', !m.isActive && 'bg-muted/30')}
                >
                  {isRecent && (
                    <motion.span
                      aria-hidden
                      initial={{ opacity: 0.9 }}
                      animate={{ opacity: 0 }}
                      transition={{ duration: 2.2, ease: 'easeOut' }}
                      className="pointer-events-none absolute inset-0"
                      style={{ background: `color-mix(in oklab, ${color} 16%, transparent)` }}
                    />
                  )}
                  <span
                    aria-hidden
                    className="absolute inset-y-0 left-0 w-0.5 transition-opacity"
                    style={{ background: color, opacity: hover === m.id ? 1 : 0 }}
                  />
                  <div className="relative flex items-start gap-2 px-4 py-3 sm:items-center">
                    <div className="flex min-w-0 flex-1 flex-col gap-2 sm:flex-row sm:items-center sm:gap-4">
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <span className={cn('text-[14px] font-medium', !m.isActive && 'text-muted-foreground')}>{m.name}</span>
                          {m.isRequired && m.isActive && <Chip tone="danger">{tx('Zorunlu')}</Chip>}
                          {m.departmentId && <Chip tone="primary">{deptName(m.departmentId)}</Chip>}
                          {!m.isActive && (
                            <Chip>
                              <Archive className="size-3" aria-hidden />
                              {tx('Arşivde · geçmiş puanlar korunuyor')}
                            </Chip>
                          )}
                        </div>
                        <p className="mt-0.5 flex min-w-0 items-center gap-2 text-[12px] text-muted-foreground">
                          <code className="shrink-0 font-mono text-[11px]">{m.code}</code>
                          {m.description && <span className="truncate">· {m.description}</span>}
                        </p>
                      </div>

                      <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 sm:flex-nowrap sm:gap-5">
                        <span className="shrink-0">
                          <ScaleBadge scale={m.scale} range={m.range} />
                        </span>
                        <div className="flex shrink-0 items-baseline gap-1.5 sm:block sm:text-right">
                          <p className="text-[11px] text-muted-foreground">{tx('Ağırlık')}</p>
                          <p className="tabular text-[13px] font-semibold">{formatWeight(m.weight)}</p>
                        </div>
                        {share !== null ? (
                          <div className="flex shrink-0 flex-col items-start sm:w-[190px] sm:items-end">
                            <ShareMeter share={share} color={color} />
                            <p className="mt-0.5 hidden text-[11px] whitespace-nowrap text-muted-foreground sm:block">
                              {tx('{0}{1} kategorisinin {2}', [m.departmentId && scope === 'all' ? tx('{0} için · ', [deptName(m.departmentId)]) : '', categoryLabels[category], formatShareOf(share)])}
                            </p>
                          </div>
                        ) : (
                          <span className="hidden sm:block sm:w-[190px]" />
                        )}
                        {share !== null && (
                          <p className="order-last w-full text-[11px] text-muted-foreground sm:hidden">
                            {tx('{0}{1} kategorisinin {2}', [m.departmentId && scope === 'all' ? tx('{0} için · ', [deptName(m.departmentId)]) : '', categoryLabels[category], formatShareOf(share)])}
                          </p>
                        )}
                      </div>
                    </div>
                    <div className="flex shrink-0 items-center gap-0.5 sm:opacity-0 sm:transition-opacity sm:group-focus-within:opacity-100 sm:group-hover:opacity-100">
                      {m.isActive && !readOnly && (
                        <>
                          <Button size="icon-sm" variant="ghost" onClick={() => onEdit(m)} aria-label={tx('{0} metriğini düzenle', [m.name])} title={tx('Düzenle')}>
                            <Pencil aria-hidden />
                          </Button>
                          <Button size="icon-sm" variant="ghost" onClick={() => onArchive(m)} aria-label={tx('{0} metriğini arşivle', [m.name])} title={tx('Arşivle')}>
                            <Archive aria-hidden />
                          </Button>
                        </>
                      )}
                    </div>
                  </div>
                </motion.li>
              )
            })}
          </AnimatePresence>
        </ul>
      </Panel>
    </motion.section>
  )
}
