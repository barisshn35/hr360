import { describe, expect, it } from 'vitest'
import { secondsLeft } from './KioskPage'

describe('kiosk QR geri sayımı', () => {
  it('kalan saniye yukarı yuvarlanır, negatif olmaz', () => {
    const now = Date.parse('2026-10-12T08:00:00Z')
    expect(secondsLeft('2026-10-12T08:00:29.200Z', now)).toBe(30)
    expect(secondsLeft('2026-10-12T07:59:00Z', now)).toBe(0)
    expect(secondsLeft('geçersiz', now)).toBe(0)
  })
})
