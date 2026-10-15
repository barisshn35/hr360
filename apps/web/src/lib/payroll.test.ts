import { describe, expect, it } from 'vitest'
import { PARAMS_2026, grossToNetYear, netToGrossYear, taxOn } from './payroll'

describe('gelir vergisi dilimleri (2026)', () => {
  it('dilim sınırlarında toplam vergi', () => {
    expect(taxOn(190_000, PARAMS_2026.brackets)).toBeCloseTo(28_500, 2)
    expect(taxOn(400_000, PARAMS_2026.brackets)).toBeCloseTo(28_500 + 42_000, 2)
    expect(taxOn(1_500_000, PARAMS_2026.brackets)).toBeCloseTo(70_500 + 297_000, 2)
    expect(taxOn(0, PARAMS_2026.brackets)).toBe(0)
  })
})

describe('asgari ücret istisnası', () => {
  const rows = grossToNetYear(PARAMS_2026.minWageGross)

  it('asgari ücretli her ay aynı neti alır (GV ve damga vergisi sıfır)', () => {
    for (const r of rows) {
      expect(r.net).toBeCloseTo(28_075.5, 2)
      expect(r.incomeTaxPayable).toBe(0)
      expect(r.stampPayable).toBe(0)
    }
  })

  it('aylık istisna tutarları resmî tutarlarla aynı', () => {
    expect(rows[0].incomeTaxExemption).toBeCloseTo(4_211.33, 2)   // Ocak–Haziran
    expect(rows[5].incomeTaxExemption).toBeCloseTo(4_211.33, 2)
    expect(rows[6].incomeTaxExemption).toBeCloseTo(4_537.75, 2)   // Temmuz (dilim geçişi)
    expect(rows[7].incomeTaxExemption).toBeCloseTo(5_615.1, 2)    // Ağustos–Aralık
    expect(rows[11].incomeTaxExemption).toBeCloseTo(5_615.1, 2)
  })

  it('işveren maliyeti 5 puanlık teşvikle', () => {
    expect(rows[0].employerCost).toBeCloseTo(33_030 * (1 + 0.2175 - 0.05 + 0.02), 1)
    expect(grossToNetYear(33_030, PARAMS_2026, false)[0].employerCost).toBeCloseTo(33_030 * (1 + 0.2175 + 0.02), 1)
  })
})

describe('brütten nete', () => {
  it('75.000 TL brüt — Ocak bordrosu (elle hesaplanan değer)', () => {
    const jan = grossToNetYear(75_000)[0]
    expect(jan.sgk).toBe(10_500)
    expect(jan.unemployment).toBe(750)
    expect(jan.taxBase).toBe(63_750)
    expect(jan.incomeTax).toBeCloseTo(9_562.5, 2)
    expect(jan.incomeTaxPayable).toBeCloseTo(5_351.18, 1)
    expect(jan.stampPayable).toBeCloseTo(318.55, 2)
    expect(jan.net).toBeCloseTo(58_080.27, 1)
  })

  it('SGK tavanı üstünde prim tavandan kesilir', () => {
    const r = grossToNetYear(400_000)[0]
    expect(r.sgk).toBeCloseTo(297_270 * 0.14, 2)
    expect(r.unemployment).toBeCloseTo(297_270 * 0.01, 2)
  })

  it('kümülatif matrah arttıkça dilim yükselir, net düşer', () => {
    const rows = grossToNetYear(150_000)
    expect(rows[11].net).toBeLessThan(rows[0].net)
    expect(rows[11].bracketRate).toBeGreaterThan(rows[0].bracketRate)
    expect(rows[11].cumulativeBase).toBeCloseTo(150_000 * 0.85 * 12, 0)
  })
})

describe('netten brüte', () => {
  it('her ay hedef neti verir (±5 kuruş)', () => {
    for (const r of netToGrossYear(50_000)) expect(Math.abs(r.net - 50_000)).toBeLessThan(0.05)
  })

  it('brütten nete ile tutarlı', () => {
    const g = netToGrossYear(40_000)[0].gross
    expect(grossToNetYear(g)[0].net).toBeCloseTo(40_000, 1)
  })
})
