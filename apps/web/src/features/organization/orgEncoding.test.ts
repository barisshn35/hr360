import { describe, expect, it } from 'vitest'
import { buildChart } from './orgChartModel'
import { deptLeaveShare, deptTenureBand, leaveBucket, MIN_GROUP, nodeColor, NEUTRAL, tenureBand } from './orgEncoding'

const TODAY = '2026-10-06'
const emp = (id: string, dept: string, hire: string) => ({
  id,
  firstName: id,
  lastName: 'X',
  email: `${id}@x.test`,
  hireDate: hire,
  status: 0 as const,
  assignments: [{ id: `as-${id}`, departmentId: dept, positionTitle: null, effectiveFrom: hire, effectiveTo: null }],
})
const deps = [
  { id: 'big', name: 'Büyük', companyId: 'c', parentDepartmentId: null },
  { id: 'small', name: 'Küçük', companyId: 'c', parentDepartmentId: null },
]
const people = [
  ...['a', 'b', 'c', 'd', 'e'].map((x, i) => emp(x, 'big', `${2016 + i}-01-01`)),
  emp('f', 'small', '2025-01-01'),
  emp('g', 'small', '2025-01-01'),
]
const model = buildChart(deps, people, TODAY)

describe('renklendirme (KVKK: 5 kişiden küçük grupta özet yok)', () => {
  it('kıdem bandı', () => {
    expect(tenureBand('2026-01-01', TODAY)).toBe('lt1')
    expect(tenureBand('2014-01-01', TODAY)).toBe('gte10')
    expect(tenureBand(null, TODAY)).toBeNull()
    expect(tenureBand('2030-01-01', TODAY)).toBeNull()
  })

  it('departman ortanca kıdemi yalnızca en az 5 kişide', () => {
    expect(MIN_GROUP).toBe(5)
    expect(deptTenureBand(model.byId.get('big')!, TODAY)).toBe('y5to10') // ortanca 2018 → 8 yıl
    expect(deptTenureBand(model.byId.get('small')!, TODAY)).toBeNull()
    expect(nodeColor('tenure', model.byId.get('small'), 1, { today: TODAY, onLeave: null })).toBe(NEUTRAL)
  })

  it('izindekilerin oranı yalnızca en az 5 kişide', () => {
    const onLeave = new Set(['a', 'f'])
    expect(deptLeaveShare(model.byId.get('big')!, onLeave)).toBeCloseTo(0.2)
    expect(deptLeaveShare(model.byId.get('small')!, onLeave)).toBeNull()
    expect(leaveBucket(0)).toBe('none')
    expect(leaveBucket(0.2)).toBe('mid')
    expect(nodeColor('leave', model.byId.get('small'), 1, { today: TODAY, onLeave })).toBe(NEUTRAL)
  })
})
