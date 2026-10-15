import { describe, expect, it } from 'vitest'
import { minutesLeft, remainingLabel } from './PlatformAccessBanner'

describe('platform erişim bandı', () => {
  const now = Date.parse('2026-10-14T10:00:00Z')
  it('kalan dakika yukarı yuvarlanır, geçmişte 0', () => {
    expect(minutesLeft('2026-10-14T10:30:10Z', now)).toBe(31)
    expect(minutesLeft('2026-10-14T09:00:00Z', now)).toBe(0)
    expect(minutesLeft(null, now)).toBe(0)
  })
  it('saat ve dakika etiketi', () => {
    expect(remainingLabel(85)).toContain('1')
    expect(remainingLabel(85)).toContain('25')
    expect(remainingLabel(5)).toContain('5')
  })
})
