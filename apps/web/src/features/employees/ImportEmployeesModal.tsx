import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useEmployees } from '@/api/queries'
import { LoaderCircle, FileSpreadsheet, CheckCircle2, XCircle, Table2 } from 'lucide-react'
import { headerField, readSpreadsheet, SpreadsheetError } from '@/lib/spreadsheet'
import { employeeApi } from '@/api/employees'
import type { Employee } from '@/api/types'
import { tenantApi } from '@/api/tenant'
import { qk } from '@/api/queries'
import { Modal } from '@/components/ui/modal'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/ui/toast'
import { tx } from '@/lib/i18n'
import { CopyButton } from '@/components/ui/copy-button'
import { ImportSource, sheetColumns, type ImportMode } from '@/components/import/ImportSource'
import { compactSheet, emptySheet, sheetToRecords } from '@/components/import/SheetEditor'

const TEMPLATE = {
  fileName: 'hr360-calisan-sablonu.csv',
  headers: ['Ad', 'Soyad', 'E-posta', 'Telefon', 'İşe giriş tarihi', 'Departman', 'Unvan'],
  example: [['Ayşe', 'Yılmaz', 'ayse.yilmaz@ornek.com', '05551234567', '01.09.2026', 'Mühendislik', 'Yazılım Mühendisi']],
}

interface DepartmentOption {
  id: string
  label: string
}

interface ParsedRow {
  rowNumber: number
  firstName: string
  lastName: string
  email: string
  phone: string
  hireDate: string // yyyy-MM-dd
  departmentLabel: string
  positionTitle: string
  /** E-postası kayıtlı çalışan: yeni kayıt yerine unvanı/departmanı güncellenir. */
  existing?: Employee
  error?: string
}

type ImportResult = {
  row: ParsedRow
  status: 'ok' | 'failed'
  message?: string
  /** Üretilen parola linki (yalnızca bu ekranda gösterilir). */
  link?: string | null
  /** Kayıtlı çalışan güncellendi (yeni kayıt değil). */
  updated?: boolean
}

// Excel şablonundaki başlıklar için kabul edilen varyasyonlar (küçük harfe
// çevrilip boşluklar temizlenerek karşılaştırılır).
const HEADER_ALIASES: Record<string, keyof Omit<ParsedRow, 'rowNumber' | 'error'>> = {
  ad: 'firstName',
  isim: 'firstName',
  soyad: 'lastName',
  soyisim: 'lastName',
  eposta: 'email',
  'e-posta': 'email',
  email: 'email',
  telefon: 'phone',
  'işegiriştarihi': 'hireDate',
  'işegiris': 'hireDate',
  giriştarihi: 'hireDate',
  departman: 'departmentLabel',
  unvan: 'positionTitle',
  'ünvan': 'positionTitle',
  pozisyon: 'positionTitle',
  'görev': 'positionTitle',
  title: 'positionTitle',
}

/** Çalışanın bugün geçerli ataması. */
const currentAssignment = (e: Employee) => e.assignments?.find((a) => !a.effectiveTo)

/** Takvimde gerçekten var olan bir gün mü (32.13.2026 ya da 30.02.2026 değil)? */
function isRealDate(y: number, m: number, d: number): boolean {
  if (y < 1900 || y > 2100 || m < 1 || m > 12 || d < 1) return false
  const dt = new Date(Date.UTC(y, m - 1, d))
  return dt.getUTCFullYear() === y && dt.getUTCMonth() === m - 1 && dt.getUTCDate() === d
}

function excelDateToIso(value: unknown): string {
  if (value instanceof Date) {
    if (Number.isNaN(value.getTime())) return ''
    const y = value.getFullYear()
    const m = String(value.getMonth() + 1).padStart(2, '0')
    const d = String(value.getDate()).padStart(2, '0')
    return `${y}-${m}-${d}`
  }
  const s = String(value ?? '').trim()
  // gg.aa.yyyy veya gg/aa/yyyy
  const m1 = s.match(/^(\d{1,2})[./](\d{1,2})[./](\d{4})$/)
  if (m1) return isRealDate(+m1[3], +m1[2], +m1[1]) ? `${m1[3]}-${m1[2].padStart(2, '0')}-${m1[1].padStart(2, '0')}` : ''
  // zaten yyyy-aa-gg
  const m2 = s.match(/^(\d{4})-(\d{2})-(\d{2})$/)
  if (m2) return isRealDate(+m2[1], +m2[2], +m2[3]) ? s : ''
  return ''
}

