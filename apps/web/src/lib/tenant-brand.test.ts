import { describe, expect, it } from 'vitest'
import { brandPalette } from './tenant-brand'

const parts = (v: string) => v.split(' ').map((x) => parseFloat(x))

describe('brandPalette', () => {
  it('koyu kırmızıyı koyu temada okunur hâle getirir (açıklık artar, ton korunur)', () => {
    const [h, , l] = parts(brandPalette('#8e0b0b')['--tenant-primary-dark'])
    expect(h).toBe(0)
    expect(l).toBeGreaterThan(55)
  })
  it('açık sarıyı açık temada koyulaştırır, düğme metnini kontrasta göre seçer', () => {
    const p = brandPalette('#ffe14d')
    expect(parts(p['--tenant-primary'])[2]).toBeLessThan(45)
    expect(p['--tenant-primary-dark-foreground']).toBe('160 50% 4%')
  })
  it('zaten okunur bir rengi yalnızca hafifçe ayarlar (ton ve doygunluk aynı)', () => {
    const [h, s, l] = parts(brandPalette('#0b8f63')['--tenant-primary'])
    expect([h, s]).toEqual([160, 86])
    expect(l).toBeGreaterThanOrEqual(25)
    expect(l).toBeLessThanOrEqual(30)
  })
})
