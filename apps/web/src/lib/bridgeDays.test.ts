import { describe, expect, it } from 'vitest'
import { bridgeSuggestions } from './bridgeDays'

describe('bridgeSuggestions', () => {
  // 2026: 29 Ekim Perşembe (tam), 28 Ekim Çarşamba arife (yarım gün)
  const h = [{ date: '2026-10-28', isHalfDay: true }, { date: '2026-10-29' }]

  it('Perşembe tatilini Cuma izniyle hafta sonuna bağlar; çakışan arife önerisi elenir', () => {
    const s = bridgeSuggestions(h, '2026-10-01', { maxLeave: 1.5 })
    expect(s).toHaveLength(1)
    expect(s[0]).toMatchObject({ start: '2026-10-29', end: '2026-11-01', totalDays: 4, leaveDays: 1 })
    expect(s[0].leaveDates).toEqual(['2026-10-30'])
  })

  it('izin üst sınırı daha genişse daha uzun aralığı da değerlendirir ve en iyi oranı seçer', () => {
    const s = bridgeSuggestions(h, '2026-10-01', { maxLeave: 4 })
    expect(s[0].leaveDays).toBeLessThanOrEqual(4)
    expect(s[0].totalDays / s[0].leaveDays).toBeGreaterThanOrEqual(4 - 1e-9)
  })

  it('geçmiş tatilleri ve hafta sonuna denk gelen tatilleri önermez', () => {
    expect(bridgeSuggestions(h, '2026-11-01')).toHaveLength(0)
    expect(bridgeSuggestions([{ date: '2026-08-30' }], '2026-08-01')).toHaveLength(0) // Pazar
  })
})
