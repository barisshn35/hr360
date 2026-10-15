import { tx } from '@/lib/i18n'
/**
 * Türkiye bordro simülasyonu (2026 parametreleri).
 *
 * Kaynak: 2026 asgari ücret (brüt 33.030 TL), SGK işçi %14 + işsizlik %1,
 * işveren %21,75 (5 puan teşvikle %16,75) + işsizlik %2, SGK tavanı 297.270 TL,
 * GVK m.103 ücret tarifesi (190.000 / 400.000 / 1.500.000 / 5.300.000 TL;
 * %15/20/27/35/40), damga vergisi binde 7,59. Asgari ücrete isabet eden gelir
 * ve damga vergisi istisnası (GVK m.23/18, DVK) aya göre kümülatif hesaplanır.
 *
 * Bu bir SİMÜLASYONDUR: AGİ kalktı; engellilik indirimi, BES, sendika aidatı,
 * yan haklar ve özel durumlar dahil değildir. Parametreler arayüzden değiştirilebilir.
 */

export interface PayrollParams {
  minWageGross: number
  sgkEmployee: number
  unemploymentEmployee: number
  sgkEmployer: number
  employerDiscount: number
  unemploymentEmployer: number
  sgkCeiling: number
  brackets: Array<[number, number]>
  stampRate: number
}

export const PARAMS_2026: PayrollParams = {
  minWageGross: 33_030,
  sgkEmployee: 0.14,
  unemploymentEmployee: 0.01,
  sgkEmployer: 0.2175,
  employerDiscount: 0.05,
  unemploymentEmployer: 0.02,
  sgkCeiling: 297_270,
  brackets: [[190_000, 0.15], [400_000, 0.2], [1_500_000, 0.27], [5_300_000, 0.35], [Infinity, 0.4]],
  stampRate: 0.00759,
}

export const MONTHS_TR = [tx('Ocak'), tx('Şubat'), tx('Mart'), tx('Nisan'), tx('Mayıs'), tx('Haziran'), tx('Temmuz'), tx('Ağustos'), tx('Eylül'), tx('Ekim'), tx('Kasım'), tx('Aralık')]

const r2 = (n: number) => Math.round(n * 100) / 100

/** Kümülatif matraha göre toplam vergi. */
export function taxOn(cumulative: number, brackets: PayrollParams['brackets']) {
  let tax = 0
  let lower = 0
  for (const [upper, rate] of brackets) {
    if (cumulative <= lower) break
    tax += (Math.min(cumulative, upper) - lower) * rate
    lower = upper
  }
  return tax
}

export interface PayrollRow {
  month: number
  gross: number
  sgk: number
  unemployment: number
  taxBase: number
  cumulativeBase: number
  incomeTax: number
  incomeTaxExemption: number
  incomeTaxPayable: number
  stampTax: number
  stampExemption: number
  stampPayable: number
  net: number
  employerSgk: number
  employerUnemployment: number
  employerCost: number
  bracketRate: number
}

function monthRow(month: number, gross: number, prevCumBase: number, p: PayrollParams, discount: boolean): PayrollRow {
  const sgkBase = Math.min(gross, p.sgkCeiling)
  const sgk = sgkBase * p.sgkEmployee
  const unemployment = sgkBase * p.unemploymentEmployee
  const taxBase = gross - sgk - unemployment
  const cum = prevCumBase + taxBase
  const incomeTax = taxOn(cum, p.brackets) - taxOn(prevCumBase, p.brackets)
  const mwBase = p.minWageGross * (1 - p.sgkEmployee - p.unemploymentEmployee)
  const incomeTaxExemption = taxOn(mwBase * month, p.brackets) - taxOn(mwBase * (month - 1), p.brackets)
  const incomeTaxPayable = Math.max(0, incomeTax - incomeTaxExemption)
  const stampTax = gross * p.stampRate
  const stampExemption = p.minWageGross * p.stampRate
  const stampPayable = Math.max(0, stampTax - stampExemption)
  const net = gross - sgk - unemployment - incomeTaxPayable - stampPayable
  const employerSgk = sgkBase * (p.sgkEmployer - (discount ? p.employerDiscount : 0))
  const employerUnemployment = sgkBase * p.unemploymentEmployer
  const bracketRate = p.brackets.find(([upper]) => cum <= upper)?.[1] ?? 0.4
  return {
    month, gross: r2(gross), sgk: r2(sgk), unemployment: r2(unemployment), taxBase: r2(taxBase), cumulativeBase: r2(cum),
    incomeTax: r2(incomeTax), incomeTaxExemption: r2(incomeTaxExemption), incomeTaxPayable: r2(incomeTaxPayable),
    stampTax: r2(stampTax), stampExemption: r2(stampExemption), stampPayable: r2(stampPayable), net: r2(net),
    employerSgk: r2(employerSgk), employerUnemployment: r2(employerUnemployment), employerCost: r2(gross + employerSgk + employerUnemployment), bracketRate,
  }
}

/** Yıl boyunca her ay aynı brütle (brütten nete). */
export function grossToNetYear(gross: number, p: PayrollParams = PARAMS_2026, discount = true): PayrollRow[] {
  const rows: PayrollRow[] = []
  let cum = 0
  for (let m = 1; m <= 12; m++) {
    const row = monthRow(m, gross, cum, p, discount)
    rows.push(row)
    cum = row.cumulativeBase
  }
  return rows
}

/** Her ay aynı neti verecek brüt (netten brüte); dilim yükseldikçe brüt artar. */
export function netToGrossYear(net: number, p: PayrollParams = PARAMS_2026, discount = true): PayrollRow[] {
  const rows: PayrollRow[] = []
  let cum = 0
  for (let m = 1; m <= 12; m++) {
    let lo = net
    let hi = net * 2.5 + 10_000
    for (let i = 0; i < 60; i++) {
      const mid = (lo + hi) / 2
      if (monthRow(m, mid, cum, p, discount).net < net) lo = mid
      else hi = mid
    }
    const row = monthRow(m, hi, cum, p, discount)
    rows.push(row)
    cum = row.cumulativeBase
  }
  return rows
}
