import { describe, expect, it } from 'vitest'
import { buildChart } from './orgChartModel'
import { computeLayout, ROOT_ID } from './orgLayouts'
import { defaultCollapsed, LAYOUT_KINDS } from './orgViewState'
import { capNodes, forceLayout3d, galaxyLayout, LAYER_GAP, labelCandidates, layered3d, MAX_NODES_3D, sizeOf, type Node3D } from './org3d'
import { syntheticOrg } from './orgSynthetic.test-util'

const TODAY = '2026-10-06'

const node = (id: string, depth: number, value = 0): Node3D => ({
  id, name: id, depth, parentId: null, x: 0, y: 0, z: 0, r: 5, colorIndex: 0, value, childCount: 0, hiddenCount: 0,
})

describe('3B katmanlı yerleşim', () => {
  const org = syntheticOrg(40, 300)
  const model = buildChart(org.departments, org.employees, TODAY)

  it('3b yerleşim türü adreste ve yerleşim listesinde', () => {
    expect(LAYOUT_KINDS).toContain('layers3d')
  })

  it('her seviye ayrı bir kat: aynı derinlik aynı y, kök en üstte', () => {
    const layout = computeLayout(model, 'layers3d', { collapsed: new Set(), rootLabel: 'Şirket' })
    const d = layered3d(layout, { peopleKnown: true })
    expect(d.nodes).toHaveLength(layout.nodes.length)
    const byDepth = new Map<number, Set<number>>()
    for (const n of d.nodes) byDepth.set(n.depth, (byDepth.get(n.depth) ?? new Set()).add(Math.round(n.y)))
    for (const ys of byDepth.values()) expect(ys.size).toBe(1)
    const root = d.nodes.find((n) => n.id === ROOT_ID)!
    expect(root.y).toBe(Math.max(...d.nodes.map((n) => n.y)))
    const deep = d.nodes.find((n) => n.depth === 2)!
    expect(root.y - deep.y).toBeCloseTo(2 * LAYER_GAP)
  })

  it('kat içi konum radyal ağacın açı/yarıçapından (x/z düzlemi)', () => {
    const layout = computeLayout(model, 'layers3d', { collapsed: new Set() })
    const d = layered3d(layout, { peopleKnown: true })
    for (const n of d.nodes) {
      const src = layout.byId.get(n.id)!
      expect(Math.hypot(n.x, n.z)).toBeCloseTo(src.radius ?? 0, 5)
    }
    // Sınırlayıcı küre tüm düğümleri kapsar.
    for (const n of d.nodes) expect(Math.hypot(n.x - d.center.x, n.y - d.center.y, n.z - d.center.z)).toBeLessThanOrEqual(d.radius + 1e-6)
  })

  it('kişi sayısı bilinmiyorsa boyut sabit (departman düzeyi)', () => {
    expect(sizeOf(500, false)).toBe(sizeOf(1, false))
    expect(sizeOf(100, true)).toBeGreaterThan(sizeOf(4, true))
    expect(sizeOf(1e9, true)).toBeLessThanOrEqual(40)
  })

  it('büyük kiracıda (5000 kişi / 300 departman) bütçe ve varsayılan kapalı dallar', () => {
    const big = syntheticOrg(300, 5000)
    const m = buildChart(big.departments, big.employees, TODAY)
    const all = layered3d(computeLayout(m, 'layers3d', { collapsed: new Set() }), { peopleKnown: true, maxNodes: 120 })
    expect(all.nodes).toHaveLength(120)
    expect(all.truncated).toBe(301 - 120)
    // Kırpılınca bağların iki ucu da çizilmiş olmalı.
    const ids = new Set(all.nodes.map((n) => n.id))
    for (const l of all.links) expect(ids.has(l.source) && ids.has(l.target)).toBe(true)
    // Varsayılan kapalı dallarla bütçenin altında kalır.
    const dflt = layered3d(computeLayout(m, 'layers3d', { collapsed: defaultCollapsed(m) }), { peopleKnown: true })
    expect(dflt.nodes.length).toBeLessThanOrEqual(MAX_NODES_3D)
    expect(dflt.truncated).toBe(0)
  })
})

describe('bütçe ve etiketler', () => {
  it('capNodes sığ ve kalabalık düğümleri tutar, sırayı korur', () => {
    const nodes = [node('a', 0), node('b', 2, 50), node('c', 1, 1), node('d', 2, 99)]
    const r = capNodes(nodes, [{ source: 'a', target: 'b' }, { source: 'a', target: 'c' }], 3)
    expect(r.nodes.map((n) => n.id)).toEqual(['a', 'c', 'd'])
    expect(r.links).toEqual([{ source: 'a', target: 'c' }])
    expect(r.truncated).toBe(1)
  })

  it('etiket önceliği: seçili, vurgulu, sonra sığ düğümler; sınır aşılmaz', () => {
    const nodes = Array.from({ length: 100 }, (_, i) => node(`n${i}`, i % 4, i))
    const ids = labelCandidates(nodes, { selected: 'n99', highlight: new Set(['n98', 'yok']) }, 10)
    expect(ids).toHaveLength(10)
    expect(ids.slice(0, 2)).toEqual(['n99', 'n98'])
    expect(ids).not.toContain('yok')
  })
})

