import { describe, expect, it } from 'vitest'
import { holidayMap, hoursProblem, leaveDays, workingDays } from './leaveDays'

const hol = holidayMap([
  { date: '2026-05-26', isHalfDay: true },   // Kurban Bayramı arifesi
  { date: '2026-05-27' }, { date: '2026-05-28' }, { date: '2026-05-29' },
  { date: '2026-10-28', isHalfDay: true }, { date: '2026-10-29' },
])

describe('izin gün hesabı', () => {
  it('hafta sonu ve tatil düşülür, arife yarım gün', () => {
    expect(workingDays('2026-05-25', '2026-05-31', hol)).toBe(1.5)
    expect(workingDays('2026-10-26', '2026-10-30', hol)).toBe(3.5)
    expect(workingDays('2026-10-05', '2026-10-09', hol)).toBe(5)
    expect(workingDays('2026-10-09', '2026-10-05', hol)).toBe(0)
  })
  it('aynı güne tam ve yarım tatil: tam gün baskın', () => {
    const m = holidayMap([{ date: '2029-04-23', isHalfDay: true }, { date: '2029-04-23T00:00:00', isHalfDay: false }])
    expect(m.get('2029-04-23')).toBe(false)
  })
  it('yarım gün ve saatlik izin', () => {
    expect(leaveDays('half', '2026-10-06', '2026-10-06', hol, 0, 7.5)).toBe(0.5)
    expect(leaveDays('hours', '2026-10-06', '2026-10-06', hol, 2, 7.5)).toBe(0.27)
    expect(leaveDays('hours', '2026-10-06', '2026-10-06', hol, 4, 8)).toBe(0.5)
    // birden çok gün seçilince birim yok sayılır
    expect(leaveDays('half', '2026-10-05', '2026-10-06', hol, 0, 7.5)).toBe(2)
    // arife gününde yarım gün izin en çok 0,5
    expect(leaveDays('half', '2026-10-28', '2026-10-28', hol, 0, 7.5)).toBe(0.5)
  })
  it('saat doğrulaması', () => {
    expect(hoursProblem(2.5, 7.5)).toBeNull()
    expect(hoursProblem(7.5, 7.5)).toBeNull()        // günün tamamı saatlik girilebilir (1 gün)
    expect(hoursProblem(8, 7.5)).toBe('range')
    expect(hoursProblem(1.25, 7.5)).toBe('step')
    expect(hoursProblem(null, 7.5)).toBe('invalid')
    expect(hoursProblem(4, 7.5, 0.5)).toBe('range')
  })
})
