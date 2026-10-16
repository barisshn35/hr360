import { describe, expect, it } from 'vitest'
import { newerThan, semver } from './platformOps'

describe('platformOps sürüm karşılaştırma', () => {
  it('etiketi sayılara çevirir', () => expect(semver('v1.10.2')).toEqual([1, 10, 2]))
  it('yeni sürümü bulur (sayısal, metinsel değil)', () => {
    expect(newerThan('v1.10.0', '1.9.9')).toBe(true)
    expect(newerThan('v1.1.0', '1.1.0')).toBe(false)
    expect(newerThan('v1.0.9', '1.1.0')).toBe(false)
    expect(newerThan('v2.0.0', null)).toBe(true)
  })
})
