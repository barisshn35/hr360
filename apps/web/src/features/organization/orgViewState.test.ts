import { describe, expect, it } from 'vitest'
import { buildChart } from './orgChartModel'
import {
  applyViewParams,
  decodeViewState,
  DEFAULT_VIEW,
  encodeViewState,
  navigate,
  PARAM,
  visibleOrder,
  type OrgViewState,
} from './orgViewState'

const IDS = [
  '0e879b9e-d72b-489f-aa5b-8291e0bcbefb',
  '1a2b3c4d-0000-4000-8000-000000000001',
  '9f8e7d6c-0000-4000-8000-000000000002',
]

describe('görünüm durumu ↔ adres', () => {
  it('varsayılan durum adrese hiçbir şey yazmaz', () => {
    const p = applyViewParams(new URLSearchParams('gorunum=sema'), DEFAULT_VIEW)
    expect(p.toString()).toBe('gorunum=sema')
  })

  it('tüm alanlar gidip gelir (kapalı dallar kısaltılmış kimlikle)', () => {
    const state: OrgViewState = {
      layout: 'sunburst',
      encoding: 'tenure',
      focus: IDS[0],
      collapsed: new Set([IDS[1], IDS[2]]),
      matrix: true,
      zoom: 0.75,
      selected: `p:${IDS[2]}`,
    }
    const p = applyViewParams(new URLSearchParams('gorunum=sema'), state)
    expect(p.get('gorunum')).toBe('sema')
    expect(p.get(PARAM.layout)).toBe('halka')
    expect(p.get(PARAM.collapsed)!.split('.')).toEqual(['1a2b3c4d', '9f8e7d6c'])
    const back = decodeViewState(new URLSearchParams(p.toString()), IDS)
    expect(back).toEqual(state)
  })

  it('boş kapalı listesi "hiçbiri kapalı değil" demektir, yokluğu varsayılan', () => {
    expect(decodeViewState(new URLSearchParams(`${PARAM.collapsed}=`), IDS).collapsed).toEqual(new Set())
    expect(decodeViewState(new URLSearchParams(''), IDS).collapsed).toBeNull()
  })

  it('bozuk/bilinmeyen değerleri yok sayar', () => {
    const p = new URLSearchParams({
      [PARAM.layout]: 'uzay',
      [PARAM.encoding]: 'maas',
      [PARAM.focus]: 'yok',
      [PARAM.collapsed]: 'zzzzzzzz.1a2b3c4d',
      [PARAM.zoom]: 'abc',
      [PARAM.selected]: 'd:baska',
    })
    const s = decodeViewState(p, IDS)
    expect(s.layout).toBe('vertical')
    expect(s.encoding).toBe('department')
    expect(s.focus).toBeNull()
    expect(s.collapsed).toEqual(new Set([IDS[1]]))
    expect(s.zoom).toBe(1)
    expect(s.selected).toBeNull()
  })

  it('yakınlık sınırlanır ve 1 iken yazılmaz', () => {
    expect(encodeViewState({ ...DEFAULT_VIEW, zoom: 1 })[PARAM.zoom]).toBeNull()
    expect(decodeViewState(new URLSearchParams(`${PARAM.zoom}=99`), IDS).zoom).toBe(4)
  })
})

describe('klavye gezintisi', () => {
  const deps = [
    { id: 'a', name: 'A', companyId: 'c', parentDepartmentId: null },
    { id: 'b', name: 'B', companyId: 'c', parentDepartmentId: 'a' },
    { id: 'c', name: 'C', companyId: 'c', parentDepartmentId: 'a' },
    { id: 'd', name: 'D', companyId: 'c', parentDepartmentId: 'b' },
    { id: 'e', name: 'E', companyId: 'c', parentDepartmentId: null },
  ]
  const model = buildChart(deps, [], '2026-10-06')
  const none = new Set<string>()

  it('ön-sıra görünür düzen kapalı dalı atlar', () => {
    expect(visibleOrder(model.roots, none).map((n) => n.dept.id)).toEqual(['a', 'b', 'd', 'c', 'e'])
    expect(visibleOrder(model.roots, new Set(['b'])).map((n) => n.dept.id)).toEqual(['a', 'b', 'c', 'e'])
  })

  it('ağaç deseni (liste)', () => {
    expect(navigate('ArrowDown', 'b', model.roots, none, 'tree')).toEqual({ move: 'd' })
    expect(navigate('ArrowUp', 'c', model.roots, none, 'tree')).toEqual({ move: 'd' })
    expect(navigate('ArrowLeft', 'b', model.roots, none, 'tree')).toEqual({ toggle: 'b' })
    expect(navigate('ArrowLeft', 'b', model.roots, new Set(['b']), 'tree')).toEqual({ move: 'a' })
    expect(navigate('ArrowRight', 'b', model.roots, new Set(['b']), 'tree')).toEqual({ toggle: 'b' })
    expect(navigate('End', 'a', model.roots, none, 'tree')).toEqual({ move: 'e' })
  })

  it('yukarıdan aşağı ağaç: ↑ ebeveyn, ↓ çocuk, ←/→ kardeş', () => {
    expect(navigate('ArrowUp', 'd', model.roots, none, 'down')).toEqual({ move: 'b' })
    expect(navigate('ArrowDown', 'a', model.roots, none, 'down')).toEqual({ move: 'b' })
    expect(navigate('ArrowRight', 'b', model.roots, none, 'down')).toEqual({ move: 'c' })
    expect(navigate('ArrowRight', 'a', model.roots, none, 'down')).toEqual({ move: 'e' })
    expect(navigate('ArrowLeft', 'a', model.roots, none, 'down')).toBeNull()
  })
})
