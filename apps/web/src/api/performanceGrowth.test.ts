import { describe, expect, it } from 'vitest'
import { bandsOf, cellOf, flattenOkr, type OkrNodeView } from './performanceGrowth'

describe('9-kutu hücre ↔ bant (kalibrasyon sürükle-bırak)', () => {
  it('backend NineBoxMath ile aynı numaralama', () => {
    expect(cellOf(1, 1)).toBe(1)
    expect(cellOf(3, 1)).toBe(3)
    expect(cellOf(1, 3)).toBe(7)
    expect(cellOf(3, 3)).toBe(9)
    for (let c = 1; c <= 9; c++) {
      const b = bandsOf(c)
      expect(cellOf(b.performanceBand, b.potentialBand)).toBe(c)
    }
  })
})

describe('OKR ağacı düzleştirme', () => {
  const node = (id: string, children: OkrNodeView[] = []): OkrNodeView => ({ id, kind: 'Company', title: id, weight: 100, progress: null, children })
  const tree = [node('a', [node('b', [node('c')]), node('d')])]

  it('derinlikle sıralı düğümler', () => {
    expect(flattenOkr(tree, new Set()).map((r) => `${r.node.id}${r.depth}`)).toEqual(['a0', 'b1', 'c2', 'd1'])
  })

  it('katlanan dalın çocukları gizlenir', () => {
    expect(flattenOkr(tree, new Set(['b'])).map((r) => r.node.id)).toEqual(['a', 'b', 'd'])
  })
})
