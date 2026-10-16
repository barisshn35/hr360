import { useId, useRef, type ClipboardEvent, type KeyboardEvent } from 'react'
import { Eraser, Plus, X } from 'lucide-react'
import { normalizeHeader } from '@/lib/spreadsheet'
import { Button } from '@/components/ui/button'
import { tx } from '@/lib/i18n'

export interface SheetColumn {
  header: string
  placeholder?: string
  /** Hücrede öneri listesi (ör. departman adları); serbest yazım da kabul edilir. */
  options?: string[]
  /** Piksel cinsinden en az genişlik. */
  width?: number
}

export const emptySheet = (cols: number, n = 8): string[][] => Array.from({ length: n }, () => Array<string>(cols).fill(''))

const isBlank = (r: string[]) => r.every((c) => !c.trim())

/** Boş satırları atar; kalanları başlık → değer kayıtlarına çevirir (dosya okumasıyla aynı biçim). */
export function sheetToRecords(columns: SheetColumn[], rows: string[][]): Record<string, unknown>[] {
  return rows.filter((r) => !isBlank(r)).map((r) => Object.fromEntries(columns.map((c, i) => [c.header, (r[i] ?? '').trim()])))
}

/** Ön izlemedeki satır numaraları tabloyla eşleşsin diye aradaki boş satırlar kaldırılır (en az bir boş satır kalır). */
export function compactSheet(rows: string[][], cols: number): string[][] {
  const filled = rows.filter((r) => !isBlank(r))
  return [...filled, ...emptySheet(cols, Math.max(1, 3 - filled.length))]
}

/**
 * Panodan gelen tablo metnini (Excel/Sheets sekme ve satır sonuyla kopyalar) hücrelere böler.
 * Tırnak içindeki sekme/satır sonu (çok satırlı hücre) korunur.
 */
export function parseClipboard(text: string): string[][] {
  const out: string[][] = [[]]
  let cur = ''
  let quoted = false
  const src = text.replace(/\r\n?/g, '\n')
  for (let i = 0; i < src.length; i++) {
    const ch = src[i]
    if (quoted) {
      if (ch === '"' && src[i + 1] === '"') { cur += '"'; i++ }
      else if (ch === '"') quoted = false
      else cur += ch
    } else if (ch === '"' && cur === '') quoted = true
    else if (ch === '\t') { out[out.length - 1].push(cur); cur = '' }
    else if (ch === '\n') { out[out.length - 1].push(cur); cur = ''; out.push([]) }
    else cur += ch
  }
  out[out.length - 1].push(cur)
  // Excel kopyası sonda satır sonu bırakır.
  if (out.length > 1 && out[out.length - 1].length === 1 && out[out.length - 1][0] === '') out.pop()
  return out
}

/**
 * Yapıştırılan bloğu tabloya yerleştirir. İlk satır sütun başlıklarıysa sütunlar ada göre eşlenir
 * (sıra farklı olabilir) ve başlık satırı atlanır; değilse blok seçili hücreden başlayarak yerleşir.
 */
export function pasteInto(rows: string[][], columns: SheetColumn[], block: string[][], row: number, col: number): string[][] {
  const headerIdx = columns.map((c) => normalizeHeader(c.header))
  const firstMap = block[0].map((h) => headerIdx.indexOf(normalizeHeader(h)))
  const isHeader = firstMap.filter((i) => i >= 0).length >= Math.min(2, block[0].length)
  const data = isHeader ? block.slice(1) : block
  const target = (j: number) => (isHeader ? firstMap[j] : col + j)
  const startRow = isHeader && isBlank(rows[row] ?? []) ? row : isHeader ? rows.length : row
  const next = rows.map((r) => [...r])
  data.forEach((line, k) => {
    const ri = startRow + k
    while (next.length <= ri) next.push(Array<string>(columns.length).fill(''))
    line.forEach((v, j) => {
      const ci = target(j)
      if (ci >= 0 && ci < columns.length) next[ri][ci] = v.trim()
    })
  })
  return next
}

/**
 * İçe aktarma için Excel benzeri giriş tablosu: hücreye yazılır, Excel/Sheets'ten blok yapıştırılır,
 * Enter ve ok tuşlarıyla gezilir. Satır numaraları Excel'deki gibi 2'den başlar (1. satır başlık),
 * böylece ön izlemedeki "Satır" sütunuyla aynıdır.
 */
