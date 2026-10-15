/**
 * Filigran metni yardımcıları (saf; ağ çağrısı yok). İz kodu alma: lib/watermark.ts (requestTrace).
 */
const TRACE_LINE = /^# .+ · .+ · [A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}\s*$/

/** CSV filigran satırı ("# ..."): ayırıcı ve satır sonu karakterleri temizlenir. */
export function csvWatermarkLine(text: string): string {
  return `# ${text.replace(/[\r\n;,\t"]/g, ' ').trim()}`
}

/** CSV'nin başına (BOM korunarak) filigran satırı ekler; metin yoksa değiştirmez. */
export function withCsvWatermark(csv: string, text?: string | null): string {
  if (!text) return csv
  const bom = csv.startsWith('﻿') ? '﻿' : ''
  return `${bom}${csvWatermarkLine(text)}\r\n${csv.slice(bom.length)}`
}

/** İçe aktarımda HR360 filigran satırı (yalnızca bu biçim) atlanır; diğer "#" ile başlayan satırlar korunur. */
export function isWatermarkLine(line: string): boolean {
  return TRACE_LINE.test(line.replace(/^﻿/, ''))
}

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

/** Yazdırma penceresi için stil: her sayfada tekrar eden soluk çapraz metin + alt bilgi satırı. */
export const WATERMARK_CSS =
  '.hr360-wm{position:fixed;inset:0;pointer-events:none;z-index:9999;display:flex;align-items:center;justify-content:center;overflow:hidden}' +
  '.hr360-wm span{transform:rotate(-30deg);font:600 18pt Arial,sans-serif;color:#000;opacity:.06;white-space:nowrap}' +
  '.hr360-wm-foot{position:fixed;left:0;right:0;bottom:0;text-align:center;font:8pt Arial,sans-serif;color:#666;pointer-events:none}' +
  '@media screen{.hr360-wm-foot{bottom:4px}}'

/** Yazdırma penceresinin gövdesine eklenecek filigran işaretlemesi (metin yoksa boş). */
export function watermarkHtml(text?: string | null): string {
  if (!text) return ''
  const t = esc(text)
  return `<div class="hr360-wm" aria-hidden="true"><span>${t}</span></div><div class="hr360-wm-foot">${t}</div>`
}
