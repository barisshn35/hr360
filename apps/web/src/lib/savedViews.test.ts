import { describe, expect, it } from 'vitest'
import { addView, MAX_SAVED_VIEWS, paramsToView, removeView, stripViewParams, viewToParams } from './savedViews'

describe('kayıtlı görünümler (dalga 12)', () => {
  it('sayfa parametrelerini korur, tablo durumunu v_ önekiyle yazar', () => {
    const p = viewToParams(
      { q: ' Ayşe ', sort: { columnId: 'createdAt', dir: 'desc' }, filters: { status: 'Submitted', type: '' } },
      new URLSearchParams('durum=Pending&v_q=eski'),
    )
    expect(p.get('durum')).toBe('Pending')
    expect(p.get('v_q')).toBe('Ayşe')
    expect(p.get('v_s')).toBe('createdAt:desc')
    expect(p.get('v_f.status')).toBe('Submitted')
    expect(p.has('v_f.type')).toBe(false)
  })

  it('adresten geri okur; bozuk sıralama atlanır', () => {
    const v = paramsToView(new URLSearchParams('v_q=izin&v_s=bad&v_f.status=all&x=1'))
    expect(v).toEqual({ q: 'izin', filters: { status: 'all' } })
    expect(paramsToView(new URLSearchParams('durum=Pending'))).toBeNull()
    expect(paramsToView(new URLSearchParams('v_s=name:asc'))?.sort).toEqual({ columnId: 'name', dir: 'asc' })
  })

  it('gidiş-dönüş aynı durumu verir ve v_ parametreleri atılabilir', () => {
    const state = { q: 'Mühendislik', sort: { columnId: 'name', dir: 'asc' as const }, filters: { status: 'Active' } }
    const p = viewToParams(state, new URLSearchParams('tab=list'))
    expect(paramsToView(new URLSearchParams(p.toString()))).toEqual(state)
    expect(stripViewParams(p).toString()).toBe('tab=list')
  })

  it('aynı adlı görünümün üzerine yazar, en fazla 20 tutar', () => {
    let list = addView([], 'Bekleyenler', 'a=1', 'id1')
    list = addView(list, 'bekleyenler', 'a=2', 'id2')
    expect(list).toHaveLength(1)
    expect(list[0]).toMatchObject({ id: 'id2', params: 'a=2', name: 'bekleyenler' })
    expect(addView(list, '   ', 'x', 'id3')).toBe(list)
    for (let i = 0; i < 30; i++) list = addView(list, `v${i}`, '', `k${i}`)
    expect(list).toHaveLength(MAX_SAVED_VIEWS)
    expect(list[0].name).toBe('v29')
    expect(removeView(list, 'k29')).toHaveLength(MAX_SAVED_VIEWS - 1)
  })
})
