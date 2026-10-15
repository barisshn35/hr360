import { describe, expect, it } from 'vitest'
import {
  addBracket, emptyForm, formFromRow, parseAmount, parsePercent, prefillSource, removeBracket, rowStatus, serverErrors, sgkFloorCeiling,
  validateForm, type ParamForm,
} from './payrollParams'
import type { PayrollParameterRow } from '@/api/payroll'

/** Test değerleri ÖRNEKTİR (resmî değer değildir); yalnızca doğrulama kurallarını sınar. */
function filled(): ParamForm {
  return {
    ...emptyForm(),
    minimumWageGross: '20.000,00', minimumWageNet: '17000', sgkEmployeePct: '14', unemploymentEmployeePct: '1',
    sgkEmployerPct: '20,5', incentivePoints: '2', unemploymentEmployerPct: '2', stampPct: '0,759', ceilingMultiplier: '7,5',
    brackets: [{ upTo: '100.000', ratePct: '15' }, { upTo: '250000', ratePct: '20' }, { upTo: '', ratePct: '40' }],
    severanceH1: '40000', severanceH2: '',
  }
}

const row = (over: Partial<PayrollParameterRow> = {}): PayrollParameterRow => ({
  id: 'r1', year: 2030, validFromMonth: 1, minimumWageGross: 20000, minimumWageNet: 17000, sgkEmployeeRate: 0.14, unemploymentEmployeeRate: 0.01,
  sgkEmployerRate: 0.205, employerIncentivePoints: 2, unemploymentEmployerRate: 0.02, stampTaxRate: 0.00759, sgkCeilingMultiplier: 7.5,
  brackets: [{ upTo: 100000, rate: 0.15 }, { upTo: null, rate: 0.4 }], minimumWageExemption: true, agiMonthly: null,
  severanceCeilingH1: 40000, severanceCeilingH2: null, verified: true, seed: false, source: 'RG', updatedBy: 'admin', updatedAt: '2030-01-02T10:00:00Z',
  ...over,
})

describe('sayı ayrıştırma', () => {
  it('tutar: virgüllü ondalık ve binlik ayırıcı', () => {
    expect(parseAmount('33.030,00')).toBe(33030)
    expect(parseAmount('33030,5')).toBe(33030.5)
    expect(parseAmount('  ')).toBeNull()
    expect(Number.isNaN(parseAmount('abc') as number)).toBe(true)
  })
  it('yüzde: tek ayırıcı her zaman ondalık, % işareti kabul', () => {
    expect(parsePercent('0,759')).toBeCloseTo(0.759)
    expect(parsePercent('0.759')).toBeCloseTo(0.759)
    expect(parsePercent('%14')).toBe(14)
    expect(parsePercent('20,75')).toBeCloseTo(20.75)
    expect(parsePercent('')).toBeNull()
    expect(Number.isNaN(parsePercent('1.000,5') as number)).toBe(true)
  })
})

