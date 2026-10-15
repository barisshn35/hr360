import { describe, expect, it } from 'vitest'
import { passwordScore, policyText, qrPath, remainingText } from './passwordHelpers'

describe('parola linki yardımcıları', () => {
  it('kalan süre metni', () => {
    expect(remainingText(0)).toBe('Süresi doldu')
    expect(remainingText(-5)).toBe('Süresi doldu')
    expect(remainingText(30_000)).toBe('1 dk')
    expect(remainingText(45 * 60_000)).toBe('45 dk')
    expect(remainingText((4 * 60 + 10) * 60_000)).toBe('4 sa 10 dk')
    expect(remainingText((2 * 1440 + 3 * 60 + 59) * 60_000)).toBe('2 gün 3 sa')
  })

  it('parola gücü 0-4 aralığında, çeşitlilik ve uzunlukla artar', () => {
    expect(passwordScore('')).toBe(0)
    expect(passwordScore('a')).toBe(0)
    expect(passwordScore('abcdefgh')).toBeLessThan(passwordScore('Abcdefgh12!x'))
    expect(passwordScore('Çok-Uzun-Parola-2026!')).toBe(4)
  })

  it('QR yolu: kare matris, yalnızca modül kareleri', () => {
    const { path, size } = qrPath('https://hr.example.com/parola-belirle/' + 'a'.repeat(43))
    expect(size).toBeGreaterThan(20)
    expect(path).toMatch(/^(M\d+ \d+h1v1h-1z)+$/)
    expect(qrPath('x').path).not.toBe(path)
  })

  it('Keycloak politika kodları anlaşılır metne çevrilir', () => {
    expect(policyText('invalidPasswordMinLengthMessage')).toBe('Parola çok kısa. Daha uzun bir parola seçin.')
    expect(policyText(undefined)).toBe('Parola, parola kurallarına uymuyor.')
  })
})
