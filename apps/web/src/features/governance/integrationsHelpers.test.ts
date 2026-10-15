import { describe, expect, it } from 'vitest'
import { formatBackoff, retryStateView, simplifyScopes } from './integrationsHelpers'

describe('webhook geri çekilme ve API yetkileri', () => {
  it('süreyi kısa metne çevirir', () => {
    expect(formatBackoff(30)).toBe('30 sn')
    expect(formatBackoff(60)).toBe('1 dk')
    expect(formatBackoff(90)).toBe('1 dk 30 sn')
    expect(formatBackoff(7200)).toBe('2 sa')
    expect(formatBackoff(43200 + 600)).toBe('12 sa 10 dk')
  })
  it('read-only seçiliyken tekil okuma yetkileri düşer, yazma kalır', () => {
    expect(simplifyScopes(['read-only', 'leaves:read', 'hooks:write', 'hooks:write'])).toEqual(['read-only', 'hooks:write'])
    expect(simplifyScopes(['leaves:read', 'employees:read'])).toEqual(['leaves:read', 'employees:read'])
  })
  it('deneme durumlarını rozete çevirir', () => {
    expect(retryStateView('pending')?.tone).toBe('warning')
    expect(retryStateView('gave_up')?.tone).toBe('danger')
    expect(retryStateView(null)).toBeNull()
  })
})
