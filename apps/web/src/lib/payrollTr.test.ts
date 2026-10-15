import { describe, expect, it } from 'vitest'
import { formatCostCenters, ibanMod97, parseCostCenters, parseRate, retroKey, retroTotal, shortHash, validOccupationCode, validTrIban } from './payrollTr'
import type { RetroCandidate } from '@/api/payrollTr'

describe('IBAN (mod-97)', () => {
  it('geçerli TR IBAN, boşluklu yazım', () => {
    expect(validTrIban('TR330006100519786457841326')).toBe(true)
    expect(validTrIban('tr33 0006 1005 1978 6457 8413 26')).toBe(true)
  })
  it('kontrol hanesi, uzunluk ve ülke', () => {
    expect(validTrIban('TR330006100519786457841327')).toBe(false)
    expect(validTrIban('TR3300061005197864578413')).toBe(false)
    expect(validTrIban('DE89370400440532013000')).toBe(false)
    expect(validTrIban('')).toBe(false)
    expect(validTrIban(null)).toBe(false)
  })
  it('yabancı IBAN kalanı da 1 (sunucuyla aynı algoritma)', () => {
    expect(ibanMod97('DE89370400440532013000')).toBe(1)
    expect(ibanMod97('GB82WEST12345698765432')).toBe(1)
  })
})

describe('SGK meslek kodu', () => {
  it('0000.00 biçimi', () => {
    expect(validOccupationCode('2512.01')).toBe(true)
    expect(validOccupationCode(' 1120.03 ')).toBe(true)
    expect(validOccupationCode('2512')).toBe(false)
    expect(validOccupationCode('25120.1')).toBe(false)
  })
})

describe('masraf merkezi eşlemesi', () => {
  it('satırlardan eşleme ve geri', () => {
    const m = parseCostCenters('Mühendislik = MM-100\n\nSatış=MM-200\nbozuk satır\n= boş\nİK = ')
    expect(m).toEqual({ Mühendislik: 'MM-100', Satış: 'MM-200' })
    expect(formatCostCenters(m)).toBe('Mühendislik = MM-100\nSatış = MM-200')
  })
})

describe('fark bordrosu toplamı', () => {
  const row = (emp: string, diff: number, applicable = diff > 0): RetroCandidate => ({
    employeeId: emp, sourcePeriodId: 'p1', year: 2026, month: 3, oldBase: 1, newBase: 2, oldGross: 1, newGross: 2, alreadyPaid: 0,
    diffGross: diff, estimatedNetDiff: diff * 0.7, label: 'Fark: 2026/03', applicable,
  })
  it('yalnızca seçili ve uygulanabilir satırlar', () => {
    const rows = [row('a', 1000), row('b', 500), row('c', -200)]
    const sel = new Set(rows.map(retroKey))
    expect(retroTotal(rows, sel)).toBe(1500)
    sel.delete(retroKey(rows[0]!))
    expect(retroTotal(rows, sel)).toBe(500)
  })
})

describe('yardımcılar', () => {
  it('özet kısaltma ve oran girişi', () => {
    expect(shortHash('0123456789abcdef0123')).toBe('0123 4567 89ab cdef')
    expect(shortHash(null)).toBe('—')
    expect(parseRate('%14')).toBeCloseTo(0.14)
    expect(parseRate('14')).toBeCloseTo(0.14)
    expect(parseRate('0,00759')).toBeCloseTo(0.00759)
    expect(parseRate('x')).toBeNull()
    expect(parseRate('')).toBeNull()
  })
})
