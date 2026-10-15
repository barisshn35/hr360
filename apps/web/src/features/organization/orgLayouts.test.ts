import { describe, expect, it } from 'vitest'
import { buildChart } from './orgChartModel'
import { computeLayout, matrixPath, ROOT_ID } from './orgLayouts'
import { defaultCollapsed, LAYOUT_KINDS } from './orgViewState'
import { syntheticOrg } from './orgSynthetic.test-util'

const TODAY = '2026-10-06'

const small = () => {
  const departments = [
    { id: 'a', name: 'Genel Müdürlük', companyId: 'c', parentDepartmentId: null },
    { id: 'b', name: 'Mühendislik', companyId: 'c', parentDepartmentId: 'a' },
    { id: 'c', name: 'Satış', companyId: 'c', parentDepartmentId: 'a' },
    { id: 'd', name: 'Platform', companyId: 'c', parentDepartmentId: 'b' },
  ]
  const emp = (id: string, dept: string) => ({
    id,
    firstName: id,
    lastName: 'X',
    email: `${id}@x.test`,
    hireDate: '2020-01-01',
    status: 0 as const,
    assignments: [{ id: `as-${id}`, departmentId: dept, positionTitle: null, effectiveFrom: '2020-01-01', effectiveTo: null }],
  })
  return buildChart(departments, [emp('p1', 'b'), emp('p2', 'b'), emp('p3', 'd'), emp('p4', 'c')], TODAY)
}

describe('computeLayout', () => {
  it('her yerleşimde tüm görünür departmanları ve şirket kökünü konumlandırır', () => {
    const model = small()
    for (const kind of LAYOUT_KINDS) {
      const l = computeLayout(model, kind, { rootLabel: 'Şirket' })
      expect(l.nodes.map((n) => n.id).sort()).toEqual([ROOT_ID, 'a', 'b', 'c', 'd'].sort())
      expect(l.links).toHaveLength(4)
      for (const n of l.nodes) {
        expect(Number.isFinite(n.x) && Number.isFinite(n.y)).toBe(true)
        expect(n.box.w).toBeGreaterThanOrEqual(0)
      }
      expect(l.bounds.x1).toBeGreaterThanOrEqual(l.bounds.x0)
    }
  })

  it('dikey ağaçta çocuk ebeveynin altında, yatayda sağında', () => {
    const model = small()
    const v = computeLayout(model, 'vertical')
    expect(v.byId.get('b')!.y).toBeGreaterThan(v.byId.get('a')!.y)
    const h = computeLayout(model, 'horizontal')
    expect(h.byId.get('b')!.x).toBeGreaterThan(h.byId.get('a')!.x)
  })

  it('ağaç haritasında alan kişi sayısıyla orantılı ve çocuk ebeveynin içinde', () => {
    const l = computeLayout(small(), 'treemap')
    const area = (id: string) => l.byId.get(id)!.box.w * l.byId.get(id)!.box.h
    expect(area('b')).toBeGreaterThan(area('c')) // 3 kişi > 1 kişi
    const a = l.byId.get('a')!.box
    const d = l.byId.get('d')!.box
    expect(d.x).toBeGreaterThanOrEqual(a.x)
    expect(d.x + d.w).toBeLessThanOrEqual(a.x + a.w)
    expect(l.byId.get('a')!.value).toBe(4)
  })

  it('halka diliminin açısı alt ağaç kişi sayısını izler', () => {
    const l = computeLayout(small(), 'sunburst')
    const span = (id: string) => l.byId.get(id)!.arc!.a1 - l.byId.get(id)!.arc!.a0
    expect(span('b')).toBeGreaterThan(span('c'))
    expect(span('a')).toBeCloseTo(2 * Math.PI, 5)
  })

  it('kapalı dal ve odak', () => {
    const model = small()
    const closed = computeLayout(model, 'vertical', { collapsed: new Set(['b']) })
    expect(closed.byId.has('d')).toBe(false)
    expect(closed.byId.get('b')!.hiddenCount).toBe(1)
    const focused = computeLayout(model, 'radial', { focus: 'b' })
    expect(focused.nodes.map((n) => n.id).sort()).toEqual(['b', 'd'])
    expect(focused.byId.has(ROOT_ID)).toBe(false)
  })

  it('matris eğrisi yalnızca iki ucu görünürken çizilir', () => {
    const model = small()
    const l = computeLayout(model, 'vertical')
    expect(matrixPath(l, 'c', 'd')).toMatch(/^M[-\d.]+,[-\d.]+Q/)
    const closed = computeLayout(model, 'vertical', { collapsed: new Set(['b']) })
    expect(matrixPath(closed, 'c', 'd')).toBeNull()
  })

  it('5000 kişi / 300 departmanlık sentetik kiracıda hesap makul sürede biter', () => {
    const { departments, employees } = syntheticOrg(300, 5000)
    const t0 = performance.now()
    const model = buildChart(departments, employees, TODAY)
    const tModel = performance.now() - t0
    expect(model.byId.size).toBe(300)
    expect(model.deptOf.size).toBe(5000)

    const times: Record<string, number> = {}
    for (const kind of LAYOUT_KINDS) {
      const s = performance.now()
      const l = computeLayout(model, kind, { rootLabel: 'Şirket' })
      times[kind] = performance.now() - s
      expect(l.nodes).toHaveLength(301)
    }
    // Varsayılan kapalı dallarla da (büyük ağaç) çalışır.
    const collapsed = defaultCollapsed(model)
    expect(collapsed.size).toBeGreaterThan(0)
    const s = performance.now()
    computeLayout(model, 'vertical', { collapsed })
    const tCollapsed = performance.now() - s

    // Bilinçli olarak cömert sınır (CI makineleri yavaş olabilir); tipik değer birkaç ms.
    expect(tModel).toBeLessThan(1500)
    for (const t of Object.values(times)) expect(t).toBeLessThan(500)
    expect(tCollapsed).toBeLessThan(500)
  })
})
