import { describe, expect, it } from 'vitest'
import type { TimeSnapshot } from '@/api/governance'
import type { OrgMove } from '@/api/engagement'
import type { Department, Employee } from '@/api/types'
import { buildChart } from './orgChartModel'
import { departmentsAt, monthStops, nearestStop, parseDateParam, snapshotEmployees, timeDelta } from './orgTimeline'
import { applyScenario, diffSubtitle, parseCompareParam, parseScenarioParam, scenarioDiff } from './orgScenario'

const TODAY = '2026-10-06'

const depts: Department[] = [
  { id: 'a', name: 'Genel', companyId: 'c', parentDepartmentId: null },
  { id: 'b', name: 'Mühendislik', companyId: 'c', parentDepartmentId: 'a' },
  { id: 'c', name: 'Satış', companyId: 'c', parentDepartmentId: 'a' },
]
const emp = (id: string, dept: string | null, extra: Partial<Employee> = {}): Employee => ({
  id,
  firstName: id,
  lastName: 'X',
  email: `${id}@x.test`,
  hireDate: '2020-01-01',
  status: 0 as Employee['status'],
  assignments: dept ? [{ id: `as-${id}`, departmentId: dept, positionTitle: 'Uzman', effectiveFrom: '2020-01-01', effectiveTo: null }] : [],
  ...extra,
})

describe('zaman kaydırıcısı', () => {
  it('tarih parametresi: geçerli ve bugünden önce olmalı', () => {
    expect(parseDateParam('2025-02-28', TODAY)).toBe('2025-02-28')
    expect(parseDateParam('2025-02-30', TODAY)).toBeNull()
    expect(parseDateParam(TODAY, TODAY)).toBeNull()
    expect(parseDateParam('2027-01-01', TODAY)).toBeNull()
    expect(parseDateParam('dun', TODAY)).toBeNull()
    expect(parseDateParam(null, TODAY)).toBeNull()
  })

  it('duraklar: geçmiş ay sonları + bugün', () => {
    const s = monthStops(TODAY, 3)
    expect(s).toEqual(['2026-07-31', '2026-08-31', '2026-09-30', TODAY])
    expect(monthStops('2026-03-15', 2)).toEqual(['2026-01-31', '2026-02-28', '2026-03-15'])
    expect(nearestStop(s, null)).toBe(3)
    expect(nearestStop(s, '2026-08-30')).toBe(1)
  })

  it('anlık görüntü → şema girdisi; o tarihte olmayan departman düşer', () => {
    const snap: Pick<TimeSnapshot, 'departments' | 'date' | 'departmentIds'> = {
      date: '2025-06-30',
      departmentIds: ['a', 'b'],
      departments: [
        { departmentId: 'b', department: 'Mühendislik', count: 2, head: null, people: [
          { employeeId: 'p1', name: 'Ayşe Y', position: 'Uzman', hireDate: '2020-01-01', isHead: false },
          { employeeId: 'p2', name: 'Can K', position: null, hireDate: '2021-01-01', isHead: false },
        ] },
        { departmentId: null, department: 'Atanmamış', count: 1, head: null, people: [
          { employeeId: 'p3', name: 'Ece T', position: null, hireDate: '2022-01-01', isHead: false },
        ] },
      ],
    }
    const model = buildChart(departmentsAt(depts, snap), snapshotEmployees(snap), snap.date)
    expect([...model.byId.keys()].sort()).toEqual(['a', 'b'])
    expect(model.byId.get('b')!.members.map((m) => m.name)).toEqual(['Ayşe Y', 'Can K'])
    expect(model.unassigned.map((p) => p.id)).toEqual(['p3'])
    // Liste yoksa (eski sunucu) tüm departmanlar.
    expect(departmentsAt(depts, {})).toHaveLength(3)
  })

  it('iki tarih arasındaki fark (parlayacak düğümler)', () => {
    const before = buildChart(depts.slice(0, 2), [emp('p1', 'b'), emp('p2', 'b')], TODAY)
    const after = buildChart(depts, [emp('p1', 'b'), emp('p2', 'c'), emp('p3', 'c')], TODAY)
    const d = timeDelta(before, after)
    expect([...d.changed.entries()]).toEqual([['b', { before: 2, after: 1 }]])
    expect([...d.added]).toEqual(['c'])
    expect(d.removed.size).toBe(0)
    expect(d.moved).toBe(1)
  })
})

describe('senaryo karşılaştırma', () => {
  const employees = [emp('p1', 'b'), emp('p2', 'b'), emp('p3', 'c'), emp('p4', null), emp('old', 'b', { status: 2 as Employee['status'] })]
  const moves: OrgMove[] = [
    { employeeId: 'p1', name: 'p1 X', fromDepartmentId: 'b', toDepartmentId: 'c', kind: 'Move', newPosition: 'Lider' },
    { employeeId: 'p3', name: 'p3 X', fromDepartmentId: 'c', kind: 'Exit' },
    { employeeId: '', name: 'Yeni mühendis', toDepartmentId: 'b', kind: 'Hire', plannedSalary: 99999 },
  ]

  it('adres parametreleri', () => {
    expect(parseScenarioParam('0e879b9e-d72b-489f-aa5b-8291e0bcbefb')).not.toBeNull()
    expect(parseScenarioParam('x')).toBeNull()
    expect(parseCompareParam('yan')).toBe('side')
    expect(parseCompareParam(null)).toBe('overlay')
  })

  it('hareketler taslağa uygulanır; planlanan maaş taşınmaz', () => {
    const draft = applyScenario(employees, moves, TODAY)
    expect(draft.map((e) => e.id).sort()).toEqual(['hire-2', 'p1', 'p2', 'p4'])
    const p1 = draft.find((e) => e.id === 'p1')!
    expect(p1.assignments?.[0]).toMatchObject({ departmentId: 'c', positionTitle: 'Lider' })
    expect(JSON.stringify(draft)).not.toContain('99999')
  })

  it('departman başına fark ve özet', () => {
    const base = buildChart(depts, employees, TODAY)
    const draft = buildChart(depts, applyScenario(employees, moves, TODAY), TODAY)
    const d = scenarioDiff(base, draft)
    // b: p1, p2 → p2 + yeni (2 → 2, kişileri değişti); c: p3 → p1 (1 → 1, değişti); a: değişmedi.
    expect(d.byDept.get('b')).toMatchObject({ before: 2, after: 2, incoming: 1, outgoing: 1, status: 'changed' })
    expect(d.byDept.get('c')).toMatchObject({ before: 1, after: 1, status: 'changed' })
    expect(d.byDept.get('a')!.status).toBe('same')
    expect(d).toMatchObject({ moved: 1, hires: 1, exits: 1, affected: 2 })
    expect(diffSubtitle({ before: 3, after: 5, incoming: 2, outgoing: 0, status: 'grew' })).toContain('+2')
  })

  it('büyüyen ve küçülen departman', () => {
    const base = buildChart(depts, [emp('p1', 'b'), emp('p2', 'b')], TODAY)
    const draft = buildChart(depts, applyScenario([emp('p1', 'b'), emp('p2', 'b')], [{ employeeId: 'p1', name: 'p1', toDepartmentId: 'c', kind: 'Move' }], TODAY), TODAY)
    const d = scenarioDiff(base, draft)
    expect(d.byDept.get('b')!.status).toBe('shrank')
    expect(d.byDept.get('c')!.status).toBe('grew')
  })
})
