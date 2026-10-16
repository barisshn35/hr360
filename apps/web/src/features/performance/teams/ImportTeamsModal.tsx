import { useQueryClient } from '@tanstack/react-query'
import { teamsApi } from '@/api/performance/client'
import { employeeApi } from '@/api/employees'
import { BulkImportModal, cell, mapRow, type ImportRow } from '@/components/import/BulkImportModal'
import { tx } from '@/lib/i18n'
import type { DeptNode } from '../hooks'

interface TeamRow {
  name: string
  department: string
  leadEmail: string
  memberEmails: string[]
  description: string
  departmentId?: string
  leadId?: string
  memberIds: string[]
  missingMembers: string[]
}

const ALIASES: Record<string, keyof TeamRow> = {
  'ekip adı': 'name', ekip: 'name', 'ekip adi': 'name', 'takım adı': 'name',
  departman: 'department', 'bağlı departman': 'department',
  'ekip lideri e-posta': 'leadEmail', 'ekip lideri': 'leadEmail', 'lider e-posta': 'leadEmail', 'takım lideri': 'leadEmail',
  'üyeler': 'memberEmails', 'üye e-postaları': 'memberEmails', uyeler: 'memberEmails', 'üyeler (e-posta)': 'memberEmails',
  'açıklama': 'description', aciklama: 'description',
}

const key = (s: string) => s.trim().toLocaleLowerCase('tr')

/**
 * Ekipleri dosyadan oluşturur: departman adla (gerekirse "Üst › Alt" yoluyla) bulunur, lider ve üyeler
 * çalışan e-postasıyla eşlenir. Üyeler aynı hücrede virgülle ayrılır. Bulunamayan üye satırı düşürmez,
 * ekip oluşturulur ve sonuçta uyarı olarak gösterilir.
 */
export function ImportTeamsModal({ open, onClose, departments }: { open: boolean; onClose: () => void; departments: DeptNode[] }) {
  const qc = useQueryClient()

  async function parse(raw: Record<string, unknown>[]): Promise<ImportRow<TeamRow>[]> {
    const [employees, teams] = await Promise.all([employeeApi.list(), teamsApi.list({})])
    const byEmail = new Map(employees.map((e) => [key(e.email ?? ''), e.id]))
    const byPath = new Map(departments.map((d) => [key(d.path.replace(/\s*›\s*/g, ' › ')), [d.id]]))
    const byName = new Map<string, string[]>()
    for (const d of departments) byName.set(key(d.name), [...(byName.get(key(d.name)) ?? []), d.id])
    const activeTeams = new Set(teams.filter((t) => t.isActive).map((t) => `${t.departmentId}|${key(t.name)}`))
    const seen = new Set<string>()

    return raw.map((record, i) => {
      const m = mapRow(record, ALIASES)
      const emails = cell(m.memberEmails).split(/[,\n]/).map((s) => s.trim()).filter(Boolean)
      const r: TeamRow = {
        name: cell(m.name), department: cell(m.department), leadEmail: cell(m.leadEmail), memberEmails: emails,
        description: cell(m.description), memberIds: [], missingMembers: [],
      }
      const dept = key(r.department.replace(/\s*[›>]\s*/g, ' › '))
      const ids = byPath.get(dept) ?? byName.get(dept) ?? []
      r.departmentId = ids.length === 1 ? ids[0] : undefined
      r.leadId = r.leadEmail ? byEmail.get(key(r.leadEmail)) : undefined
      for (const e of emails) {
        const id = byEmail.get(key(e))
        if (id) { if (!r.memberIds.includes(id) && id !== r.leadId) r.memberIds.push(id) } else r.missingMembers.push(e)
      }

      let error: string | undefined
      const dup = `${r.departmentId}|${key(r.name)}`
      if (!r.name) error = tx('Ekip adı boş')
      else if (!r.department) error = tx('Departman boş')
      else if (ids.length === 0) error = tx('Departman bulunamadı: "{0}"', [r.department])
      else if (ids.length > 1) error = tx('"{0}" adında birden fazla departman var; "Üst › Alt" biçiminde yazın', [r.department])
      else if (activeTeams.has(dup)) error = tx('Bu departmanda aynı adla etkin ekip var')
      else if (seen.has(dup)) error = tx('Ekip dosyada birden fazla kez geçiyor')
      else if (r.leadEmail && !r.leadId) error = tx('Ekip lideri bulunamadı: "{0}"', [r.leadEmail])
      if (!error) seen.add(dup)

      const members = emails.length ? `${emails.length}${r.missingMembers.length ? ` (${tx('{0} bulunamadı', [r.missingMembers.length])})` : ''}` : ''
      return { rowNumber: i + 2, data: r, cells: [r.name, r.department, r.leadEmail, members], error }
    })
  }

  async function run(row: ImportRow<TeamRow>) {
    const r = row.data
    const team = await teamsApi.create({ name: r.name, departmentId: r.departmentId!, description: r.description || null })
    const problems: string[] = []
    if (r.leadId) {
      try { await teamsApi.setLead(team.id, r.leadId) } catch { problems.push(tx('lider atanamadı')) }
    }
    let failed = 0
    for (const id of r.memberIds) {
      try { await teamsApi.addMember(team.id, { employeeId: id }) } catch { failed++ }
    }
    if (failed) problems.push(tx('{0} üye eklenemedi', [failed]))
    if (r.missingMembers.length) problems.push(tx('bulunamayan üyeler: {0}', [r.missingMembers.join(', ')]))
    if (problems.length) return tx('"{0}" oluşturuldu; {1}', [r.name, problems.join('; ')])
  }

  return (
    <BulkImportModal<TeamRow>
      open={open}
      onClose={onClose}
      title={tx('Ekipleri içe aktar')}
      note={tx('Sütunlar: Ekip adı, Departman, Ekip lideri e-posta (isteğe bağlı), Üyeler (e-postalar, virgülle; isteğe bağlı), Açıklama (isteğe bağlı). Kişiler önceden çalışan olarak kayıtlı olmalı.')}
      template={{
        fileName: 'hr360-ekip-sablonu.csv',
        headers: ['Ekip adı', 'Departman', 'Ekip lideri e-posta', 'Üyeler', 'Açıklama'],
        example: [['Mobil', 'Yazılım Geliştirme', 'lider@sirket.com', 'uye1@sirket.com, uye2@sirket.com', 'iOS ve Android uygulamaları']],
      }}
      columns={[tx('Ekip'), tx('Departman'), tx('Lider'), tx('Üye')]}
      parse={parse}
      run={run}
      entityLabel={tx('ekip')}
      onDone={() => void qc.invalidateQueries({ queryKey: ['perf', 'teams'] })}
    />
  )
}
