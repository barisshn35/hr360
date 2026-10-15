import { describe, expect, it } from 'vitest'
import { laborWarnings } from './shared'

const day = (s: string, e: string) => ({ type: 'Day' as const, startTime: s, endTime: e })
const night = (s: string, e: string) => ({ type: 'Night' as const, startTime: s, endTime: e })
const off = { type: 'Off' as const, startTime: null, endTime: null }

describe('laborWarnings (İş Kanunu sınır uyarıları)', () => {
  it('5×8 ofis deseninde uyarı yok', () => {
    expect(laborWarnings([...Array(5).fill(day('09:00', '17:00')), off, off])).toEqual([])
  })

  it('7×12 desen: günlük 11 saat ve haftalık 45 saat aşılır', () => {
    const w = laborWarnings(Array(7).fill(day('09:00', '21:00')))
    expect(w.some((x) => x.includes('11 saati'))).toBe(true)
    expect(w.some((x) => x.includes('45 saati'))).toBe(true)
  })

  it('12 saatlik gece vardiyası gece çalışması sınırını aşar', () => {
    const w = laborWarnings([night('21:00', '09:00'), off, off])
    expect(w.some((x) => x.includes('7,5'))).toBe(true)
  })

  it('geceden hemen gündüze geçiş dinlenme uyarısı verir', () => {
    const w = laborWarnings([night('22:00', '06:00'), day('08:00', '16:00'), off, off, off])
    expect(w.some((x) => x.includes('dinlenme') || x.includes('çakışıyor'))).toBe(true)
  })
})
