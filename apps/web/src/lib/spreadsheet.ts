import { tx } from '@/lib/i18n'
import { isWatermarkLine } from '@/lib/watermarkText'
/**
 * Tablo dosyası okuma/yazma (.xlsx ve .csv).
 *
 * SheetJS'in npm'deki son sürümü (0.18.5) dosya okurken prototip kirletme ve ReDoS
 * açıkları taşıdığı için (CVE-2023-30533, CVE-2024-22363) bakımı süren, yalnızca .xlsx
 * okuyan/yazan küçük kütüphanelere geçildi. Kütüphaneler ihtiyaç anında yüklenir,
 * ana pakete girmez. Eski ikili .xls biçimi desteklenmez (kullanıcıya .xlsx olarak
 * kaydetmesi söylenir).
 */

export class SpreadsheetError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'SpreadsheetError'
  }
}

/**
 * Sütun başlığını karşılaştırma için sadeleştirir: büyük/küçük harf, boşluk ve i/ı farkı
 * yok sayılır ("İŞE GİRİŞ TARİHİ" = "işegiriştarihi"). JavaScript'te "İ".toLowerCase()
 * "i" + birleşik nokta (U+0307) verdiği için nokta ayrıca temizlenir.
 */
export function normalizeHeader(h: string): string {
  return h.trim().toLowerCase().normalize('NFC').replace(/\u0307/g, '').replace(/ı/g, 'i').replace(/\s+/g, '')
}

/** Başlığı takma ad sözlüğünden alana çevirir (sözlük anahtarları da aynı biçimde sadeleştirilir). */
export function headerField<T>(aliases: Record<string, T>, header: string): T | undefined {
  const key = normalizeHeader(header)
  for (const [alias, field] of Object.entries(aliases)) if (normalizeHeader(alias) === key) return field
  return undefined
}

type Cell = string | number | boolean | Date | null | undefined

/** Excel tarihini (read-excel-file UTC gece yarısı üretir) yyyy-aa-gg metnine çevirir. */
function cellToValue(c: unknown): unknown {
  if (c instanceof Date) {
    if (Number.isNaN(c.getTime())) return ''
    return c.toISOString().slice(0, 10)
  }
  return c ?? ''
}

/** İlk dolu satırı başlık sayar; her satırı { başlık: değer } nesnesine çevirir (boş hücre = ''). */
export function rowsToRecords(rows: Cell[][]): Record<string, unknown>[] {
  const isEmpty = (r: Cell[]) => r.every((c) => c === null || c === undefined || String(c).trim() === '')
  const start = rows.findIndex((r) => !isEmpty(r))
  if (start < 0) return []
  const header = rows[start].map((h, i) => {
    const s = String(h ?? '').trim()
    return s || tx('Sütun {0}', [i + 1])
  })
  const out: Record<string, unknown>[] = []
  for (const row of rows.slice(start + 1)) {
    if (isEmpty(row)) continue
    const rec: Record<string, unknown> = Object.create(null)
    header.forEach((h, i) => {
      // Başlıklar dosyadan gelir; prototip anahtarları atlanır.
      if (h === '__proto__' || h === 'constructor' || h === 'prototype') return
      rec[h] = cellToValue(row[i])
    })
    out.push({ ...rec })
  }
  return out
}

/** RFC 4180 CSV; ayırıcı ilk satırdan (; , sekme) tahmin edilir. Türkçe Excel ';' kullanır. */
export function parseCsv(text: string): string[][] {
  // Dışa aktarılan CSV'nin başındaki HR360 filigran satırı ("# ad · zaman · kod") veri sayılmaz.
  const noBom = text.replace(/^﻿/, '')
  const firstEnd = noBom.search(/\r?\n/)
  const src = firstEnd > 0 && isWatermarkLine(noBom.slice(0, firstEnd)) ? noBom.slice(firstEnd).replace(/^\r?\n/, '') : noBom
  const firstLine = src.split(/\r?\n/, 1)[0] ?? ''
  const count = (ch: string) => firstLine.split(ch).length - 1
  const delim = [';', ',', '\t'].sort((a, b) => count(b) - count(a))[0]
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let i = 0; i < src.length; i++) {
    const ch = src[i]
    if (quoted) {
      if (ch === '"') {
        if (src[i + 1] === '"') {
          field += '"'
          i++
        } else quoted = false
      } else field += ch
    } else if (ch === '"' && field === '') quoted = true
    else if (ch === delim) {
      row.push(field)
      field = ''
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && src[i + 1] === '\n') i++
      row.push(field)
      rows.push(row)
      row = []
      field = ''
    } else field += ch
  }
  if (field !== '' || row.length) {
    row.push(field)
    rows.push(row)
  }
  return rows.map((r) => r.map((c) => c.trim()))
}

/** Dosyanın ilk sayfasını satır nesneleri olarak okur. */
export async function readSpreadsheet(file: File): Promise<Record<string, unknown>[]> {
  const name = file.name.toLowerCase()
  if (name.endsWith('.csv')) return rowsToRecords(parseCsv(await file.text()))
  if (name.endsWith('.xls')) throw new SpreadsheetError(tx('Eski .xls biçimi desteklenmiyor. Dosyayı Excel\'de .xlsx olarak kaydedip yeniden deneyin.'))
  const { readSheet } = await import('read-excel-file/browser')
  try {
    const rows = await readSheet(file)
    return rowsToRecords(rows as unknown as Cell[][])
  } catch (e) {
    throw new SpreadsheetError(tx('Dosya okunamadı. Geçerli bir .xlsx ya da .csv dosyası seçin.{0}', [e instanceof Error && e.message ? ` (${e.message})` : '']))
  }
}

function toCell(v: unknown): Cell {
  if (v === null || v === undefined) return null
  if (v instanceof Date || typeof v === 'number' || typeof v === 'boolean') return v
  if (typeof v === 'object') return JSON.stringify(v)
  return String(v)
}

/** Her biri ayrı sayfa olan nesne listelerini .xlsx olarak indirir. */
export async function downloadWorkbook(fileName: string, sheets: Record<string, Record<string, unknown>[]>, opts: { watermark?: string | null } = {}): Promise<void> {
  const { default: writeXlsxFile } = await import('write-excel-file/browser')
  const used = new Set<string>()
  // Güvenlik dalgası 2B: indiren kişiyi ve iz kodunu gösteren ayrı bir sayfa (hücre içeriğine dokunulmaz).
  const all = opts.watermark ? { ...sheets, [tx('İz kaydı')]: [{ [tx('İndiren · zaman · iz kodu')]: opts.watermark }] } : sheets
  const data = Object.entries(all).map(([title, list]) => {
    const rows = list.length ? list : [{ Bilgi: tx('Kayıt yok') }]
    const keys = [...new Set(rows.flatMap((r) => Object.keys(r)))]
    // Excel sayfa adı: en çok 31 karakter, []:*?/\ içeremez, benzersiz olmalı.
    let sheet = title.replace(/[[\]:*?/\\]/g, ' ').slice(0, 31) || 'Sayfa'
    for (let n = 2; used.has(sheet); n++) sheet = `${sheet.slice(0, 28)} ${n}`
    used.add(sheet)
    return {
      sheet,
      stickyRowsCount: 1,
      columns: keys.map((k) => ({ width: Math.max(12, Math.min(40, k.length + 4)) })),
      data: [
        keys.map((k) => ({ value: k, fontWeight: 'bold' as const })),
        ...rows.map((r) => keys.map((k) => toCell(r[k]))),
      ],
    }
  })
  await writeXlsxFile(data).toFile(fileName)
}
