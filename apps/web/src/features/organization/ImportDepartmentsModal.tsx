import { useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { organizationApi } from '@/api/organization'
import { employeeApi } from '@/api/employees'
import type { Department } from '@/api/types'
import { BulkImportModal, type ImportRow } from '@/components/import/BulkImportModal'
import { deptKey as key, needsHeads, planDepartmentImport, type DeptRow } from './departmentImport'
import { tx } from '@/lib/i18n'

/**
 * Bir şirketin departmanlarını dosyadan oluşturur. Üst departman mevcut departmanlardan ya da dosyadaki
 * başka bir satırdan olabilir: satırlar üst departman önce oluşacak şekilde sıralanır, döngü hatalıdır.
 * Departman başı çalışan e-postasıyla eşlenir (çalışanlar önceden kayıtlı olmalı).
 */
export function ImportDepartmentsModal({ open, onClose, companyId, departments }: {
  open: boolean
  onClose: () => void
  companyId: string
  departments: Department[]
}) {
  const qc = useQueryClient()
  // İçe aktarma sırasında oluşturulanlar: sonraki satırlar üst departmanı buradan bulur.
  const created = useRef(new Map<string, string>())

  async function parse(raw: Record<string, unknown>[]): Promise<ImportRow<DeptRow>[]> {
    created.current = new Map()
    const existing = new Map(departments.filter((d) => d.companyId === companyId).map((d) => [key(d.name), d.id]))
    const emails = needsHeads(raw)
      ? new Map((await employeeApi.list()).map((e) => [key(e.email ?? ''), e.id]))
      : new Map<string, string>()

    return planDepartmentImport(raw, new Set(existing.keys()), emails)
  }

  async function run(row: ImportRow<DeptRow>) {
    const d = row.data
    const existing = departments.find((x) => x.companyId === companyId && d.parent && key(x.name) === key(d.parent))
    const parentId = d.parent ? existing?.id ?? created.current.get(key(d.parent)) : null
    if (d.parent && !parentId) throw new Error(tx('Üst departman oluşturulamadığı için atlandı'))
    const dept = await organizationApi.createDepartment({ name: d.name, companyId, parentDepartmentId: parentId ?? null })
    created.current.set(key(d.name), dept.id)
    if (d.headId) {
      try { await organizationApi.updateDepartment(dept.id, d.name, d.headId) }
      catch { return tx('Departman oluşturuldu, departman başı atanamadı') }
    }
  }

  return (
    <BulkImportModal<DeptRow>
      open={open}
      onClose={onClose}
      title={tx('Departmanları içe aktar')}
      note={tx('Sütunlar: Departman adı, Üst departman (isteğe bağlı), Departman başı e-posta (isteğe bağlı). Üst departman bu şirkette var olan ya da dosyadaki bir departman olabilir.')}
      template={{
        fileName: 'hr360-departman-sablonu.csv',
        headers: ['Departman adı', 'Üst departman', 'Departman başı e-posta'],
        example: [['Teknoloji', '', ''], ['Yazılım Geliştirme', 'Teknoloji', 'ornek.yonetici@sirket.com'], ['Altyapı', 'Teknoloji', '']],
      }}
      columns={[tx('Departman'), tx('Üst departman'), tx('Departman başı')]}
      parse={parse}
      run={run}
      entityLabel={tx('departman')}
      onDone={() => { void qc.invalidateQueries({ queryKey: ['departments'] }); void qc.invalidateQueries({ queryKey: ['companies'] }) }}
    />
  )
}