function parseWorkbook(raw: Record<string, unknown>[], departments: DepartmentOption[], existing: Map<string, Employee>): ParsedRow[] {
  // Dosya içinde aynı e-posta ikinci kez geçerse o satır da hatalı sayılır.
  const seen = new Set<string>()
  const deptByLabel = new Map(departments.map((d) => [d.label.trim().toLowerCase(), d.id]))

  return raw.map((record, idx) => {
    const mapped: Record<string, unknown> = {}
    for (const [key, value] of Object.entries(record)) {
      const field = headerField(HEADER_ALIASES, key)
      if (field) mapped[field] = value
    }

    const firstName = String(mapped.firstName ?? '').trim()
    const lastName = String(mapped.lastName ?? '').trim()
    const email = String(mapped.email ?? '').trim()
    const phone = String(mapped.phone ?? '').trim()
    const hireDate = mapped.hireDate ? excelDateToIso(mapped.hireDate) : ''
    const departmentLabel = String(mapped.departmentLabel ?? '').trim()
    const positionTitle = String(mapped.positionTitle ?? '').trim()

    let error: string | undefined
    const emailKey = email.toLowerCase()
    const known = existing.get(emailKey)
    if (known) {
      // Kayıtlı çalışan: dosyadaki unvan/departman mevcut atamaya işlenir (ör. unvanları toplu doldurmak).
      const cur = currentAssignment(known)
      const deptId = departmentLabel ? deptByLabel.get(departmentLabel.toLowerCase()) : cur?.departmentId
      if (seen.has(emailKey)) error = tx('E-posta dosyada birden fazla kez geçiyor')
      else if (departmentLabel && !deptId) error = tx('Departman bulunamadı: "{0}"', [departmentLabel])
      else if (!positionTitle && !departmentLabel) error = tx('Bu e-postayla kayıtlı çalışan var')
      else if (!deptId) error = tx('Kayıtlı çalışanın departmanı yok; unvan için Departman sütununu doldurun')
      else if (cur && cur.departmentId === deptId && (cur.positionTitle ?? '') === (positionTitle || (cur.positionTitle ?? '')))
        error = tx('Kayıt zaten güncel')
      if (email) seen.add(emailKey)
      return { rowNumber: idx + 2, firstName, lastName, email, phone, hireDate, departmentLabel, positionTitle, existing: known, error }
    }
    if (!firstName || !lastName) error = tx('Ad/Soyad eksik')
    else if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(email)) error = tx('E-posta geçersiz')
    else if (seen.has(emailKey)) error = tx('E-posta dosyada birden fazla kez geçiyor')
    else if (!hireDate) error = tx('İşe giriş tarihi okunamadı ya da geçersiz')
    else if (departmentLabel && !deptByLabel.has(departmentLabel.toLowerCase()))
      error = tx('Departman bulunamadı: "{0}"', [departmentLabel])

    if (email) seen.add(emailKey)
    return {
      rowNumber: idx + 2, // Excel'de 1. satır başlık
      firstName,
      lastName,
      email,
      phone,
      hireDate,
      departmentLabel,
      positionTitle,
      error,
    }
  })
}

