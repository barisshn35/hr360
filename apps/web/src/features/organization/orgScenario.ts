/**
 * Organizasyon şemasında senaryo karşılaştırması (adres: `senaryo=<id>`, `karsilastir=yan`) — saf hesap.
 *
 * Senaryolar (Org senaryoları ekranı, engagement-service) gerçek organizasyonu değiştirmez;
 * kişi taşıma / ayrılış / yeni pozisyon hareketlerinden oluşur. Burada hareketler bugünkü
 * çalışan listesine uygulanıp taslak model kurulur ve departman başına fark çıkarılır.
 *
 * KVKK: yeni pozisyonların planlanan maaşı hiçbir koşulda modele taşınmaz (yalnızca ad ve
 * departman). Fark kişi SAYISIdır; şemayı zaten kişi düzeyinde görebilen (yönetici ve üstü)
 * kullanıcıya açılır.
 */

import type { Employee } from '@/api/types'
import type { OrgMove } from '@/api/engagement'
import { tx } from '@/lib/i18n'
import { activeAssignment, isFormerEmployee, type ChartModel } from './orgChartModel'

export const SCENARIO_PARAM = 'senaryo'
export const COMPARE_PARAM = 'karsilastir'

export type CompareMode = 'overlay' | 'side'

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function parseScenarioParam(v: string | null): string | null {
  return v && UUID_RE.test(v) ? v : null
}

export const parseCompareParam = (v: string | null): CompareMode => (v === 'yan' ? 'side' : 'overlay')

/**
 * Hareketleri çalışan listesine uygular (taslak). Taşıma: aktif atamanın departmanı değişir
 * (hedef yoksa atanmamış); ayrılış: kişi düşer; yeni pozisyon: sahte kimlikli kişi eklenir.
 * Aynı kişi için birden çok hareket varsa sonuncusu geçerlidir (ekrandaki düzenleyiciyle aynı).
 */
export function applyScenario(employees: Employee[], moves: readonly OrgMove[], today: string): Employee[] {
  const last = new Map<string, OrgMove>()
  for (const m of moves) if (m.kind !== 'Hire' && m.employeeId) last.set(m.employeeId, m)
  const out: Employee[] = []
  for (const e of employees) {
    if (isFormerEmployee(e)) continue
    const m = last.get(e.id)
    if (!m) {
      out.push(e)
      continue
    }
    if (m.kind === 'Exit') continue
    const a = activeAssignment(e)
    const to = m.toDepartmentId ?? null
    out.push({
      ...e,
      assignments: to
        ? [{ id: `sc-${e.id}`, departmentId: to, positionTitle: m.newPosition ?? a?.positionTitle ?? null, effectiveFrom: today, effectiveTo: null }]
        : [],
    })
  }
  moves.forEach((m, i) => {
    if (m.kind !== 'Hire') return
    out.push({
      id: `hire-${i}`,
      firstName: m.name || tx('Yeni pozisyon'),
      lastName: '',
      email: '',
      hireDate: today,
      status: 0 as Employee['status'],
      assignments: m.toDepartmentId
        ? [{ id: `sc-hire-${i}`, departmentId: m.toDepartmentId, positionTitle: m.newPosition ?? null, effectiveFrom: today, effectiveTo: null }]
        : [],
    })
  })
  return out
}

export type DiffStatus = 'grew' | 'shrank' | 'changed' | 'same'

export interface DeptDiff {
  before: number
  after: number
  /** Departmana giren kişi (taşınan + yeni pozisyon). */
  incoming: number
  /** Departmandan çıkan kişi (taşınan + ayrılan). */
  outgoing: number
  status: DiffStatus
}

export interface ScenarioDiff {
  byDept: Map<string, DeptDiff>
  moved: number
  hires: number
  exits: number
  /** Kişi sayısı ya da bileşimi değişen departman sayısı. */
  affected: number
}

/** Bugünkü ve taslak model arasındaki fark (departmanın kendi üyeleri üzerinden). */
export function scenarioDiff(base: ChartModel, draft: ChartModel): ScenarioDiff {
  const incoming = new Map<string, number>()
  const outgoing = new Map<string, number>()
  const inc = (m: Map<string, number>, k: string) => m.set(k, (m.get(k) ?? 0) + 1)
  let moved = 0
  let hires = 0
  let exits = 0
  for (const [emp, d] of base.deptOf) {
    const after = draft.deptOf.get(emp)
    if (after === d) continue
    inc(outgoing, d)
    if (after) {
      inc(incoming, after)
      moved++
    } else if (!draft.unassigned.some((p) => p.id === emp)) exits++
    else moved++
  }
  for (const [emp, d] of draft.deptOf) {
    if (base.deptOf.has(emp)) continue
    inc(incoming, d)
    if (emp.startsWith('hire-')) hires++
    else moved++ // atanmamışken departmana alınan
  }
  // Atanmamışken ayrılanlar da ayrılış sayılır.
  for (const p of base.unassigned) if (!draft.deptOf.has(p.id) && !draft.unassigned.some((x) => x.id === p.id)) exits++

  const byDept = new Map<string, DeptDiff>()
  let affected = 0
  for (const [id, n] of base.byId) {
    const before = n.members.length
    const after = draft.byId.get(id)?.members.length ?? 0
    const i = incoming.get(id) ?? 0
    const o = outgoing.get(id) ?? 0
    const status: DiffStatus = after > before ? 'grew' : after < before ? 'shrank' : i + o > 0 ? 'changed' : 'same'
    if (status !== 'same') affected++
    byDept.set(id, { before, after, incoming: i, outgoing: o, status })
  }
  return { byDept, moved, hires, exits, affected }
}

const DIFF_COLOR: Record<DiffStatus, string> = {
  grew: 'hsl(152 55% 42%)',
  shrank: 'hsl(4 75% 52%)',
  changed: 'hsl(38 92% 50%)',
  same: 'hsl(var(--muted-foreground) / 0.35)',
}
export const diffColor = (s: DiffStatus) => DIFF_COLOR[s]

export const diffLabel = (s: DiffStatus): string =>
  ({
    grew: tx('Senaryoda büyüyen'),
    shrank: tx('Senaryoda küçülen'),
    changed: tx('Kişileri değişen (sayı aynı)'),
    same: tx('Değişmeyen'),
  })[s]

export const DIFF_STATUSES: DiffStatus[] = ['grew', 'shrank', 'changed', 'same']

/** Düğüm alt yazısı: "8 → 10 (+2)". */
export function diffSubtitle(d: DeptDiff | undefined): string {
  if (!d) return ''
  const delta = d.after - d.before
  const sign = delta > 0 ? '+' : delta < 0 ? '−' : '±'
  return tx('{0} → {1} kişi ({2}{3})', [d.before, d.after, sign, Math.abs(delta)])
}
