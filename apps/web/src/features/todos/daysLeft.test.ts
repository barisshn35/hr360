import { describe, expect, it } from 'vitest'
import { daysLeft } from './TodosPage'

describe('daysLeft', () => {
  const now = new Date(2026, 9, 16, 15, 0)
  it('bugün, yarın, geçmiş ve tarihsiz', () => {
    expect(daysLeft('2026-10-16', now)).toBe(0)
    expect(daysLeft('2026-10-17T08:00:00Z', now)).toBe(1)
    expect(daysLeft('2026-10-10', now)).toBe(-6)
    expect(daysLeft(null, now)).toBeNull()
  })
})
