import { describe, expect, it } from 'vitest'
import { freshnessState, nearestRow, polyline, reasonText, reliabilityPoints } from './modelQuality'
import type { ThresholdRow } from '@/api/mlModel'

const row = (threshold: number): ThresholdRow => ({ threshold, flag_rate: 1 - threshold, precision: 0.5, recall: 0.5, false_positive_rate: 0.1, f1: 0.5 })

describe('modelQuality', () => {
  it('güvenilirlik eğrisi gizlenen kovaları çizmez', () => {
    const pts = reliabilityPoints([
      { lo: 0, hi: 0.1, count: 40, mean_predicted: 0.05, observed_rate: 0.1, suppressed: false },
      { lo: 0.9, hi: 1, count: 3, mean_predicted: null, observed_rate: null, suppressed: true },
    ], 200)
    expect(pts).toHaveLength(1)
    expect(pts[0]).toMatchObject({ x: 10, y: 180 })
    expect(polyline(pts)).toBe('10,180')
  })

  it('kayıtlı eşiğe en yakın tablo satırı', () => {
    const rows = [0.3, 0.35, 0.4].map(row)
    expect(nearestRow(rows, 0.37)?.threshold).toBe(0.35)
    expect(nearestRow(rows, 0.5)?.threshold).toBe(0.4)
    expect(nearestRow([], 0.5)).toBeUndefined()
  })

  it('sade dilde neden: kod çeviri anahtarına eşlenir, bilinmeyen kodda sunucu metni', () => {
    expect(reasonText({ feature: 'compa_ratio', code: 'pay_below', params: [82], direction: 'up', contribution: 0.1, text: 'x' }))
      .toBe('Ücreti bant ortasının altında (%82) (+)')
    expect(reasonText({ feature: 'overtime_hours_month', code: 'overtime', params: [30], direction: 'down', contribution: -0.1, text: 'x' }))
      .toBe('Aylık ortalama 30 saat fazla mesai (−)')
    expect(reasonText({ feature: 'f', code: 'yeni_kod', params: [], direction: 'up', contribution: 0.1, text: 'Sunucu metni (+)' }))
      .toBe('Sunucu metni (+)')
  })

  it('güncellik durumu', () => {
    const base = { validity_days: 180, expires_on: '2026-01-01', age_days: 1, data_range: null }
    expect(freshnessState({ ...base, stale: true })).toBe('stale')
    expect(freshnessState({ ...base, stale: false, days_left: 10 })).toBe('expiring')
    expect(freshnessState({ ...base, stale: false, days_left: 120 })).toBe('fresh')
    expect(freshnessState({ ...base, stale: null })).toBe('unknown')
    expect(freshnessState(undefined)).toBe('unknown')
  })
})
