import { describe, expect, it } from 'vitest'
import { istanbulMinute, newerThan, pitrWindow, semver, validPitrTarget } from './platformOps'

describe('platformOps sürüm karşılaştırma', () => {
  it('etiketi sayılara çevirir', () => expect(semver('v1.10.2')).toEqual([1, 10, 2]))
  it('yeni sürümü bulur (sayısal, metinsel değil)', () => {
    expect(newerThan('v1.10.0', '1.9.9')).toBe(true)
    expect(newerThan('v1.1.0', '1.1.0')).toBe(false)
    expect(newerThan('v1.0.9', '1.1.0')).toBe(false)
    expect(newerThan('v2.0.0', null)).toBe(true)
  })
})

describe('anlık geri dönüş aralığı (İstanbul saati)', () => {
  const now = new Date('2026-10-19T12:00:30Z') // 15:00 İstanbul
  it('UTC anı İstanbul dakikasına çevirir', () => expect(istanbulMinute(new Date('2026-10-18T22:59:59Z'))).toBe('2026-10-19 01:59'))
  it('en eski temel yedeğin bitişinden sonraki dakikadan şimdiye', () => {
    expect(pitrWindow('2026-10-18T03:01:40Z', now)).toEqual({ min: '2026-10-18 06:02', max: '2026-10-19 15:00' })
    expect(pitrWindow('2026-10-18T03:01:00Z', now)?.min).toBe('2026-10-18 06:02')
    expect(pitrWindow(null, now)).toBeNull()
    expect(pitrWindow('2026-10-19T12:00:10Z', now)).toBeNull()
  })
  it('hedef biçim ve aralık denetimi', () => {
    const w = pitrWindow('2026-10-18T03:01:40Z', now)
    expect(validPitrTarget('2026-10-19 14:05', w)).toBe(true)
    expect(validPitrTarget('2026-10-19 15:00', w)).toBe(true)
    expect(validPitrTarget('2026-10-19 15:01', w)).toBe(false)
    expect(validPitrTarget('2026-10-18 06:01', w)).toBe(false)
    expect(validPitrTarget('2026-10-19 14:05; reboot', w)).toBe(false)
    expect(validPitrTarget('2026-10-19 ', w)).toBe(false)
    expect(validPitrTarget('2026-10-19 14:05', null)).toBe(false)
  })
})
