/**
 * Performans ekranlarının ortak veri kancaları.
 */

import { useMemo } from 'react'
import { useDirectory } from '@/api/directory'
import { useCompanies, useEmployees } from '@/api/queries'
import { useCycles } from '@/api/performance'
import type { Assignment, Department, Employee } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { fullName } from '@/lib/format'
import { localISODate } from '@/lib/dates'
import { tx, appLocale } from '@/lib/i18n'

/* --------------------------------- Departmanlar --------------------------------- */

export interface DeptNode extends Department {
  companyName: string
  depth: number
  /** "Yazılım › Mobil Geliştirme" */
  path: string
  children: DeptNode[]
}

/** İç içe departmanlarda en fazla bu kadar derinlik çizilir; aşan veri düz listeye düşer. */
export const MAX_DEPT_DEPTH = 6

/**
 * `parentDepartmentId`'den ağaç kurar. Bozuk veriye karşı korumalı:
 * döngü ya da aşırı derinlik görülürse o dal köke (düz listeye) alınır,
 * hiçbir departman kaybolmaz ve sonsuz döngüye girilmez.
 */
export function buildDeptTree(departments: Department[], companyName: string): DeptNode[] {
  const byId = new Map(departments.map((d) => [d.id, d]))
  const kids = new Map<string, Department[]>()
  const roots: Department[] = []
  for (const d of departments) {
    if (d.parentDepartmentId && byId.has(d.parentDepartmentId) && d.parentDepartmentId !== d.id) {
      kids.set(d.parentDepartmentId, [...(kids.get(d.parentDepartmentId) ?? []), d])
    } else roots.push(d)
  }

  const placed = new Set<string>()
  const build = (d: Department, depth: number, trail: string[]): DeptNode => {
    placed.add(d.id)
    const path = [...trail, d.name]
    const children =
      depth + 1 >= MAX_DEPT_DEPTH
        ? []
        : (kids.get(d.id) ?? [])
            .filter((c) => !placed.has(c.id))
            .sort((a, b) => a.name.localeCompare(b.name, 'tr-TR'))
            .map((c) => build(c, depth + 1, path))
    return { ...d, companyName, depth, path: path.join(' › '), children }
  }

  const tree = roots.sort((a, b) => a.name.localeCompare(b.name, 'tr-TR')).map((r) => build(r, 0, []))
  // Döngüdeki ya da derinlik sınırını aşan departmanlar: köke düz eklenir.
  for (const d of departments) if (!placed.has(d.id)) tree.push(build(d, 0, []))
  return tree
}

export function flattenDepts(nodes: DeptNode[]): DeptNode[] {
  return nodes.flatMap((n) => [n, ...flattenDepts(n.children)])
}

/** Tüm şirketlerin departmanları — düz liste (yol adıyla) ve ağaç. */
export function useDepartments() {
  const companies = useCompanies()
  return useMemo(() => {
    const trees = (companies.data ?? []).map((c) => ({ company: c, roots: buildDeptTree(c.departments ?? [], c.name) }))
    const list = trees.flatMap((t) => flattenDepts(t.roots))
    const byId = new Map(list.map((d) => [d.id, d]))
    return {
      list,
      trees,
      companies: companies.data ?? [],
      nameOf: (id: string | null | undefined) => (id ? (byId.get(id)?.name ?? tx('Bilinmeyen departman')) : tx('Tüm departmanlar')),
      pathOf: (id: string | null | undefined) => (id ? (byId.get(id)?.path ?? tx('Bilinmeyen departman')) : tx('Tüm departmanlar')),
      isPending: companies.isPending,
      error: companies.error,
    }
  }, [companies.data, companies.isPending, companies.error])
}

/* ----------------------------------- Kişiler ----------------------------------- */

/** Güncel görevlendirme: bitmemiş olanlar içinde `effectiveFrom`'u en büyük olan. */
export function currentAssignment(e: Employee | undefined): Assignment | null {
  const list = e?.assignments ?? []
  if (!list.length) return null
  const today = localISODate()
  const open = list.filter((a) => !a.effectiveTo || a.effectiveTo >= today)
  return [...(open.length ? open : list)].sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom))[0]
}

export interface PickerPerson {
  id: string
  name: string
}

/**
 * Kimlikten kişi çözümü.
 *
 * Ad her rol için dizinden (`/employees/directory`) gelir. Unvan ve
 * departman yalnızca tam çalışan listesine erişimi olanlarda (yönetici ve
 * üstü) bilinir; çalışan rolünde bu uç 403 döndüğü için hiç çağrılmaz.
 */
export function usePeople() {
  const { can, user } = useAuth()
  const full = can('employee:viewAll')
  const email = user?.email ?? null
  const directory = useDirectory()
  const employees = useEmployees({ enabled: full, staleTime: 5 * 60_000 })
  // Tam liste erişimi yoksa (çalışan rolü), en azından kendi kaydını email ile
  // çekelim — aksi halde kendi unvanını bile göremez (Ekipler ekranında "Üye"
  // görünür). Diğer çalışanların unvanı hâlâ bilinmez; bu beklenen.
  const self = useEmployees({ enabled: !full && Boolean(email), email: email ?? undefined, staleTime: 5 * 60_000 })

  return useMemo(() => {
    const names = new Map((directory.data ?? []).map((d) => [d.id, d.fullName || `${d.firstName} ${d.lastName}`.trim()]))
    const byId = new Map<string, Employee>([...(employees.data ?? []), ...(self.data ?? [])].map((e) => [e.id, e]))
    const nameOf = (id: string | null | undefined, fallback?: string | null) => {
      if (!id) return '—'
      const n = names.get(id) ?? (byId.has(id) ? fullName(byId.get(id)!) : null)
      return n ?? fallback ?? tx('Bilinmeyen çalışan')
    }
    const isFormer = (id: string) => byId.get(id)?.status === 2
    const source = directory.data?.length ? directory.data.map((d) => d.id) : (employees.data ?? []).map((e) => e.id)
    const list: PickerPerson[] = source
      .filter((id) => !isFormer(id))
      .map((id) => ({ id, name: nameOf(id) }))
      .sort((a, b) => a.name.localeCompare(b.name, 'tr-TR'))

    return {
      list,
      nameOf,
      titleOf: (id: string) => currentAssignment(byId.get(id))?.positionTitle ?? null,
      departmentOf: (id: string) => currentAssignment(byId.get(id))?.departmentId ?? null,
      /** Unvan/departman bilgisi var mı (yalnızca yönetici ve üstü). */
      hasDetails: Boolean(employees.data),
      employees: employees.data ?? [],
      isPending: directory.isPending,
      error: directory.error,
    }
  }, [directory.data, directory.isPending, directory.error, employees.data, self.data])
}

/* ------------------------------------ Dönem ------------------------------------ */

/** Açık dönem (yoksa en son kapanan) — varsayılan dönem seçimi için. */
export function useCurrentCycle() {
  const cycles = useCycles()
  return useMemo(() => {
    const list = [...(cycles.data ?? [])].sort((a, b) => b.startDate.localeCompare(a.startDate))
    const current = list.find((c) => c.status === 'Open') ?? list.find((c) => c.status === 'Closed') ?? null
    return { cycles: list, current, isPending: cycles.isPending, error: cycles.error }
  }, [cycles.data, cycles.isPending, cycles.error])
}

export function initialsOf(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p.charAt(0).toLocaleUpperCase(appLocale))
    .join('')
}
