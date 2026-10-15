import { apiFetch } from './client'
import type { RuleWarning } from './opsPlus'

const BASE = '/api/timeshift/shift-optimizer'

/** Talep satırı: vardiya tanımı, kişi sayısı, haftanın günleri (ISO 1 = Pazartesi; boş = her gün), isteğe bağlı beceri (ekip etiketi). */
export interface DemandRow { shiftId: string; required: number; weekdays?: number[] | null; date?: string | null; skill?: string | null }

export type DiffKind = 'add' | 'change' | 'remove' | 'same'

export interface ProposalDiffRow {
  employeeId: string
  name: string | null
  date: string
  kind: DiffKind
  currentAssignmentId: string | null
  currentShiftId: string | null
  currentShift: string | null
  proposedShiftId: string | null
  proposedShift: string | null
}

export interface Proposal {
  solver: 'cp-sat' | 'greedy'
  status: string
  seconds: number
  from: string
  to: string
  employees: Array<{ id: string; name: string }>
  shifts: Array<{ id: string; name: string; startTime: string; endTime: string }>
  diff: ProposalDiffRow[]
  uncovered: Array<{ date: string; shiftId: string; skill: string | null; missing: number }>
  unusableShifts: Array<{ shiftId: string; reason: 'daily' | 'night' }>
  fairness: { nightSpread: number; weekendSpread: number; loadSpread: number; preferenceConflicts: number }
}

export const shiftOptimizerApi = {
  propose: (body: { from: string; to: string; teamId?: string; employeeIds?: string[]; departmentId?: string; demand: DemandRow[]; solver?: 'auto' | 'greedy' }) =>
    apiFetch<Proposal>(`${BASE}/propose`, { method: 'POST', body }),
  apply: (body: { upserts: Array<{ employeeId: string; date: string; shiftId: string }>; removeAssignmentIds: string[] }) =>
    apiFetch<{ created: number; updated: number; removed: number; warnings: Array<RuleWarning & { employeeId: string; date: string }> }>(`${BASE}/apply`, { method: 'POST', body }),
}

/** Uygulanacak satırları ayırır (saf; birim testli): add/change → upsert, remove → kaldırma; same atlanır. */
export function applyBody(rows: ReadonlyArray<ProposalDiffRow>, selected: ReadonlySet<string>, key: (r: ProposalDiffRow) => string) {
  const upserts: Array<{ employeeId: string; date: string; shiftId: string }> = []
  const removeAssignmentIds: string[] = []
  for (const r of rows) {
    if (!selected.has(key(r))) continue
    if ((r.kind === 'add' || r.kind === 'change') && r.proposedShiftId) upserts.push({ employeeId: r.employeeId, date: r.date, shiftId: r.proposedShiftId })
    else if (r.kind === 'remove' && r.currentAssignmentId) removeAssignmentIds.push(r.currentAssignmentId)
  }
  return { upserts, removeAssignmentIds }
}
