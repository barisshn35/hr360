import { describe, expect, it } from 'vitest'
import { compactSheet, emptySheet, parseClipboard, pasteInto, sheetToRecords } from './SheetEditor'

const cols = ['Ad', 'Soyad', 'E-posta'].map((header) => ({ header }))

describe('SheetEditor', () => {
  it('Excel kopyasını hücrelere böler, sondaki satır sonunu atar, tırnaklı hücreyi korur', () => {
    expect(parseClipboard('a\tb\r\nc\td\r\n')).toEqual([['a', 'b'], ['c', 'd']])
    expect(parseClipboard('"x\ty"\tz')).toEqual([['x\ty', 'z']])
    expect(parseClipboard('"a ""b"""\tc')).toEqual([['a "b"', 'c']])
  })

  it('başlıksız blok seçili hücreden yerleşir ve tabloyu büyütür', () => {
    const out = pasteInto(emptySheet(3, 1), cols, [['Soy1', 'e1'], ['Soy2', 'e2']], 0, 1)
    expect(out).toEqual([['', 'Soy1', 'e1'], ['', 'Soy2', 'e2']])
  })

  it('başlık satırı varsa sütunları ada göre eşler ve başlığı atlar', () => {
    const block = [['E-POSTA', 'ad', 'Bilinmeyen'], ['a@x.com', 'Ayşe', '?']]
    expect(pasteInto(emptySheet(3, 2), cols, block, 0, 2)[0]).toEqual(['Ayşe', '', 'a@x.com'])
  })

  it('boş satırları atlayıp kayda çevirir; sıkıştırma ön izleme numaralarını hizalar', () => {
    const rows = [['Ayşe', ' Yılmaz ', ''], ['', '', ''], ['Ali', 'Kaya', 'a@x.com']]
    expect(sheetToRecords(cols, rows)).toEqual([
      { Ad: 'Ayşe', Soyad: 'Yılmaz', 'E-posta': '' },
      { Ad: 'Ali', Soyad: 'Kaya', 'E-posta': 'a@x.com' },
    ])
    expect(compactSheet(rows, 3).slice(0, 2).map((r) => r[0])).toEqual(['Ayşe', 'Ali'])
  })
})
