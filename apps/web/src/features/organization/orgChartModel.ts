/**
 * Organizasyon şemasının veri modeli: düz departman listesi + çalışanlar →
 * çizilebilir ağaç.
 *
 * Hiyerarşi departman seviyesindedir (`parentDepartmentId`); bireysel
 * "kime raporluyor" ilişkisi yoktur. Her çalışan, aktif atamasının
 * (`effectiveTo === null`) departmanına yerleşir.
 */

import type { Assignment, Department, Employee } from '@/api/types'
import { fullName } from '@/lib/format'
import { activeOf } from '@/lib/assignments'

export interface ChartPerson {
  id: string
  name: string
  title: string | null
  /** Aktif atamanın başlangıcı. */
  since: string | null
  /** Atama ileri tarihli (henüz başlamadı). */
  future: boolean
  /** İşe giriş tarihi (kıdem renklendirmesi için; yalnızca tam çalışan listesini görebilene gelir). */
  hireDate: string | null
}

export interface ChartDept {
  dept: Department
  depth: number
  /** Kök departmanın sırası — alt departmanlar ebeveynin rengini alır. */
  colorIndex: number
  children: ChartDept[]
  /** Departman başı en üstte, sonra ada göre. */
  members: ChartPerson[]
  /** Alt departmanlar dahil kişi sayısı. */
  total: number
}

export interface ChartModel {
  roots: ChartDept[]
  byId: Map<string, ChartDept>
  /** Aktif ataması olmayan (ayrılmamış) çalışanlar. */
  unassigned: ChartPerson[]
  /** Çalışan → bu şirketteki aktif departmanı. */
  deptOf: Map<string, string>
  /** Döngü yüzünden köke alınan departman sayısı (veri bozukluğu uyarısı için). */
  cycleCount: number
}

/** Backend durumu sayı (2) ya da metin ("Terminated") dönebilir. */
export function isFormerEmployee(e: Employee): boolean {
  const s = String(e.status)
  return s === '2' || s === 'Terminated'
}

/** Aktif atama: `effectiveTo` boş olan; birden fazlaysa en yeni başlayan. */
export const activeAssignment = (e: Employee): Assignment | null => activeOf(e.assignments ?? [])

const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name, 'tr-TR')

export function buildChart(departments: Department[], employees: Employee[], today: string): ChartModel {
  const ids = new Set(departments.map((d) => d.id))

  /* ------------------------------- kişileri dağıt ------------------------------- */
  const membersOf = new Map<string, ChartPerson[]>()
  const unassigned: ChartPerson[] = []
  const deptOf = new Map<string, string>()

  for (const e of employees) {
    if (isFormerEmployee(e)) continue
    const a = activeAssignment(e)
    const person: ChartPerson = {
      id: e.id,
      name: fullName(e),
      title: a?.positionTitle ?? null,
      since: a?.effectiveFrom ?? null,
      future: Boolean(a && a.effectiveFrom.slice(0, 10) > today),
      hireDate: e.hireDate ?? null,
    }
    if (!a) unassigned.push(person)
    else if (ids.has(a.departmentId)) {
      membersOf.set(a.departmentId, [...(membersOf.get(a.departmentId) ?? []), person])
      deptOf.set(e.id, a.departmentId)
    }
    // Başka şirketin departmanındaysa bu şemaya girmez.
  }

  /* ------------------------------- ağacı kur ------------------------------- */
  const kids = new Map<string, Department[]>()
  const roots: Department[] = []
  for (const d of departments) {
    const p = d.parentDepartmentId
    if (p && p !== d.id && ids.has(p)) kids.set(p, [...(kids.get(p) ?? []), d])
    else roots.push(d)
  }

  const visited = new Set<string>()
  const byId = new Map<string, ChartDept>()

  const build = (d: Department, depth: number, colorIndex: number): ChartDept => {
    visited.add(d.id)
    const children = (kids.get(d.id) ?? [])
      // Ziyaret edilmiş düğüme geri dönmek döngü demektir — o dal kesilir.
      .filter((c) => !visited.has(c.id))
      .sort(byName)
      .map((c) => build(c, depth + 1, colorIndex))
    const head = d.headEmployeeId ?? null
    const members = [...(membersOf.get(d.id) ?? [])].sort(
      (a, b) => Number(b.id === head) - Number(a.id === head) || byName(a, b),
    )
    const node: ChartDept = {
      dept: d,
      depth,
      colorIndex,
      children,
      members,
      total: members.length + children.reduce((n, c) => n + c.total, 0),
    }
    byId.set(d.id, node)
    return node
  }

  const tree = [...roots].sort(byName).map((r, i) => build(r, 0, i))

  // Yalnızca döngü içinde kalan (hiç köke bağlanmayan) departmanlar: kaybolmasınlar, köke alınır.
  let cycleCount = 0
  for (const d of departments) {
    if (visited.has(d.id)) continue
    cycleCount++
    tree.push(build(d, 0, tree.length))
  }

  return { roots: tree, byId, unassigned: unassigned.sort(byName), deptOf, cycleCount }
}

/** Kökten verilen departmana kadar olan kimlikler (arama sonucunu açmak için). */
export function ancestorsOf(model: ChartModel, deptId: string): string[] {
  const out: string[] = []
  const seen = new Set<string>()
  let cur = model.byId.get(deptId)?.dept
  while (cur && !seen.has(cur.id)) {
    seen.add(cur.id)
    out.unshift(cur.id)
    cur = cur.parentDepartmentId ? model.byId.get(cur.parentDepartmentId)?.dept : undefined
  }
  return out
}
