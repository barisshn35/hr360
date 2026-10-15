import { afterEach, describe, expect, it } from 'vitest'
import { einvoiceToDraft, flagSummary, flagText, type EInvoice } from './expenseAudit'
import { setDictionary } from './i18n'

const base: EInvoice = {
  supplierTaxId: '1234567890', supplierTaxIdKind: 'vkn', buyerTaxId: null, invoiceNo: 'ABC2026000000123',
  ettn: 'f47ac10b-58cc-4372-a567-0e02b2c3d479', date: '2026-10-03', scenario: 'EARSIVFATURA', type: 'SATIS',
  currency: 'TRY', net: 100, vat: 20, vatBreakdown: [{ rate: 20, base: 100, vat: 20 }], total: 120, payable: 120.5, warnings: [],
}

describe('e-Fatura karekodu -> kalem', () => {
  it('ödenecek tutarı ondalık virgülle, tarihi, VKN, no, ETTN ve KDV\'yi aktarır', () => {
    expect(einvoiceToDraft(base, ['TRY', 'USD'])).toEqual({
      amount: '120,5', currency: 'TRY', expenseDate: '2026-10-03', supplierTaxId: '1234567890',
      invoiceNo: 'ABC2026000000123', ettn: base.ettn, vatAmount: 20,
    })
  })
  it('desteklenmeyen para birimi ve eksik alanlar aktarılmaz', () => {
    const d = einvoiceToDraft({ ...base, currency: 'JPY', payable: null, total: null, date: null, ettn: null, vat: null }, ['TRY'])
    expect(d).toEqual({ supplierTaxId: '1234567890', invoiceNo: 'ABC2026000000123' })
  })
})

describe('denetim işaretleri', () => {
  afterEach(() => setDictionary({}, 'tr'))
  it('özet: sayı ve en yüksek önem', () => {
    expect(flagSummary([{ anomalyFlags: null }, {}])).toEqual({ count: 0, worst: null })
    expect(flagSummary([
      { anomalyFlags: [{ code: 'UNUSUAL_PATTERN', severity: 'low', reason: '' }] },
      { anomalyFlags: [{ code: 'DUPLICATE_ETTN', severity: 'high', reason: '' }, { code: 'X', severity: 'medium', reason: '' }] },
    ])).toEqual({ count: 3, worst: 'high' })
  })
  it('kod yerelleştirilir, bilinmeyen kodda sunucu gerekçesi', () => {
    setDictionary({ 'Tutar, bu kategorideki olağan tutarların çok üzerinde (medyan {0}).': 'Amount far above the usual for this category (median {0}).' }, 'en')
    expect(flagText({ code: 'AMOUNT_OUTLIER_CATEGORY', severity: 'low', reason: '', details: { median: 120 } })).toBe(
      'Amount far above the usual for this category (median 120).')
    expect(flagText({ code: 'NEW_CODE', severity: 'low', reason: 'Sunucu gerekçesi' })).toBe('Sunucu gerekçesi')
  })
})
