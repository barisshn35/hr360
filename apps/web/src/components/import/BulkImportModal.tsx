import { useState, type ReactNode } from 'react'
import { motion } from 'motion/react'
import { CheckCircle2, FileSpreadsheet, LoaderCircle, Table2, XCircle } from 'lucide-react'
import { headerField, readSpreadsheet, SpreadsheetError } from '@/lib/spreadsheet'
import { Modal } from '@/components/ui/modal'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/ui/toast'
import { tx } from '@/lib/i18n'
import { ImportSource, sheetColumns, type ImportMode, type ImportTemplate } from './ImportSource'
import { compactSheet, emptySheet, sheetToRecords, type SheetColumn } from './SheetEditor'

export type { ImportTemplate }

/** Ön izlemedeki bir satır: dosyadaki satır numarası, tablo hücreleri ve (varsa) neden atlanacağı. */
export interface ImportRow<T> {
  rowNumber: number
  data: T
  cells: ReactNode[]
  error?: string
}

/**
 * Tarayıcıda çalışan toplu içe aktarma kabuğu (çalışan içe aktarmasıyla aynı yaklaşım): dosya okunur,
 * satırlar ön izlemede doğrulanır, geçerli satırlar sırayla ürünün kendi API'leriyle oluşturulur.
 * `run` sırayla çağrılır; önceki satırların sonucuna dayanan satırlar (ör. dosyada daha önce tanımlanan
 * üst departman) bu sayede çözülebilir.
 */
