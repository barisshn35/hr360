import { describe, expect, it } from 'vitest'
import { dueBadge } from './learningW11'

describe('dueBadge (dalga 11, madde 84)', () => {
  it('gecikme ve son gün kırmızı', () => {
    expect(dueBadge(-3).tone).toBe('danger')
    expect(dueBadge(-3).label).toContain('3')
    expect(dueBadge(0).tone).toBe('danger')
  })
  it('7 gün ve altı turuncu, sonrası bilgi', () => {
    expect(dueBadge(7).tone).toBe('warning')
    expect(dueBadge(8).tone).toBe('info')
  })
})