describe('şirket grubu galaksisi', () => {
  const companies = [
    {
      id: 'c1',
      name: 'Alfa',
      departments: [
        { id: 'a', name: 'Genel', companyId: 'c1', parentDepartmentId: null },
        { id: 'b', name: 'Mühendislik', companyId: 'c1', parentDepartmentId: 'a' },
        { id: 'c', name: 'Platform', companyId: 'c1', parentDepartmentId: 'b' },
      ],
    },
    { id: 'c2', name: 'Beta', departments: [{ id: 'x', name: 'Satış', companyId: 'c2', parentDepartmentId: null }] },
  ]
  const heads: Record<string, number> = { a: 2, b: 5, c: 10, x: 3 }

  it('şirketler merkez, departmanlar seviyesine göre yörüngede', () => {
    const g = galaxyLayout(companies, (id) => heads[id] ?? 0, true)
    const alfa = g.nodes.find((n) => n.id === 'company:c1')!
    const beta = g.nodes.find((n) => n.id === 'company:c2')!
    expect(alfa.kind).toBe('company')
    expect(Math.hypot(alfa.x - beta.x, alfa.z - beta.z)).toBeGreaterThan(100)
    const dist = (id: string) => {
      const n = g.nodes.find((x) => x.id === id)!
      return Math.hypot(n.x - alfa.x, n.z - alfa.z)
    }
    expect(dist('a')).toBeLessThan(dist('b'))
    expect(dist('b')).toBeLessThan(dist('c'))
    // Bağlar: kök departman şirkete, alt departman üstüne.
    expect(g.links).toContainEqual({ source: 'company:c1', target: 'a' })
    expect(g.links).toContainEqual({ source: 'b', target: 'c' })
    // Her şirket ve seviye için bir yörünge halkası.
    expect(g.rings).toHaveLength(3 + 1)
  })

  it('boyut kişi sayısından; kişi bilinmiyorsa departman sayısından', () => {
    const known = galaxyLayout(companies, (id) => heads[id] ?? 0, true)
    const a = known.nodes.find((n) => n.id === 'a')!
    expect(a.value).toBe(17) // alt ağaç toplamı
    const unknown = galaxyLayout(companies, () => 999, false)
    const ua = unknown.nodes.find((n) => n.id === 'a')!
    expect(ua.value).toBe(0)
    const alfaU = unknown.nodes.find((n) => n.id === 'company:c1')!
    const betaU = unknown.nodes.find((n) => n.id === 'company:c2')!
    expect(alfaU.r).toBeGreaterThan(betaU.r) // 3 departman > 1 departman
  })

  it('döngülü departmanlar kaybolmaz, tek şirkette merkez başlangıçta', () => {
    const cyc = [{ id: 'c', name: 'C', departments: [
      { id: 'p', name: 'P', companyId: 'c', parentDepartmentId: 'q' },
      { id: 'q', name: 'Q', companyId: 'c', parentDepartmentId: 'p' },
    ] }]
    const g = galaxyLayout(cyc, () => 1, true)
    expect(g.nodes.map((n) => n.id).sort()).toEqual(['company:c', 'p', 'q'])
    expect(g.nodes.find((n) => n.id === 'company:c')!.x).toBe(0)
  })
})

describe('kuvvet yönlendirmeli 3B yerleşim', () => {
  it('deterministik ve sonlu', () => {
    const ids = ['a', 'b', 'c', 'd']
    const edges = [{ source: 'a', target: 'b', weight: 10 }, { source: 'c', target: 'd', weight: 1 }]
    const p1 = forceLayout3d(ids, edges)
    const p2 = forceLayout3d(ids, edges)
    expect([...p1.entries()]).toEqual([...p2.entries()])
    for (const p of p1.values()) expect(Number.isFinite(p.x + p.y + p.z)).toBe(true)
  })

  it('güçlü bağlı düğümler bağsızlardan yakın', () => {
    const ids = ['a', 'b', 'c', 'd', 'e', 'f']
    const pos = forceLayout3d(ids, [{ source: 'a', target: 'b', weight: 20 }])
    const d = (x: string, y: string) => {
      const p = pos.get(x)!, q = pos.get(y)!
      return Math.hypot(p.x - q.x, p.y - q.y, p.z - q.z)
    }
    expect(d('a', 'b')).toBeLessThan(d('c', 'd'))
  })

  it('boş ve tek düğüm', () => {
    expect(forceLayout3d([], []).size).toBe(0)
    const one = forceLayout3d(['x'], [{ source: 'x', target: 'x', weight: 3 }])
    expect(one.get('x')).toEqual({ x: 0, y: 0, z: 0 })
  })
})