export function BulkImportModal<T>({
  open, onClose, title, note, template, columns, parse, run, entityLabel, onDone, sheetHints,
}: {
  open: boolean
  onClose: () => void
  title: string
  note: string
  template: ImportTemplate
  columns: string[]
  /** Dosyadaki satırları (başlık → değer) ön izleme satırlarına çevirir ve doğrular. */
  parse: (raw: Record<string, unknown>[]) => Promise<ImportRow<T>[]> | ImportRow<T>[]
  /** Tek satırı oluşturur; uyarı (ör. "üye bulunamadı") varsa metin döner. */
  run: (row: ImportRow<T>) => Promise<string | void>
  /** Sonuç mesajlarında kullanılır, ör. "departman". */
  entityLabel: string
  onDone?: () => void
  /** Tabloya giriş için başlık adıyla ipuçları: örnek değer, öneri listesi, sütun genişliği. */
  sheetHints?: Record<string, Omit<SheetColumn, 'header'>>
}) {
  const toast = useToast()
  const [mode, setMode] = useState<ImportMode>('file')
  const sheetCols = sheetColumns(template.headers, sheetHints)
  const [sheet, setSheet] = useState(() => emptySheet(template.headers.length))
  const [rows, setRows] = useState<ImportRow<T>[]>([])
  const [fileName, setFileName] = useState('')
  const [parsing, setParsing] = useState(false)
  const [importing, setImporting] = useState(false)
  const [progress, setProgress] = useState(0)
  const [results, setResults] = useState<{ row: ImportRow<T>; ok: boolean; message?: string }[] | null>(null)

  const valid = rows.filter((r) => !r.error)
  const invalid = rows.length - valid.length

  function reset() {
    setRows([]); setFileName(''); setResults(null); setProgress(0)
  }
  function close() {
    if (importing) return
    reset(); setSheet(emptySheet(template.headers.length)); setMode('file'); onClose()
  }

  /** Tablodaki kayıtları ön izlemeye alır; tablo korunur, "Tabloyu düzenle" ile geri dönülür. */
  async function readSheet() {
    const records = sheetToRecords(sheetCols, sheet)
    setSheet(compactSheet(sheet, template.headers.length))
    setFileName(tx('Tablodan girilen {0} kayıt', [records.length])); setResults(null); setParsing(true)
    try {
      setRows(await parse(records))
    } catch (err) {
      toast.stop(err instanceof Error ? err.message : tx('Bilinmeyen hata'))
      setRows([])
    } finally {
      setParsing(false)
    }
  }
  const sheetFilled = sheetToRecords(sheetCols, sheet).length
  const editing = mode === 'sheet' && !fileName

  async function readFile(file: File) {
    setFileName(file.name); setResults(null); setParsing(true)
    try {
      setRows(await parse(await readSpreadsheet(file)))
    } catch (err) {
      toast.stop(err instanceof SpreadsheetError ? err.message : tx('Dosya okunamadı. Geçerli bir .xlsx ya da .csv dosyası seçin.'))
      setRows([])
    } finally {
      setParsing(false)
    }
  }

  async function start() {
    setImporting(true); setProgress(0)
    const out: { row: ImportRow<T>; ok: boolean; message?: string }[] = []
    for (const row of valid) {
      try {
        const warning = await run(row)
        out.push({ row, ok: true, message: warning || undefined })
      } catch (e) {
        out.push({ row, ok: false, message: e instanceof Error ? e.message : tx('Bilinmeyen hata') })
      }
      setProgress(out.length)
    }
    setResults(out); setImporting(false); onDone?.()
    const okCount = out.filter((o) => o.ok).length
    if (okCount === out.length) toast.ok(tx('{0} {1} içe aktarıldı.', [okCount, entityLabel]))
    else toast.stop(tx('{0} başarılı, {1} başarısız. Detaylar aşağıda.', [okCount, out.length - okCount]))
  }

  return (
    <Modal open={open} onClose={close} title={title} note={note} size={mode === 'sheet' && !fileName ? 'sheet' : 'lg'}
      footer={<>
        <Button variant="outline" onClick={close} disabled={importing}>{results ? tx('Kapat') : tx('Vazgeç')}</Button>
        {editing && (
          <Button onClick={() => void readSheet()} disabled={sheetFilled === 0}>
            {sheetFilled === 0 ? tx('Tabloya kayıt girin') : tx('{0} kaydı kontrol et', [sheetFilled])}
          </Button>
        )}
        {!results && !editing && (
          <Button onClick={start} disabled={importing || valid.length === 0}>
            {importing && <LoaderCircle className="size-4 animate-spin" />}
            {importing ? tx('İçe aktarılıyor ({0}/{1})', [progress, valid.length])
              : valid.length === 0 ? tx('İçe aktarılacak geçerli kayıt yok') : tx('{0} kaydı içe aktar', [valid.length])}
          </Button>
        )}
      </>}>
      <div className="space-y-4">
        {!fileName && (
          <ImportSource mode={mode} onModeChange={setMode} onFile={(f) => void readFile(f)} template={template}
            columns={sheetCols} sheet={sheet} onSheetChange={setSheet} />
        )}

        {fileName && (
          <div className="flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm">
            {mode === 'sheet' ? <Table2 className="size-4 shrink-0" /> : <FileSpreadsheet className="size-4 shrink-0" />}
            <span className="truncate">{fileName}</span>
            {!results && !importing && (
              <button type="button" className="ml-auto cursor-pointer text-xs text-muted-foreground underline hover:text-foreground" onClick={reset}>
                {mode === 'sheet' ? tx('Tabloyu düzenle') : tx('Değiştir')}
              </button>
            )}
          </div>
        )}

        {parsing && <div className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" />{tx('Dosya okunuyor…')}</div>}

        {!parsing && rows.length > 0 && !results && (
          <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="space-y-3">
            <div className="flex gap-4 text-sm">
              <span className="flex items-center gap-1 text-[hsl(var(--success))]"><CheckCircle2 className="size-4" /> {tx('{0} geçerli', [valid.length])}</span>
              {invalid > 0 && <span className="flex items-center gap-1 text-destructive"><XCircle className="size-4" /> {tx('{0} hatalı (atlanacak)', [invalid])}</span>}
            </div>
            <div className="max-h-72 overflow-auto rounded-lg border border-border">
              <table className="w-full text-left text-sm">
                <thead className="bg-muted/50 text-xs">
                  <tr>
                    <th className="px-3 py-2">{tx('Satır')}</th>
                    {columns.map((c) => <th key={c} className="px-3 py-2">{c}</th>)}
                    <th className="px-3 py-2">{tx('Durum')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.rowNumber} className="border-t border-border">
                      <td className="px-3 py-2 text-muted-foreground">{r.rowNumber}</td>
                      {r.cells.map((c, i) => <td key={i} className="px-3 py-2">{c || '—'}</td>)}
                      <td className="px-3 py-2">{r.error ? <span className="text-destructive">{r.error}</span> : <span className="text-[hsl(var(--success))]">{tx('Hazır')}</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </motion.div>
        )}

        {results && (
          <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="space-y-2">
            <div className="flex gap-4 text-sm">
              <span className="flex items-center gap-1 text-[hsl(var(--success))]"><CheckCircle2 className="size-4" />{tx('{0} başarılı', [results.filter((r) => r.ok).length])}</span>
              {results.some((r) => !r.ok) && <span className="flex items-center gap-1 text-destructive"><XCircle className="size-4" />{tx('{0} başarısız', [results.filter((r) => !r.ok).length])}</span>}
            </div>
            {results.some((r) => r.message) && (
              <ul className="max-h-56 divide-y divide-border overflow-y-auto rounded-lg border border-border text-[13px]">
                {results.filter((r) => r.message).map((r) => (
                  <li key={r.row.rowNumber} className={`px-3 py-2 ${r.ok ? 'text-muted-foreground' : 'text-destructive'}`}>
                    {tx('Satır {0}: {1}', [r.row.rowNumber, r.message])}
                  </li>
                ))}
              </ul>
            )}
          </motion.div>
        )}
      </div>
    </Modal>
  )
}

/** Hücre değerini kırpılmış metne çevirir. */
export const cell = (v: unknown) => String(v ?? '').trim()

/** Satırdaki başlıkları alan adlarına eşler (büyük/küçük harf, Türkçe karakter ve boşluk duyarsız). */
export function mapRow<K extends string>(record: Record<string, unknown>, aliases: Record<string, K>) {
  const out: Partial<Record<K, unknown>> = {}
  for (const [k, v] of Object.entries(record)) {
    const f = headerField(aliases, k)
    if (f) out[f] = v
  }
  return out
}
