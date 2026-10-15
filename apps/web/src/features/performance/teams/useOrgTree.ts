/**
 * Şirket → Departman (→ alt departman …) → Ekip → Üyeler hiyerarşisi.
 *
 * Toplam iki-üç istek:
 *   1. şirketler + departmanlar          (organizasyon servisi)
 *   2. ekipler, üyeleriyle birlikte      (`?includeMembers=true`)
 *   3. çalışan dizini                     (ad çözümü, her rol)
 * Yönetici ve üstünde ayrıca tam çalışan listesi (unvan, güncel departman)
 * zaten önbellekte olur; "ekipte olmayanlar" yalnızca onunla hesaplanır —
 * çalışan rolünde bu bölüm hiç gösterilmez.
 */

import { useMemo } from 'react'
import { useTeams, type Team } from '@/api/performance'
import { useCompanies } from '@/api/queries'
import { buildDeptTree, usePeople, type DeptNode } from '../hooks'

export interface OrgPerson {
  id: string
  name: string
  title: string | null
}

export interface OrgMember extends OrgPerson {
  memberId: string
  roleInTeam: string | null
  isLead: boolean
  joinedOn: string
  leftOn: string | null
}

export interface OrgTeam {
  team: Team
  lead: OrgPerson | null
  members: OrgMember[]
}

export interface OrgDepartment {
  id: string
  name: string
  path: string
  depth: number
  /** Kök departmanın sırası — renk için; alt departmanlar ebeveynin rengini alır. */
  colorIndex: number
  teams: OrgTeam[]
  children: OrgDepartment[]
  /** Departmanda olup hiçbir etkin ekipte olmayanlar. Detay yoksa `null` (çalışan rolü). */
  unassigned: OrgPerson[] | null
  /** Alt departmanlar dahil. Çalışan listesi yoksa `null`. */
  employeeCount: number | null
  /** Alt departmanlar dahil etkin ekip sayısı. */
  teamCount: number
}

export interface OrgCompany {
  id: string
  name: string
  departments: OrgDepartment[]
  departmentCount: number
  teamCount: number
  employeeCount: number | null
  memberCount: number
}

export function walkDepartments(nodes: OrgDepartment[]): OrgDepartment[] {
  return nodes.flatMap((d) => [d, ...walkDepartments(d.children)])
}

export function useOrgTree({ includeInactive = false }: { includeInactive?: boolean } = {}) {
  const companies = useCompanies()
  const teams = useTeams({ includeInactive: includeInactive || undefined, includeMembers: true })
  const people = usePeople()

  const tree = useMemo<OrgCompany[]>(() => {
    const person = (id: string): OrgPerson => ({ id, name: people.nameOf(id), title: people.titleOf(id) })

    const toOrgTeam = (t: Team): OrgTeam => {
      const members = (t.members ?? [])
        .filter((m) => !m.leftOn)
        .map((m) => ({
          ...person(m.employeeId),
          name: people.nameOf(m.employeeId, m.employeeName),
          memberId: m.id,
          roleInTeam: m.roleInTeam,
          isLead: m.isLead || m.employeeId === t.leadEmployeeId,
          joinedOn: m.joinedOn,
          leftOn: m.leftOn,
        }))
        .sort((a, b) => Number(b.isLead) - Number(a.isLead) || a.name.localeCompare(b.name, 'tr-TR'))
      return {
        team: t,
        lead: t.leadEmployeeId ? (members.find((m) => m.isLead) ?? person(t.leadEmployeeId)) : null,
        members,
      }
    }

    const activeEmployees = people.employees.filter((e) => e.status !== 2)

    return (companies.data ?? []).map((c) => {
      const roots = buildDeptTree(c.departments ?? [], c.name)

      const convert = (d: DeptNode, colorIndex: number): OrgDepartment => {
        const children = d.children.map((ch) => convert(ch, colorIndex))
        const deptTeams = (teams.data ?? [])
          .filter((t) => t.departmentId === d.id)
          .map(toOrgTeam)
          .sort((a, b) => Number(b.team.isActive) - Number(a.team.isActive) || a.team.name.localeCompare(b.team.name, 'tr-TR'))
        const inTeam = new Set(deptTeams.filter((t) => t.team.isActive).flatMap((t) => t.members.map((m) => m.id)))
        const ownEmployees = people.hasDetails ? activeEmployees.filter((e) => people.departmentOf(e.id) === d.id) : null
        const childEmployees = children.reduce<number | null>((a, ch) => (a === null || ch.employeeCount === null ? null : a + ch.employeeCount), 0)
        return {
          id: d.id,
          name: d.name,
          path: d.path,
          depth: d.depth,
          colorIndex,
          teams: deptTeams,
          children,
          unassigned: ownEmployees ? ownEmployees.filter((e) => !inTeam.has(e.id)).map((e) => person(e.id)) : null,
          employeeCount: ownEmployees && childEmployees !== null ? ownEmployees.length + childEmployees : null,
          teamCount: deptTeams.filter((t) => t.team.isActive).length + children.reduce((a, ch) => a + ch.teamCount, 0),
        }
      }

      const departments = roots.map((r, i) => convert(r, i))
      const all = walkDepartments(departments)
      return {
        id: c.id,
        name: c.name,
        departments,
        departmentCount: all.length,
        teamCount: departments.reduce((a, d) => a + d.teamCount, 0),
        employeeCount: people.hasDetails ? activeEmployees.length : people.list.length || null,
        memberCount: new Set(all.flatMap((d) => d.teams.filter((t) => t.team.isActive).flatMap((t) => t.members.map((m) => m.id)))).size,
      }
    })
  }, [companies.data, teams.data, people])

  /**
   * Hiçbir etkin ekipte olmayan aktif çalışan sayısı (aktif çalışanlar − etkin ekip üyeleri).
   * Departman toplamları bunu vermez: başka departmanın ekibindeki ya da departmansız kişi kaçıyordu.
   */
  const unassignedCount = useMemo<number | null>(() => {
    if (!people.hasDetails) return null
    const members = new Set((teams.data ?? []).filter((t) => t.isActive).flatMap((t) => (t.members ?? []).filter((m) => !m.leftOn).map((m) => m.employeeId)))
    return people.employees.filter((e) => e.status !== 2 && !members.has(e.id)).length
  }, [teams.data, people])

  return {
    tree,
    unassignedCount,
    isPending: companies.isPending || teams.isPending,
    error: companies.error ?? teams.error ?? null,
    /** Dizin alınamadıysa adlar eksik olabilir — ekranda not düşülür. */
    namesUnavailable: Boolean(people.error),
    refetch: () => {
      void companies.refetch()
      void teams.refetch()
    },
  }
}
