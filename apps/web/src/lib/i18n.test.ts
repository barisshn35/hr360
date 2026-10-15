import { afterAll, describe, expect, it } from 'vitest'
import { pct, setDictionary, translateServerData, tx, txServer } from './i18n'

describe('i18n', () => {
  afterAll(() => setDictionary({}, 'tr'))

  it('Türkçede kaynak metni yer tutucularıyla döndürür', () => {
    setDictionary({ 'Kaydet': 'Save' }, 'tr')
    expect(tx('Kaydet')).toBe('Kaydet')
    expect(tx('{0} kayıt', [3])).toBe('3 kayıt')
    expect(pct(40)).toBe('%40')
  })

  it('İngilizcede sözlükten çevirir, tekil/çoğul seçer, eksikte Türkçeye düşer', () => {
    setDictionary({ 'Kaydet': 'Save', '{0} kayıt': '{0} record|{0} records', 'Toplam {0} / {1}': '{1} of {0}' }, 'en')
    expect(tx('Kaydet')).toBe('Save')
    expect(tx('{0} kayıt', [1])).toBe('1 record')
    expect(tx('{0} kayıt', [5])).toBe('5 records')
    expect(tx('Toplam {0} / {1}', [10, 2])).toBe('2 of 10')
    expect(tx('Çevrilmemiş')).toBe('Çevrilmemiş')
    expect(pct(40)).toBe('40%')
  })

  it('sunucu iletilerini birebir ve kalıpla çevirir', () => {
    setDictionary({
      '@server:Talep onaylandı': 'Request approved',
      '@server:{0} gündür izin kullanmadı': 'No leave taken for {0} day|No leave taken for {0} days',
      '@server:{0} günlük {1} talebi ({2} - {3})': '{0}-day {1} request ({2} – {3})',
    }, 'en')
    expect(txServer('Talep onaylandı')).toBe('Request approved')
    expect(txServer('261 gündür izin kullanmadı')).toBe('No leave taken for 261 days')
    expect(txServer('1 gündür izin kullanmadı')).toBe('No leave taken for 1 day')
    expect(txServer('3 günlük Annual talebi (07.06.2027 - 09.06.2027)')).toBe('3-day Annual request (07.06.2027 – 09.06.2027)')
    expect(txServer('Bilinmeyen ileti')).toBe('Bilinmeyen ileti')
    setDictionary({
      '@server:{0} günlük {1} talebi ({2} - {3})': '{0}-day {1} request ({2} – {3})',
      '@server:{0} saatlik {1} talebi ({2})': '{0}-hour {1} request ({2})',
      '@server:yıllık izin': 'annual leave',
    }, 'en')
    // İzin türü adı da çevrilir; Türkçe ondalık virgül İngilizcede nokta olur.
    expect(txServer('0,5 günlük yıllık izin talebi (28.10.2026 - 28.10.2026)')).toBe('0.5-day annual leave request (28.10.2026 – 28.10.2026)')
    expect(txServer('7,5 saatlik yıllık izin talebi (27.10.2026)')).toBe('7.5-hour annual leave request (27.10.2026)')
    expect(txServer(null)).toBe('')
  })
})

describe('kalıp önceliği', () => {
  it('iç içe iletide daha belirgin kalıp kazanır', () => {
    setDictionary({
      '@server:{0} günlük {1} talebi ({2} - {3})': '{0}-day {1} request ({2} – {3})',
      '@server:"{0}" talebiniz reddedildi. Gerekçe: {1}': 'Your request "{0}" was rejected. Reason: {1}',
      '@server:(Slack üzerinden)': '(via Slack)',
    }, 'en')
    expect(txServer('"1 günlük Unpaid talebi (04.10.2028 - 04.10.2028)" talebiniz reddedildi. Gerekçe: (Slack üzerinden)'))
      .toBe('Your request "1-day Unpaid request (04.10.2028 – 04.10.2028)" was rejected. Reason: (via Slack)')
    setDictionary({}, 'tr')
  })

  it('imzalı belge metinleri çevrilmez; kalıplar çok satırlı metni yakalamaz', () => {
    setDictionary({ '@server:{0} İnsan Kaynakları': '{0} Human Resources', '@server:Talep onaylandı': 'Request approved' }, 'en')
    const letter = 'Sayın Ayşe,\nTeklifimizi sunarız.\n\nSaygılarımızla,\nDemo İnsan Kaynakları'
    expect(txServer('Demo İnsan Kaynakları')).toBe('Demo Human Resources')
    expect(txServer(letter)).toBe(letter)
    const out = translateServerData({ letterText: 'Demo İnsan Kaynakları', status: 'Talep onaylandı', note: letter })
    expect(out).toEqual({ letterText: 'Demo İnsan Kaynakları', status: 'Request approved', note: letter })
    setDictionary({}, 'tr')
  })
})
