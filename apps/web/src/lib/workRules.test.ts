import { describe, expect, it } from 'vitest'
import { DEFAULT_LIMITS, limitsFrom, nightMinutes, patternWarnings, span } from './workRules'

const D = (s: string, e: string) => ({ type: 'Day', startTime: s, endTime: e })
const N = (s: string, e: string) => ({ type: 'Night', startTime: s, endTime: e })
const OFF = { type: 'Off', startTime: null, endTime: null }
const codes = (w: ReturnType<typeof patternWarnings>) => w.map((x) => x.code).sort()

describe('vardiya deseni kuralları', () => {
  it('uygun desen: 2 gündüz 2 gece 4 tatil (12 saat değil, 7,5)', () => {
    expect(patternWarnings([D('08:00', '15:30'), D('08:00', '15:30'), N('22:00', '05:30'), N('22:00', '05:30'), OFF, OFF, OFF, OFF])).toEqual([])
  })
  it('geceden sabaha geçiş 11 saat dinlenmeyi ihlal eder', () => {
    const w = patternWarnings([N('22:00', '06:00'), D('08:00', '16:00'), OFF, OFF])
    expect(w.some((x) => x.code === 'rest' && x.day === 1)).toBe(true)
  })
  it('12 saatlik gece: günlük ve gece sınırı', () => {
    expect(codes(patternWarnings([N('20:00', '08:00'), OFF, OFF, OFF]))).toEqual(['daily', 'night'])
  })
  it('dinlenme günü olmayan desen ve 7 günlük dilim', () => {
    const w = patternWarnings([D('08:00', '17:00')])
    expect(codes(w)).toContain('consecutive')
    expect(codes(w)).toContain('weekly')
  })
  it('döngü sonundan başına ardışık gün sayılır', () => {
    const days = [D('09:00', '13:00'), D('09:00', '13:00'), D('09:00', '13:00'), D('09:00', '13:00'), OFF, D('09:00', '13:00'), D('09:00', '13:00'), D('09:00', '13:00')]
    expect(codes(patternWarnings(days))).toEqual(['consecutive']) // 3 (son) + 4 (baş) = 7 gün
    expect(patternWarnings(days, limitsFrom({ maxConsecutiveDays: 7 }))).toEqual([])
  })
  it('yardımcılar', () => {
    expect(span('22:00', '06:00')).toEqual({ start: 1320, end: 1800 })
    expect(nightMinutes(1320, 1800)).toBe(480)
    expect(nightMinutes(840, 1380)).toBe(180)
    expect(limitsFrom(null)).toEqual(DEFAULT_LIMITS)
  })
})
