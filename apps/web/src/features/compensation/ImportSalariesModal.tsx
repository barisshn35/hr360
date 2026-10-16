import { useQueryClient } from '@tanstack/react-query'
import { compensationApi, type CompensationChangeReason, type CompensationRecord } from '@/api/compensation'
import { employeeApi } from '@/api/employees'
import { BulkImportModal, cell, mapRow, type ImportRow } from '@/components/import/BulkImportModal'
import { formatMoney } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

interface SalaryRow {
  email: string
  employeeId?: string
  name?: string
  salary: number
  currency: string
  effectiveFrom: string
  reason: CompensationChangeReason
  grade: string
  note: string
  current?: CompensationRecord
}

const ALIASES: Record<string, keyof SalaryRow | 'salaryText' | 'dateText' | 'reasonText'> = {
  'e-posta': 'email', eposta: 'email', email: 'email',
  'brüt ücret': 'salaryText', ücret: 'salaryText', 'yeni ücret': 'salaryText', maaş: 'salaryText', 'brüt maaş': 'salaryText',
  'para birimi': 'currency', 'geçerlilik tarihi': 'dateText', 'başlangıç tarihi': 'dateText', tarih: 'dateText',
  gerekçe: 'reasonText', kademe: 'grade', not: 'note',
}

const REASONS: Record<string, CompensationChangeReason> = {
  'yıllık zam': 'AnnualIncrease', zam: 'AnnualIncrease', terfi: 'Promotion', 'piyasa düzeltmesi': 'MarketAdjustment',
  'işe giriş': 'Hire', 'görev düşürme': 'Demotion', diğer: 'Other',
}

const key = (s: string) => s.trim().toLocaleLowerCase('tr')

/** "1.234,56" / "1234.56" / "45000" → sayı. */
export function parseAmount(v: string): number | null {
  const s = v.replace(/\s|₺|TL|TRY/gi, '')
  if (!s) return null
  const normalized = s.includes(',') ? s.replace(/\./g, '').replace(',', '.') : /^\d{1,3}(\.\d{3})+$/.test(s) ? s.replace(/\./g, '') : s
  const n = Number(normalized)
  return Number.isFinite(n) ? n : null
}

/** gg.aa.yyyy ya da yyyy-aa-gg → yyyy-aa-gg. */
export function parseDate(v: string): string | null {
  const m = /^(\d{1,2})[./](\d{1,2})[./](\d{4})$/.exec(v)
  const iso = m ? `${m[3]}-${m[2].padStart(2, '0')}-${m[1].padStart(2, '0')}` : /^\d{4}-\d{2}-\d{2}$/.test(v) ? v : null
  if (!iso) return null
  const [y, mo, da] = iso.split('-').map(Number)
  const d = new Date(Date.UTC(y, mo - 1, da))
  return d.getUTCFullYear() === y && d.getUTCMonth() === mo - 1 && d.getUTCDate() === da ? iso : null
}

/**
 * Toplu ücret güncelleme (yenilik haritası #15): zam döneminde ücretleri dosyadan ya da tablodan girer.
 * Ön izleme mevcut ve yeni ücreti, değişim yüzdesini gösterir; her satır ayrı ücret kaydı olarak
 * (önceki açık kayıt kapanarak) oluşturulur.
 */
