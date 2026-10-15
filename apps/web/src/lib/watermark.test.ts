import { describe, expect, it } from 'vitest'
import { csvWatermarkLine, isWatermarkLine, watermarkHtml, withCsvWatermark } from './watermarkText'
import { parseCsv } from './spreadsheet'

const TEXT = 'Ayşe Yılmaz · 06.10.2026 14:32 · K7P2-MX9Q'

describe('filigran (güvenlik dalgası 2B)', () => {
  it('CSV başına BOM korunarak tek satır eklenir; metin yoksa dokunulmaz', () => {
    const csv = '﻿"Ad";"Soyad"\r\n"A";"B"'
    const out = withCsvWatermark(csv, TEXT)
    expect(out.startsWith('﻿# Ayşe Yılmaz · 06.10.2026 14:32 · K7P2-MX9Q\r\n"Ad"')).toBe(true)
    expect(withCsvWatermark(csv, null)).toBe(csv)
  })

  it('ayırıcı ve satır sonu karakterleri filigran satırından temizlenir', () => {
    expect(csvWatermarkLine('A;B\nC · 1 · K7P2-MX9Q')).toBe('# A B C · 1 · K7P2-MX9Q')
  })

  it('yalnızca HR360 filigran biçimi tanınır', () => {
    expect(isWatermarkLine(`# ${TEXT}`)).toBe(true)
    expect(isWatermarkLine('#;Ad;Soyad')).toBe(false)
    expect(isWatermarkLine('# not')).toBe(false)
  })

  it('dışa aktarılan CSV yeniden içe aktarılınca filigran satırı veri sayılmaz', () => {
    const rows = parseCsv(withCsvWatermark('﻿"Ad";"Soyad"\r\n"Ali";"Veli"', TEXT))
    expect(rows).toEqual([['Ad', 'Soyad'], ['Ali', 'Veli']])
  })

  it('yazdırma filigranı HTML kaçışlı; metin yoksa boş', () => {
    expect(watermarkHtml('<b>x</b> · 1 · K7P2-MX9Q')).toContain('&lt;b&gt;x&lt;/b&gt;')
    expect(watermarkHtml(null)).toBe('')
  })
})