export function SheetEditor({ columns, rows, onChange }: {
  columns: SheetColumn[]
  rows: string[][]
  onChange: (rows: string[][]) => void
}) {
  const id = useId()
  const box = useRef<HTMLDivElement>(null)
  const filled = rows.filter((r) => !isBlank(r)).length

  function focus(r: number, c: number) {
    requestAnimationFrame(() => box.current?.querySelector<HTMLInputElement>(`[data-cell="${r}:${c}"]`)?.focus())
  }

  function set(r: number, c: number, v: string) {
    const next = rows.map((x) => [...x])
    next[r][c] = v
    // Son satıra yazılınca altına boş satır açılır; tablo hiç "dolmaz".
    if (r === rows.length - 1 && v) next.push(Array<string>(columns.length).fill(''))
    onChange(next)
  }

  function onPaste(e: ClipboardEvent<HTMLInputElement>, r: number, c: number) {
    const text = e.clipboardData.getData('text/plain')
    if (!/[\t\n]/.test(text.replace(/\n+$/, ''))) return // tek hücre: tarayıcının kendi yapıştırması
    e.preventDefault()
    const next = pasteInto(rows, columns, parseClipboard(text), r, c)
    if (!isBlank(next[next.length - 1])) next.push(Array<string>(columns.length).fill(''))
    onChange(next)
  }

  function onKey(e: KeyboardEvent<HTMLInputElement>, r: number, c: number) {
    const el = e.currentTarget
    if (e.key === 'Enter' || e.key === 'ArrowDown') {
      e.preventDefault()
      if (r === rows.length - 1) onChange([...rows, Array<string>(columns.length).fill('')])
      focus(r + 1, c)
    } else if (e.key === 'ArrowUp' && r > 0) {
      e.preventDefault(); focus(r - 1, c)
    } else if (e.key === 'ArrowRight' && el.selectionStart === el.value.length && c < columns.length - 1) {
      e.preventDefault(); focus(r, c + 1)
    } else if (e.key === 'ArrowLeft' && el.selectionEnd === 0 && c > 0) {
      e.preventDefault(); focus(r, c - 1)
    }
  }

  function removeRow(r: number) {
    const next = rows.filter((_, i) => i !== r)
    onChange(next.length ? next : emptySheet(columns.length, 1))
  }

  return (
    <div className="space-y-2">
      <p className="text-xs text-muted-foreground">
        {tx('Hücrelere yazın ya da Excel\'den hücreleri kopyalayıp bir hücreye yapıştırın. Başlık satırını da kopyalarsanız sütunlar adına göre eşleşir. Enter ve ok tuşlarıyla hücreler arasında gezebilirsiniz.')}
      </p>
      <div ref={box} className="max-h-[52vh] overflow-auto rounded-lg border border-border">
        <table className="w-full border-collapse text-sm">
          <thead className="sticky top-0 z-10 bg-muted text-xs">
            <tr>
              <th className="w-10 border-b border-r border-border px-2 py-2 text-center font-normal text-muted-foreground">1</th>
              {columns.map((c) => (
                <th key={c.header} className="border-b border-r border-border px-2 py-2 text-left font-semibold" style={{ minWidth: c.width ?? 140 }}>{c.header}</th>
              ))}
              <th className="w-8 border-b border-border" />
            </tr>
          </thead>
          <tbody>
            {rows.map((row, r) => (
              <tr key={r} className="group">
                <td className="border-b border-r border-border bg-muted/40 px-2 text-center text-xs text-muted-foreground tabular-nums">{r + 2}</td>
                {columns.map((c, ci) => (
                  <td key={c.header} className="border-b border-r border-border p-0">
                    <input
                      data-cell={`${r}:${ci}`}
                      aria-label={`${c.header} ${r + 2}`}
                      value={row[ci] ?? ''}
                      placeholder={r === 0 ? c.placeholder : undefined}
                      list={c.options?.length ? `${id}-${ci}` : undefined}
                      onChange={(e) => set(r, ci, e.target.value)}
                      onPaste={(e) => onPaste(e, r, ci)}
                      onKeyDown={(e) => onKey(e, r, ci)}
                      className="h-9 w-full bg-transparent px-2 outline-none placeholder:text-muted-foreground/50 focus:bg-primary/5 focus:ring-2 focus:ring-inset focus:ring-primary"
                    />
                  </td>
                ))}
                <td className="border-b border-border p-0 text-center">
                  {!isBlank(row) && (
                    <button type="button" onClick={() => removeRow(r)} aria-label={tx('Satırı sil')}
                      className="cursor-pointer rounded p-1 text-muted-foreground opacity-0 hover:text-destructive focus:opacity-100 group-hover:opacity-100">
                      <X className="size-3.5" />
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {columns.map((c, ci) => c.options?.length ? (
          <datalist key={c.header} id={`${id}-${ci}`}>{c.options.map((o) => <option key={o} value={o} />)}</datalist>
        ) : null)}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="outline" size="sm" onClick={() => onChange([...rows, ...emptySheet(columns.length, 10)])}>
          <Plus className="size-4" />{' '}{tx('10 satır ekle')}
        </Button>
        {filled > 0 && (
          <Button variant="ghost" size="sm" onClick={() => onChange(emptySheet(columns.length))}>
            <Eraser className="size-4" />{' '}{tx('Tabloyu temizle')}
          </Button>
        )}
        <span className="ml-auto text-xs text-muted-foreground">{tx('{0} dolu satır', [filled])}</span>
      </div>
    </div>
  )
}