export function ImportEmployeesModal({
  open,
  onClose,
  departments,
}: {
  open: boolean
  onClose: () => void
  departments: DepartmentOption[]
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  // Mevcut çalışanların e-postaları (çalışan listesi sorgusu; çoğu zaman önbellekte).
  const employees = useEmployees({ enabled: open })

  const [rows, setRows] = useState<ParsedRow[]>([])
  const [fileName, setFileName] = useState('')
  const [parsing, setParsing] = useState(false)
  const [importing, setImporting] = useState(false)
  const [progress, setProgress] = useState(0)
  const [results, setResults] = useState<ImportResult[] | null>(null)
  const [mode, setMode] = useState<ImportMode>('file')
  const [sheet, setSheet] = useState(() => emptySheet(TEMPLATE.headers.length))
  const sheetCols = sheetColumns(TEMPLATE.headers, {
    Ad: { width: 110 },
    Soyad: { width: 110 },
    'E-posta': { placeholder: 'ad.soyad@sirket.com', width: 200 },
    Telefon: { placeholder: '05xx xxx xx xx', width: 130 },
    'İşe giriş tarihi': { placeholder: 'gg.aa.yyyy', width: 130 },
    Departman: { width: 180, options: departments.map((d) => d.label) },
    Unvan: { placeholder: tx('ör. Muhasebe Uzmanı'), width: 180 },
  })
  const sheetFilled = sheetToRecords(sheetCols, sheet).length
  const editing = mode === 'sheet' && !fileName

  function reset() {
    setRows([])
    setFileName('')
    setResults(null)
    setProgress(0)
  }

  function handleClose() {
    if (importing) return
    reset()
    setSheet(emptySheet(TEMPLATE.headers.length))
    setMode('file')
    onClose()
  }

  async function readFile(file: File) {
    await load(file.name, () => readSpreadsheet(file))
  }

  /** Tablodaki kayıtları ön izlemeye alır; tablo korunur, "Tabloyu düzenle" ile geri dönülür. */
  async function readSheet() {
    const records = sheetToRecords(sheetCols, sheet)
    setSheet(compactSheet(sheet, TEMPLATE.headers.length))
    await load(tx('Tablodan girilen {0} kayıt', [records.length]), async () => records)
  }

  async function load(name: string, read: () => Promise<Record<string, unknown>[]>) {
    setFileName(name)
    setResults(null)
    setParsing(true)
    try {
      const list = employees.data ?? (await employees.refetch()).data ?? []
      const existing = new Map(list.filter((x) => x.email).map((x) => [x.email.trim().toLowerCase(), x]))
      setRows(parseWorkbook(await read(), departments, existing))
    } catch (err) {
      toast.stop(err instanceof SpreadsheetError ? err.message : tx('Dosya okunamadı. Geçerli bir .xlsx ya da .csv dosyası seçin.'))
      setRows([])
    } finally {
      setParsing(false)
    }
  }

  const validRows = rows.filter((r) => !r.error)
  const invalidRows = rows.filter((r) => r.error)
  const deptByLabel = new Map(departments.map((d) => [d.label.trim().toLowerCase(), d.id]))

  async function handleImport() {
    setImporting(true)
    setProgress(0)
    const outcomes: ImportResult[] = []

    for (const row of validRows) {
      if (row.existing) {
        try {
          const cur = currentAssignment(row.existing)
          const deptId = row.departmentLabel ? deptByLabel.get(row.departmentLabel.toLowerCase())! : cur!.departmentId
          // Aynı başlangıç tarihli atama sunucuda yerinde güncellenir; geçmişe yeni satır açılmaz.
          await employeeApi.addAssignment(row.existing.id, {
            departmentId: deptId,
            positionTitle: row.positionTitle || cur?.positionTitle || undefined,
            effectiveFrom: cur?.effectiveFrom ?? row.existing.hireDate,
          })
          outcomes.push({ row, status: 'ok', updated: true })
        } catch (e: unknown) {
          outcomes.push({ row, status: 'failed', message: e instanceof Error ? e.message : tx('Bilinmeyen hata') })
        }
        setProgress(outcomes.length)
        continue
      }
      try {
        const employee = await employeeApi.create({
          firstName: row.firstName,
          lastName: row.lastName,
          email: row.email,
          phone: row.phone || undefined,
          hireDate: row.hireDate,
        })

        const deptId = row.departmentLabel
          ? deptByLabel.get(row.departmentLabel.toLowerCase())
          : undefined
        if (deptId) {
          try {
            await employeeApi.addAssignment(employee.id, {
              departmentId: deptId,
              positionTitle: row.positionTitle || undefined,
              effectiveFrom: row.hireDate,
            })
          } catch {
            // Kayıt oluştu, sadece atama başarısız - liste/şemadan elle tamamlanabilir.
          }
        }

        let link: string | null | undefined = null
        try {
          link = (await tenantApi.inviteMember(employee.id)).passwordLink?.link
        } catch {
          // Giriş erişimi açılamadı - kayıt yine de geçerli (Roller ekranından açılabilir).
        }

        outcomes.push({ row, status: 'ok', link })
      } catch (e: unknown) {
        outcomes.push({
          row,
          status: 'failed',
          message: e instanceof Error ? e.message : tx('Bilinmeyen hata'),
        })
      }
      setProgress(outcomes.length)
    }

    setResults(outcomes)
    setImporting(false)
    void queryClient.invalidateQueries({ queryKey: qk.employees })
    void queryClient.invalidateQueries({ queryKey: ['departments'] })

    const okCount = outcomes.filter((o) => o.status === 'ok').length
    const failCount = outcomes.length - okCount
    const updatedCount = outcomes.filter((o) => o.updated).length
    if (failCount === 0 && updatedCount === okCount) toast.ok(tx('{0} çalışan güncellendi.', [okCount]))
    else if (failCount === 0) toast.ok(tx('{0} çalışan başarıyla içe aktarıldı.', [okCount]))
    else toast.stop(tx('{0} başarılı, {1} başarısız. Detaylar aşağıda.', [okCount, failCount]))
  }

  return (
    <Modal
      open={open}
      onClose={handleClose}
      title={tx('Excel\'den çalışan içe aktar')}
      note={tx('Sütunlar: Ad, Soyad, E-posta, Telefon (isteğe bağlı), İşe giriş tarihi, Departman (isteğe bağlı), Unvan (isteğe bağlı). E-postası kayıtlı çalışanların unvanı ve departmanı güncellenir.')}
      size={mode === 'sheet' && !fileName ? 'sheet' : 'lg'}
      footer={
        <>
          <Button variant="outline" className="cursor-pointer" onClick={handleClose} disabled={importing}>
            {results ? tx('Kapat') : tx('Vazgeç')}
          </Button>
          {editing && (
            <Button className="cursor-pointer" onClick={() => void readSheet()} disabled={sheetFilled === 0}>
              {sheetFilled === 0 ? tx('Tabloya kayıt girin') : tx('{0} kaydı kontrol et', [sheetFilled])}
            </Button>
          )}
          {!results && !editing && (
            <Button
              className="cursor-pointer"
              onClick={handleImport}
              disabled={importing || validRows.length === 0}
            >
              {importing && <LoaderCircle className="size-4 animate-spin" />}
              {importing
                ? tx('İçe aktarılıyor ({0}/{1})', [progress, validRows.length])
                : validRows.length === 0
                  ? tx('İçe aktarılacak geçerli kayıt yok')
                  : tx('{0} kaydı içe aktar', [validRows.length])}
            </Button>
          )}
        </>
      }
    >
      <div className="space-y-4">
        {!fileName && (
          <ImportSource mode={mode} onModeChange={setMode} onFile={(f) => void readFile(f)} template={TEMPLATE}
            columns={sheetCols} sheet={sheet} onSheetChange={setSheet} />
        )}

        {fileName && (
          <div className="border-border flex items-center gap-2 rounded-lg border px-3 py-2 text-sm">
            {mode === 'sheet' ? <Table2 className="size-4 shrink-0" /> : <FileSpreadsheet className="size-4 shrink-0" />}
            <span className="truncate">{fileName}</span>
            {!results && !importing && (
              <button
                type="button"
                className="text-muted-foreground hover:text-foreground ml-auto cursor-pointer text-xs underline"
                onClick={reset}
              >
                {mode === 'sheet' ? tx('Tabloyu düzenle') : tx('Değiştir')}
              </button>
            )}
          </div>
        )}

        {parsing && (
          <div className="text-muted-foreground flex items-center gap-2 text-sm">
            <LoaderCircle className="size-4 animate-spin" />
            {tx('Dosya okunuyor…')}
          </div>
        )}

        {!parsing && rows.length > 0 && !results && (
          <>
            <div className="flex gap-4 text-sm">
              <span className="flex items-center gap-1 text-emerald-500">
                <CheckCircle2 className="size-4" /> {tx('{0} geçerli', [validRows.length])}</span>
              {invalidRows.length > 0 && (
                <span className="flex items-center gap-1 text-red-500">
                  <XCircle className="size-4" /> {tx('{0} hatalı (atlanacak)', [invalidRows.length])}</span>
              )}
            </div>

            <div className="border-border max-h-64 overflow-y-auto rounded-lg border">
              <table className="w-full text-left text-sm">
                <thead className="bg-white/5 text-xs uppercase">
                  <tr>
                    <th className="px-3 py-2">{tx('Satır')}</th>
                    <th className="px-3 py-2">{tx('Ad Soyad')}</th>
                    <th className="px-3 py-2">{tx('E-posta')}</th>
                    <th className="px-3 py-2">{tx('Departman')}</th>
                    <th className="px-3 py-2">{tx('Unvan')}</th>
                    <th className="px-3 py-2">{tx('Durum')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.rowNumber} className="border-border border-t">
                      <td className="text-muted-foreground px-3 py-2">{r.rowNumber}</td>
                      <td className="px-3 py-2">
                        {r.firstName} {r.lastName}
                      </td>
                      <td className="px-3 py-2">{r.email}</td>
                      <td className="px-3 py-2">{r.departmentLabel || '—'}</td>
                      <td className="px-3 py-2">{r.positionTitle || '—'}</td>
                      <td className="px-3 py-2">
                        {r.error ? (
                          <span className="text-red-500">{r.error}</span>
                        ) : r.existing ? (
                          <span className="text-sky-500">{tx('Güncellenecek')}</span>
                        ) : (
                          <span className="text-emerald-500">{tx('Hazır')}</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}

        {results && (
          <div className="space-y-2">
            <div className="flex gap-4 text-sm">
              <span className="flex items-center gap-1 text-emerald-500">
                <CheckCircle2 className="size-4" />
                {tx('{0} başarılı', [results.filter((r) => r.status === 'ok').length])}</span>
              {results.some((r) => r.status === 'failed') && (
                <span className="flex items-center gap-1 text-red-500">
                  <XCircle className="size-4" />
                  {tx('{0} başarısız', [results.filter((r) => r.status === 'failed').length])}</span>
              )}
            </div>
            {results.some((r) => r.link) && (
              <div className="space-y-1.5">
                <p className="text-[12.5px] text-muted-foreground">
                  {tx('Parola linkleri yalnızca burada gösterilir; her birini kişiye iletin. Kaybolursa Roller > Davetler listesinden yeniden üretin.')}
                </p>
                <ul className="border-border max-h-56 divide-y divide-border overflow-y-auto rounded-lg border">
                  {results.filter((r) => r.link).map((r) => (
                    <li key={r.row.rowNumber} className="flex items-center gap-3 px-3 py-2">
                      <span className="min-w-0 flex-1 truncate text-[13px]">{r.row.firstName} {r.row.lastName}</span>
                      <CopyButton value={r.link!} label={tx('Linki kopyala')} className="h-8" />
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {results.some((r) => r.status === 'failed') && (
              <div className="border-border max-h-48 overflow-y-auto rounded-lg border">
                <table className="w-full text-left text-sm">
                  <tbody>
                    {results
                      .filter((r) => r.status === 'failed')
                      .map((r) => (
                        <tr key={r.row.rowNumber} className="border-border border-t">
                          <td className="px-3 py-2 text-red-500">{tx('Satır {0} ({1} {2}): {3}', [r.row.rowNumber, r.row.firstName, r.row.lastName, r.message])}
                          </td>
                        </tr>
                      ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  )
}