describe('form doğrulama', () => {
  it('geçerli form API girdisine çevrilir (yüzde → kesir)', () => {
    const { errors, input } = validateForm(filled(), 1)
    expect(errors).toEqual({})
    expect(input).not.toBeNull()
    expect(input!.minimumWageGross).toBe(20000)
    expect(input!.sgkEmployeeRate).toBeCloseTo(0.14)
    expect(input!.sgkEmployerRate).toBeCloseTo(0.205)
    expect(input!.stampTaxRate).toBeCloseTo(0.00759)
    expect(input!.brackets).toEqual([{ upTo: 100000, rate: 0.15 }, { upTo: 250000, rate: 0.2 }, { upTo: null, rate: 0.4 }])
    expect(input!.severanceCeilingH1).toBe(40000)
    expect(input!.severanceCeilingH2).toBeNull()
  })
  it('boş form: zorunlu alanlar işaretlenir, varsayılan doldurulmaz', () => {
    const { errors, input } = validateForm(emptyForm(), 1)
    expect(input).toBeNull()
    for (const k of ['minimumWageGross', 'sgkEmployeeRate', 'unemploymentEmployeeRate', 'sgkEmployerRate', 'unemploymentEmployerRate',
      'stampTaxRate', 'employerIncentivePoints', 'sgkCeilingMultiplier', 'brackets.0.upTo', 'brackets.0.rate', 'brackets.1.rate']) {
      expect(errors[k], k).toBeTruthy()
    }
    // Son dilimin üst sınırı yoktur; isteğe bağlı alanlar zorunlu değildir.
    expect(errors['brackets.1.upTo']).toBeUndefined()
    expect(errors.severanceCeilingH1).toBeUndefined()
    expect(errors.minimumWageNet).toBeUndefined()
  })
  it('yüzde aralıkları', () => {
    expect(validateForm({ ...filled(), sgkEmployeePct: '60' }, 1).errors.sgkEmployeeRate).toBeTruthy()
    expect(validateForm({ ...filled(), stampPct: '-1' }, 1).errors.stampTaxRate).toBeTruthy()
    expect(validateForm({ ...filled(), unemploymentEmployerPct: '11' }, 1).errors.unemploymentEmployerRate).toBeTruthy()
    expect(validateForm({ ...filled(), incentivePoints: '25' }, 1).errors.employerIncentivePoints).toBeTruthy()
    expect(validateForm({ ...filled(), sgkEmployerPct: '1', incentivePoints: '2' }, 1).errors.employerIncentivePoints).toBeTruthy()
  })
  it('dilimler artan sınırlı ve azalmayan oranlı olmalı', () => {
    const desc = { ...filled(), brackets: [{ upTo: '200000', ratePct: '15' }, { upTo: '100000', ratePct: '20' }, { upTo: '', ratePct: '40' }] }
    expect(validateForm(desc, 1).errors['brackets.1.upTo']).toBeTruthy()
    const rateDown = { ...filled(), brackets: [{ upTo: '100000', ratePct: '20' }, { upTo: '', ratePct: '15' }] }
    expect(validateForm(rateDown, 1).errors['brackets.1.rate']).toBeTruthy()
  })
  it('tavan tabandan düşük olamaz; net brütten büyük olamaz', () => {
    expect(validateForm({ ...filled(), ceilingMultiplier: '0,5' }, 1).errors.sgkCeilingMultiplier).toBeTruthy()
    expect(validateForm({ ...filled(), minimumWageNet: '25000' }, 1).errors.minimumWageNet).toBeTruthy()
    expect(validateForm({ ...filled(), severanceH1: '0' }, 1).errors.severanceCeilingH1).toBeTruthy()
  })
  it('yürürlük ayı 1–12', () => {
    expect(validateForm(filled(), 13).errors.validFromMonth).toBeTruthy()
  })
})

describe('yardımcılar', () => {
  it('SGK taban/tavan önizlemesi', () => {
    expect(sgkFloorCeiling(filled())).toEqual({ floor: 20000, ceiling: 150000 })
    expect(sgkFloorCeiling(emptyForm())).toBeNull()
  })
  it('dilim ekle/sil: yeni dilim "üzeri"nin önüne', () => {
    const list = [{ upTo: '1', ratePct: '1' }, { upTo: '', ratePct: '40' }]
    const added = addBracket(list)
    expect(added).toHaveLength(3)
    expect(added[2]).toEqual({ upTo: '', ratePct: '40' })
    expect(removeBracket([{ upTo: '', ratePct: '' }], 0)).toHaveLength(1)
  })
  it('satırdan form (önceki yıldan doldur): kaynak notu taşınmaz', () => {
    const f = formFromRow(row(), { locale: 'tr-TR' })
    expect(f.sgkEmployeePct).toBe('14')
    expect(f.stampPct).toBe('0,759')
    expect(f.source).toBe('')
    expect(f.brackets[1].upTo).toBe('')
    expect(validateForm(f, 1).errors).toEqual({})
    expect(formFromRow(row(), { keepSource: true }).source).toBe('RG')
  })
  it('doldurma kaynağı: aynı yılın önceki satırı, yoksa önceki yıl', () => {
    const prev = row({ id: 'p', year: 2029, validFromMonth: 7 })
    const jan = row({ id: 'j', validFromMonth: 1 })
    expect(prefillSource([jan], prev, 7)?.id).toBe('j')
    expect(prefillSource([], prev, 1)?.id).toBe('p')
    expect(prefillSource([], null, 1)).toBeNull()
  })
  it('durum ve sunucu alan hataları', () => {
    expect(rowStatus(row())).toBe('entered')
    expect(rowStatus(row({ seed: true }))).toBe('seed')
    expect(serverErrors({ errors: [{ field: 'brackets', message: 'x' }, { field: 'brackets', message: 'y' }] })).toEqual({ brackets: 'x' })
    expect(serverErrors(null)).toEqual({})
  })
})
