import { describe, expect, it } from 'vitest'
import { INSTALL_SNOOZE_DAYS, isIos, shouldOfferInstall } from './pwa'

describe('PWA yükleme önerisi (dalga 12)', () => {
  const now = Date.parse('2026-10-07T10:00:00Z')
  it('kurulu uygulamada gösterilmez', () => {
    expect(shouldOfferInstall({ hasPrompt: true, ios: false, standalone: true, dismissedAt: null, now })).toBeNull()
  })
  it('tarayıcı öneri sunuyorsa düğme, iOS Safari ise yönerge', () => {
    expect(shouldOfferInstall({ hasPrompt: true, ios: false, standalone: false, dismissedAt: null, now })).toBe('prompt')
    expect(shouldOfferInstall({ hasPrompt: false, ios: true, standalone: false, dismissedAt: null, now })).toBe('ios')
    expect(shouldOfferInstall({ hasPrompt: false, ios: false, standalone: false, dismissedAt: null, now })).toBeNull()
  })
  it('"Daha sonra" 30 gün susturur', () => {
    const day = 86_400_000
    expect(shouldOfferInstall({ hasPrompt: true, ios: false, standalone: false, dismissedAt: now - 5 * day, now })).toBeNull()
    expect(shouldOfferInstall({ hasPrompt: true, ios: false, standalone: false, dismissedAt: now - (INSTALL_SNOOZE_DAYS + 1) * day, now })).toBe('prompt')
  })
  it('iPhone ve iPadOS tanınır', () => {
    expect(isIos('Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)', 5)).toBe(true)
    expect(isIos('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', 5)).toBe(true)
    expect(isIos('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', 0)).toBe(false)
    expect(isIos('Mozilla/5.0 (Linux; Android 14)', 5)).toBe(false)
  })
})
