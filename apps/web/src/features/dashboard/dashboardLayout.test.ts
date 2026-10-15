import { describe, expect, it } from 'vitest'
import { defaultLayout, moveWidget, resolveLayout, type WidgetId } from './dashboardLayout'

const ALL: WidgetId[] = ['welcome', 'queue', 'pending', 'overdue', 'kpis', 'chart', 'organization', 'modules', 'pinned']

describe('ana panel düzeni (dalga 12)', () => {
  it('kayıt yoksa rol varsayılanı; yetkisiz kart çıkarılır', () => {
    const r = resolveLayout(['welcome', 'pending', 'kpis', 'modules', 'chart'], null, defaultLayout('employee'))
    expect(r.order).toEqual(['welcome', 'pending', 'kpis', 'modules', 'chart'])
    expect(r.hidden.has('chart')).toBe(true)
    expect(resolveLayout(ALL, null, defaultLayout('manager')).order[1]).toBe('queue')
  })

  it('kayıtlı sıra uygulanır; yeni kart varsayılandan sona eklenir, bilinmeyen atılır', () => {
    const r = resolveLayout(ALL, { order: ['modules', 'welcome', 'eski-kart'], hidden: ['kpis', 'yok'] }, defaultLayout('hr'))
    expect(r.order.slice(0, 2)).toEqual(['modules', 'welcome'])
    expect(r.order).toHaveLength(ALL.length)
    expect([...r.hidden]).toEqual(['kpis'])
  })

  it('yukarı/aşağı taşıma', () => {
    const o: WidgetId[] = ['welcome', 'queue', 'kpis']
    expect(moveWidget(o, 'kpis', -1)).toEqual(['welcome', 'kpis', 'queue'])
    expect(moveWidget(o, 'welcome', -1)).toBe(o)
    expect(moveWidget(o, 'kpis', 1)).toBe(o)
  })
})
