import { describe, expect, it } from 'vitest'
import writeXlsxFile from 'write-excel-file/node'
import { readSheet } from 'read-excel-file/node'
import { headerField, parseCsv, rowsToRecords } from './spreadsheet'

describe('parseCsv', () => {
  it('Türkçe Excel noktalı virgül ayırıcısı, tırnak ve BOM', () => {
    const rows = parseCsv('﻿Ad;Soyad;Not\r\nAyşe;Yılmaz;"a;b ""c"""\n\nMehmet;Kaya;\n')
    expect(rows).toEqual([['Ad', 'Soyad', 'Not'], ['Ayşe', 'Yılmaz', 'a;b "c"'], [''], ['Mehmet', 'Kaya', '']])
  })
  it('virgül ayırıcı ve tırnak içinde satır sonu', () => {
    expect(parseCsv('a,b\n"x\ny",2')).toEqual([['a', 'b'], ['x\ny', '2']])
  })
})

describe('rowsToRecords', () => {
  it('ilk dolu satır başlık, boş satırlar atlanır, eksik hücre boş metin', () => {
    const recs = rowsToRecords([[null, null], ['Ad', 'Tarih'], ['Ayşe', new Date(Date.UTC(2026, 2, 5))], [null, ''], ['Ali']])
    expect(recs).toEqual([{ Ad: 'Ayşe', Tarih: '2026-03-05' }, { Ad: 'Ali', Tarih: '' }])
  })
  it('prototip anahtarlarını almaz', () => {
    const [r] = rowsToRecords([['__proto__', 'Ad'], ['{"polluted":1}', 'x']])
    expect(Object.keys(r)).toEqual(['Ad'])
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
  })
})

describe('xlsx gidiş-dönüş', () => {
  it('yazılan dosya aynı değerlerle okunur (tarih, sayı, metin)', async () => {
    const blob = await writeXlsxFile([
      ['Ad', 'Tutar', 'Tarih'],
      ['Ayşe', 1250.5, { value: new Date(Date.UTC(2026, 9, 1)), type: Date, format: 'dd.mm.yyyy' }],
    ]).toBuffer()
    const rows = await readSheet(blob)
    expect(rowsToRecords(rows as never)).toEqual([{ Ad: 'Ayşe', Tutar: 1250.5, Tarih: '2026-10-01' }])
  })
})

describe('headerField', () => {
  const aliases = { 'işegiriştarihi': 'hireDate', 'açıklama': 'description', email: 'email' }
  it('Türkçe büyük harfler ve boşluklar', () => {
    expect(headerField(aliases, 'İşe giriş tarihi')).toBe('hireDate')
    expect(headerField(aliases, 'İŞE GİRİŞ TARİHİ')).toBe('hireDate')
    expect(headerField(aliases, 'AÇIKLAMA')).toBe('description')
    expect(headerField(aliases, 'EMAIL')).toBe('email')
    expect(headerField(aliases, 'Bilinmeyen')).toBeUndefined()
  })
})
