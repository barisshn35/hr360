import { cell, mapRow, type ImportRow } from '@/components/import/BulkImportModal'
import { tx } from '@/lib/i18n'

export interface DeptRow { name: string; parent: string; headEmail: string; headId?: string }

const ALIASES: Record<string, keyof DeptRow> = {
  'departman adı': 'name', departman: 'name', ad: 'name', 'departman adi': 'name',
  'üst departman': 'parent', 'bağlı olduğu departman': 'parent', 'ust departman': 'parent', 'üst birim': 'parent',
  'departman başı e-posta': 'headEmail', 'departman başı': 'headEmail', 'yönetici e-posta': 'headEmail', 'departman basi e-posta': 'headEmail',
}

export const deptKey = (s: string) => s.trim().toLocaleLowerCase('tr')
const key = deptKey

/** Dosyada departman başı sütunu dolu mu (çalışan listesi yalnızca gerekirse çekilir)? */
export const needsHeads = (raw: Record<string, unknown>[]) => raw.some((r) => cell(mapRow(r, ALIASES).headEmail))

/**
 * Departman satırlarını doğrular ve üst departman önce oluşacak şekilde sıralar. `existing`: şirketteki
 * departman adları (deptKey), `emails`: çalışan e-postası (deptKey) → çalışan kimliği.
 */
export function planDepartmentImport(raw: Record<string, unknown>[], existing: Set<string>, emails: Map<string, string>): ImportRow<DeptRow>[] {
  const rows: ImportRow<DeptRow>[] = raw.map((record, i) => {
    const m = mapRow(record, ALIASES)
    const d: DeptRow = { name: cell(m.name), parent: cell(m.parent), headEmail: cell(m.headEmail) }
    d.headId = d.headEmail ? emails.get(key(d.headEmail)) : undefined
    return { rowNumber: i + 2, data: d, cells: [d.name, d.parent, d.headEmail] }
  })

  const inFile = new Map<string, ImportRow<DeptRow>>()
  for (const r of rows) {
    const d = r.data
    if (!d.name) r.error = tx('Departman adı boş')
    else if (existing.has(key(d.name))) r.error = tx('Bu adla departman zaten var')
    else if (inFile.has(key(d.name))) r.error = tx('Departman dosyada birden fazla kez geçiyor')
    else if (d.parent && key(d.parent) === key(d.name)) r.error = tx('Departman kendisinin üst departmanı olamaz')
    else if (d.headEmail && !d.headId) r.error = tx('Departman başı bulunamadı: "{0}"', [d.headEmail])
    if (d.name && !r.error) inFile.set(key(d.name), r)
  }
  for (const r of rows) {
    if (!r.error && r.data.parent && !existing.has(key(r.data.parent)) && !inFile.has(key(r.data.parent)))
      r.error = tx('Üst departman bulunamadı: "{0}"', [r.data.parent])
  }

  // Derinlik: dosyadaki üst zincir boyunca; döngüdeki satırlar hatalı.
  const depth = new Map<ImportRow<DeptRow>, number>()
  const depthOf = (r: ImportRow<DeptRow>, seen: Set<ImportRow<DeptRow>>): number => {
    if (depth.has(r)) return depth.get(r)!
    const p = r.data.parent ? inFile.get(key(r.data.parent)) : undefined
    if (!p) { depth.set(r, 0); return 0 }
    if (seen.has(p)) { r.error = tx('Üst departman zinciri döngü oluşturuyor'); depth.set(r, 0); return 0 }
    seen.add(r)
    const v = depthOf(p, seen) + 1
    if (p.error && !r.error) r.error = tx('Üst departman satırı hatalı')
    depth.set(r, v)
    return v
  }
  for (const r of rows) if (!r.error) depthOf(r, new Set([r]))
  return [...rows].sort((a, b) => (depth.get(a) ?? 0) - (depth.get(b) ?? 0) || a.rowNumber - b.rowNumber)
}
