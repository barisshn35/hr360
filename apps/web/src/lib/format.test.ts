import { describe, expect, it } from 'vitest'
import { parseDecimal } from './format'

describe('parseDecimal', () => {
  it('Türkçe ve noktalı ondalık biçimleri okur', () => {
    expect(parseDecimal('58.080,27')).toBe(58080.27)
    expect(parseDecimal('58080,27')).toBe(58080.27)
    expect(parseDecimal('58080.27')).toBe(58080.27)
    expect(parseDecimal('58,080.27')).toBe(58080.27)
    expect(parseDecimal('58.080')).toBe(58080)
    expect(parseDecimal('1.000.000')).toBe(1000000)
    expect(parseDecimal('41.0082')).toBe(41.0082)
    expect(parseDecimal('41,0082')).toBe(41.0082)
    expect(parseDecimal('75000')).toBe(75000)
    expect(parseDecimal('-12,5')).toBe(-12.5)
  })
  it('sayı olmayanı null döndürür', () => {
    expect(parseDecimal('')).toBeNull()
    expect(parseDecimal('abc')).toBeNull()
    expect(parseDecimal('12a')).toBeNull()
    expect(parseDecimal('.')).toBeNull()
  })
})
