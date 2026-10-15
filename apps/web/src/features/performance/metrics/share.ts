/**
 * Metrik payı hesabı — "bu metrik Teknik kategorinin %31'i".
 *
 * Pay, metriğin kategorisindeki etkin metriklerin ağırlık toplamına göre
 * hesaplanır. Departmana özgü metrikler yalnızca o departmanın
 * çalışanlarına uygulandığı için "toplam" kime göre hesaplandığına bağlı:
 *
 *   kapsam = departman  → genel metrikler + o departmanın metrikleri
 *   kapsam = genel      → yalnızca genel metrikler
 *   kapsam = tümü       → her metrik kendi kitlesine göre (genel metrik
 *                         genellere göre, departman metriği genel + o
 *                         departmana göre)
 */

import type { Metric, MetricCategory } from '@/api/performance'

export type Scope = 'all' | 'global' | string

export function basisFor(metric: Pick<Metric, 'category' | 'departmentId'>, active: Metric[], scope: Scope): Metric[] {
  const inCat = active.filter((m) => m.category === metric.category)
  if (scope === 'global') return inCat.filter((m) => m.departmentId === null)
  if (scope !== 'all') return inCat.filter((m) => m.departmentId === null || m.departmentId === scope)
  return inCat.filter((m) => m.departmentId === null || (metric.departmentId !== null && m.departmentId === metric.departmentId))
}

/** Kategori şeridinde gösterilecek küme ve başlığı. */
export function categoryBasis(
  category: MetricCategory,
  active: Metric[],
  scope: Scope,
): { metrics: Metric[]; departmentId: string | null; mixed: boolean } {
  const inCat = active.filter((m) => m.category === category)
  const globals = inCat.filter((m) => m.departmentId === null)
  const deptIds = [...new Set(inCat.map((m) => m.departmentId).filter((d): d is string => d !== null))]

  if (scope === 'global') return { metrics: globals, departmentId: null, mixed: false }
  if (scope !== 'all') {
    return { metrics: inCat.filter((m) => m.departmentId === null || m.departmentId === scope), departmentId: scope, mixed: false }
  }
  if (globals.length) return { metrics: globals, departmentId: null, mixed: deptIds.length > 0 }
  const first = deptIds[0] ?? null
  return { metrics: inCat.filter((m) => m.departmentId === first), departmentId: first, mixed: deptIds.length > 1 }
}
