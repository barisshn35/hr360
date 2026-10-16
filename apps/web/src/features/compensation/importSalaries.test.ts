import { describe, expect, it } from 'vitest'
import { parseAmount, parseDate } from './ImportSalariesModal'

describe('toplu ücret ayrıştırma', () => {
  it('Türkçe ve düz sayı biçimleri', () => {
    expect(parseAmount('65.000')).toBe(65000)
    expect(parseAmount('65.000,50')).toBe(65000.5)
    expect(parseAmount('65000.5')).toBe(65000.5)
    expect(parseAmount('45 000 ₺')).toBe(45000)
    expect(parseAmount('abc')).toBeNull()
  })
  it('tarih biçimleri ve geçersiz gün', () => {
    expect(parseDate('01.01.2027')).toBe('2027-01-01')
    expect(parseDate('2027-01-01')).toBe('2027-01-01')
    expect(parseDate('31.02.2027')).toBeNull()
  })
})
