import { describe, expect, it } from 'vitest'
import type { TeamNetwork } from '@/api/governance'
import { edgeKinds, neighborsOf, OTHER_ID, shapeTeamNetwork } from './teamNetworkModel'

const net = (): TeamNetwork => ({
  days: 90,
  since: '2026-07-08',
  minTeamSize: 5,
  minEdgeCount: 3,
  hiddenTeams: 1,
  hiddenEdges: 2,
  nodes: [
    { id: 't1', name: 'Platform', department: 'Mühendislik', members: 9, mergedTeams: 1, internal: { kudos: 4, oneOnOnes: 0, sharedGoals: 0 } },
    { id: 't2', name: 'Mobil', department: 'Mühendislik', members: 5, mergedTeams: 1, internal: { kudos: 0, oneOnOnes: 0, sharedGoals: 0 } },
    { id: 't3', name: 'Kurumsal', department: 'Satış', members: 16, mergedTeams: 1, internal: { kudos: 0, oneOnOnes: 3, sharedGoals: 0 } },
    { id: OTHER_ID, name: 'Diğer', department: null, members: 7, mergedTeams: 3, internal: { kudos: 0, oneOnOnes: 0, sharedGoals: 0 } },
  ],
  edges: [
    { source: 't1', target: 't2', weight: 3, kudos: 3, oneOnOnes: 0, sharedGoals: 0 },
    { source: 't1', target: 't3', weight: 12, kudos: 5, oneOnOnes: 4, sharedGoals: 3 },
    { source: 't3', target: 'yok', weight: 50, kudos: 50, oneOnOnes: 0, sharedGoals: 0 },
  ],
})

describe('ekip ağı veri biçimlendirme', () => {
  it('yalnızca bilinen ekipler arası bağlar, ağırlığa göre sıralı', () => {
    const g = shapeTeamNetwork(net())
    expect(g.edges.map((e) => `${e.source}-${e.target}`)).toEqual(['t1-t3', 't1-t2'])
    expect(g.maxWeight).toBe(12)
    expect(g.scene.nodes).toHaveLength(4)
    expect(g.scene.links).toHaveLength(2)
  })

  it('renk departmana göre; Diğer nötr; boyut üye sayısıyla artar', () => {
    const g = shapeTeamNetwork(net())
    const n = (id: string) => g.scene.nodes.find((x) => x.id === id)!
    expect(n('t1').colorIndex).toBe(n('t2').colorIndex)
    expect(n('t3').colorIndex).not.toBe(n('t1').colorIndex)
    expect(n(OTHER_ID).colorIndex).toBe(-1)
    expect(n('t3').r).toBeGreaterThan(n('t2').r)
    expect(g.departments.map((d) => d.name)).toEqual(['Mühendislik', 'Satış'])
  })

  it('alt sınır süzgeci zayıf bağları çıkarır', () => {
    const g = shapeTeamNetwork(net(), 10)
    expect(g.edges).toHaveLength(1)
    expect(neighborsOf(g, 't1').map((x) => x.other)).toEqual(['t3'])
    expect(neighborsOf(g, 't2')).toEqual([])
  })

  it('bağ türü metni yalnızca var olan türleri içerir', () => {
    expect(edgeKinds({ kudos: 5, oneOnOnes: 0, sharedGoals: 3 })).toBe('5 takdir · 3 ortak hedef')
    expect(edgeKinds({ kudos: 0, oneOnOnes: 0, sharedGoals: 0 })).toBe('')
  })

  it('konumlar deterministik', () => {
    const a = shapeTeamNetwork(net()).scene.nodes.map((n) => [n.x, n.y, n.z])
    const b = shapeTeamNetwork(net()).scene.nodes.map((n) => [n.x, n.y, n.z])
    expect(a).toEqual(b)
  })
})