export function ImportSalariesModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient()

  async function parse(raw: Record<string, unknown>[]): Promise<ImportRow<SalaryRow>[]> {
    const [employees, records] = await Promise.all([employeeApi.list(), compensationApi.listRecords()])
    const byEmail = new Map(employees.map((e) => [key(e.email ?? ''), e]))
    const current = new Map<string, CompensationRecord>()
    for (const r of records) if (!r.effectiveTo) {
      const prev = current.get(r.employeeId)
      if (!prev || prev.effectiveFrom < r.effectiveFrom) current.set(r.employeeId, r)
    }
    const seen = new Set<string>()
    return raw.map((rec, i) => {
      const m = mapRow(rec, ALIASES)
      const email = cell(m.email)
      const emp = byEmail.get(key(email))
      const salary = parseAmount(cell(m.salaryText))
      const date = parseDate(cell(m.dateText))
      const reasonText = key(cell(m.reasonText) || 'yıllık zam')
      const cur = emp ? current.get(emp.id) : undefined
      const r: SalaryRow = {
        email, employeeId: emp?.id, name: emp ? `${emp.firstName} ${emp.lastName}` : undefined, salary: salary ?? 0,
        currency: (cell(m.currency) || cur?.currency || 'TRY').toUpperCase(), effectiveFrom: date ?? '', reason: REASONS[reasonText] ?? 'Other',
        grade: cell(m.grade) || cur?.grade || '', note: cell(m.note), current: cur,
      }
      let error: string | undefined
      if (!emp) error = tx('Çalışan bulunamadı: "{0}"', [email])
      else if (seen.has(emp.id)) error = tx('Çalışan dosyada birden fazla kez geçiyor')
      else if (!salary || salary <= 0) error = tx('Ücret okunamadı')
      else if (!date) error = tx('Geçerlilik tarihi okunamadı')
      else if (cur && date <= cur.effectiveFrom) error = tx('Tarih mevcut kaydın başlangıcından ({0}) sonra olmalı', [cur.effectiveFrom])
      if (emp) seen.add(emp.id)
      const pct = cur && salary ? ((salary - cur.baseSalary) / cur.baseSalary) * 100 : null
      return {
        rowNumber: i + 2, data: r, error,
        cells: [
          r.name ?? email,
          cur ? formatMoney(cur.baseSalary, cur.currency) : '—',
          salary ? formatMoney(salary, r.currency) : '—',
          pct == null ? '—' : <span className={cn('tabular-nums', pct < 0 && 'text-destructive')}>{pct >= 0 ? '+' : ''}{pct.toFixed(1)}%</span>,
          date ?? '—',
        ],
      }
    })
  }

  async function run(row: ImportRow<SalaryRow>) {
    const r = row.data
    await compensationApi.createRecord({
      employeeId: r.employeeId!, baseSalary: r.salary, currency: r.currency, grade: r.grade || undefined,
      reason: r.reason, effectiveFrom: r.effectiveFrom, note: r.note || undefined,
    })
  }

  return (
    <BulkImportModal<SalaryRow>
      open={open}
      onClose={onClose}
      title={tx('Toplu ücret güncelleme')}
      note={tx('Sütunlar: E-posta, Brüt ücret, Geçerlilik tarihi, Gerekçe (Yıllık zam, Terfi, Piyasa düzeltmesi, Diğer; boşsa yıllık zam), Para birimi ve Kademe (boşsa mevcut kayıttan), Not. Her satır yeni ücret kaydı açar, öncekini kapatır.')}
      template={{
        fileName: 'hr360-toplu-ucret-sablonu.csv',
        headers: ['E-posta', 'Brüt ücret', 'Geçerlilik tarihi', 'Gerekçe', 'Para birimi', 'Kademe', 'Not'],
        example: [['ornek@sirket.com', '65000', '01.01.2027', 'Yıllık zam', 'TRY', '', '']],
      }}
      sheetHints={{
        'E-posta': { placeholder: 'ad.soyad@sirket.com', width: 220 },
        'Brüt ücret': { placeholder: '65000', width: 120 },
        'Geçerlilik tarihi': { placeholder: 'gg.aa.yyyy', width: 130 },
        Gerekçe: { width: 150, options: ['Yıllık zam', 'Terfi', 'Piyasa düzeltmesi', 'Diğer'] },
        'Para birimi': { placeholder: 'TRY', width: 90 },
      }}
      columns={[tx('Çalışan'), tx('Mevcut'), tx('Yeni'), tx('Değişim'), tx('Geçerlilik')]}
      parse={parse}
      run={run}
      entityLabel={tx('ücret kaydı')}
      onDone={() => void qc.invalidateQueries({ queryKey: ['compensation'] })}
    />
  )
}
