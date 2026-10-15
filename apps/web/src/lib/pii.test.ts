import { describe, expect, it } from 'vitest'
import { fold, ibanValid, piiCategories, piiRules, scanPii, tcknValid, PII_LABELS } from './pii'

// Tek kaynak: ml-inference'taki kural dosyası. Arayüz kopyası onunla birebir aynı olmalı.
const mlRules = import.meta.glob('../../../ml-inference/pii_rules.json', { eager: true, import: 'default' })

const VALID_TCKN = '10000000146'
const VALID_IBAN = 'TR33 0006 1005 1978 6457 8413 26'
const cats = (t: string) => scanPii(t).map((f) => f.category)

describe('kural dosyası', () => {
  it('ml-inference/pii_rules.json ile aynıdır', () => {
    const files = Object.values(mlRules)
    expect(files).toHaveLength(1)
    expect(piiRules).toEqual(files[0])
  })
  it('her kategori için arayüz etiketi var ve sunucu etiketiyle aynı', () => {
    for (const c of piiRules.categories) expect(PII_LABELS[c.key as keyof typeof PII_LABELS]).toBe(c.label)
  })
})

describe('TCKN / IBAN', () => {
  it('TCKN sağlama hanelerini denetler', () => {
    expect(tcknValid(VALID_TCKN)).toBe(true)
    expect(tcknValid('10000000147')).toBe(false)
    expect(tcknValid('01234567890')).toBe(false)
    expect(cats(`Kimlik no: ${VALID_TCKN}`)).toEqual(['tckn'])
    expect(cats('Sipariş no 12345678901')).toEqual([])
  })
  it('IBAN mod-97', () => {
    expect(ibanValid(VALID_IBAN)).toBe(true)
    expect(ibanValid('DE89370400440532013000')).toBe(true)
    expect(ibanValid('TR33 0006 1005 1978 6457 8413 27')).toBe(false)
    expect(cats(`Hesap ${VALID_IBAN} numaralı`)).toEqual(['iban'])
  })
})

describe('scanPii', () => {
  it('telefon ve e-posta', () => {
    expect(cats('0532 123 45 67')).toEqual(['phone'])
    expect(cats('+90 (532) 123-45-67')).toEqual(['phone'])
    expect(cats('Tutar 2125551234 TL')).toEqual([])
    expect(cats('ayse.yilmaz@ornek.com.tr')).toEqual(['email'])
  })
  it('özel nitelikli sözcükler ekleriyle ve Türkçe karakter olmadan', () => {
    expect(cats('Sağlık raporunu getirdi')).toContain('health')
    expect(cats('SAGLIK RAPORU')).toContain('health')
    expect(cats('Adli sicil kaydında sabıkası var')).toContain('criminal')
    expect(cats('Sendikaya üye oldu')).toContain('union')
    expect(cats('Proje raporu hazırlandı')).toEqual([])
    expect(cats('Haftalık hedefler')).toEqual([])
  })
  it('istisna kalıpları: "dini bayram" inanç bilgisi değildir', () => {
    expect(cats('Dini bayramda memleketime gideceğim')).toEqual([])
    expect(cats('dini bayram tatili + yıllık izin')).toEqual([])
    expect(cats('Dini inancı gereği oruç tutuyor')).toContain('religion')
    expect(cats('Bayramda camiye gidecek')).toContain('religion')
  })
  it('konumlar özgün metinle aynıdır', () => {
    const text = 'İstanbul: İLAÇ KULLANIYOR; ŞİZOFRENİ'
    expect(scanPii(text).map((f) => text.slice(f.start, f.end))).toEqual(['İLAÇ KULLANIYOR', 'ŞİZOFRENİ'])
    expect(fold('İŞÇİ').length).toBe(4)
  })
  it('kategoriler tekrarsız; özel nitelik işaretli', () => {
    const f = scanPii(`diyabet ve kanser, TCKN ${VALID_TCKN}`)
    expect(piiCategories(f)).toEqual(['health', 'tckn'])
    expect(f.find((x) => x.category === 'health')?.special).toBe(true)
    expect(f.find((x) => x.category === 'tckn')?.special).toBe(false)
  })
})
