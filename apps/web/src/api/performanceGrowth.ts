/** Dalga 11 uçları: kalibrasyon oturumu (79), OKR hizalama ağacı (80), anonim 360 (81). */
import { apiFetch, qs } from './client'

const BASE = '/api/performance'

/* ------------------------------------------------------------ kalibrasyon */

export type CalibrationStatus = 'Open' | 'Finalized' | 'Cancelled'

export interface CalibrationSessionRow {
  id: string
  cycleId: string
  name: string
  status: CalibrationStatus
  createdBy: string
  createdAt: string
  finalizedBy: string | null
  finalizedAt: string | null
  total: number
  confirmed: number
}

export interface CalibrationItem {
  employeeId: string
  name: string
  department: string | null
  positionTitle: string | null
  score: number | null
  performanceBand: number
  potentialBand: number
  cell: number
  originalCell: number
  decisionNote: string | null
  confirmed: boolean
  confirmedBy: string | null
  confirmedAt: string | null
  updatedAt: string
  isSelf: boolean
}

export interface CalibrationChange {
  id: string
  employeeId: string
  name: string | null
  action: 'Moved' | 'Confirmed' | 'Unconfirmed' | 'Finalized'
  fromCell: number | null
  toCell: number | null
  note: string | null
  changedBy: string
  changedAt: string
}

export interface CalibrationDetail {
  notice: string
  session: { id: string; cycleId: string; cycleName: string | null; name: string; status: CalibrationStatus; createdBy: string; createdAt: string; finalizedBy: string | null; finalizedAt: string | null }
  cells: { cell: number; label: string; performanceBand: number; potentialBand: number }[]
  items: CalibrationItem[]
  changes: CalibrationChange[]
}

export const calibrationApi = {
  list: (cycleId: string, signal?: AbortSignal) => apiFetch<CalibrationSessionRow[]>(`${BASE}/calibration/sessions${qs({ cycleId })}`, { signal }),
  create: (body: { cycleId: string; name: string }) =>
    apiFetch<{ id: string; placed: number; unplaced: number }>(`${BASE}/calibration/sessions`, { method: 'POST', body }),
  get: (id: string, signal?: AbortSignal) => apiFetch<CalibrationDetail>(`${BASE}/calibration/sessions/${id}`, { signal }),
  move: (id: string, body: { employeeId: string; performanceBand: number; potentialBand: number; note: string }) =>
    apiFetch<{ cell: number; label: string }>(`${BASE}/calibration/sessions/${id}/move`, { method: 'POST', body }),
  confirm: (id: string, body: { employeeIds: string[]; confirmed: boolean }) =>
    apiFetch<{ changed: number }>(`${BASE}/calibration/sessions/${id}/confirm`, { method: 'POST', body }),
  finalize: (id: string) => apiFetch<{ changed: number; employees: number }>(`${BASE}/calibration/sessions/${id}/finalize`, { method: 'POST' }),
  cancel: (id: string) => apiFetch<{ status: string }>(`${BASE}/calibration/sessions/${id}/cancel`, { method: 'POST' }),
}

/* ------------------------------------------------------------ OKR */

export type OkrKind = 'Company' | 'Department' | 'Goal'

export interface OkrNodeView {
  id: string
  kind: OkrKind
  title: string
  description?: string | null
  weight: number
  parentId?: string | null
  departmentId?: string | null
  departmentName?: string | null
  progress: number | null
  progressHidden?: boolean
  people?: number
  hiddenGoals?: number
  canEdit?: boolean
  status?: string
  employeeId?: string
  employeeName?: string | null
  children: OkrNodeView[]
}

export interface OkrTree {
  cycle: { id: string; name: string; status: string }
  roots: OkrNodeView[]
  unaligned: { id: string; title: string; weight: number; employeeId: string; employeeName: string | null; progress: number | null; status: string }[]
  canEditCompany: boolean
  canAlign: boolean
  editableDepartments: { id: string; name: string }[]
  minGroup: number
  objectives: { id: string; title: string; level: 'Company' | 'Department'; departmentId: string | null }[]
}

export interface ObjectiveInput {
  cycleId: string
  level: 'Company' | 'Department'
  departmentId?: string | null
  parentId?: string | null
  title: string
  description?: string | null
  weight: number
}

export const okrApi = {
  tree: (cycleId: string, signal?: AbortSignal) => apiFetch<OkrTree>(`${BASE}/okr/tree${qs({ cycleId })}`, { signal }),
  create: (body: ObjectiveInput) => apiFetch<{ id: string }>(`${BASE}/okr/objectives`, { method: 'POST', body }),
  update: (id: string, body: ObjectiveInput) => apiFetch<{ id: string }>(`${BASE}/okr/objectives/${id}`, { method: 'PUT', body }),
  remove: (id: string) => apiFetch<void>(`${BASE}/okr/objectives/${id}`, { method: 'DELETE' }),
  align: (goalId: string, parentObjectiveId: string | null) =>
    apiFetch<{ id: string }>(`${BASE}/okr/goals/${goalId}/parent`, { method: 'PUT', body: { parentObjectiveId } }),
}

/* ------------------------------------------------------------ 360 */

export interface F360Competency { key: string; label: string }
export type F360Relationship = 'Manager' | 'Peer' | 'DirectReport' | 'Other'

export interface F360Invitation {
  id: string
  title: string
  subjectName: string | null
  dueDate: string | null
  competencies: F360Competency[]
  submitted: boolean
  relationship: F360Relationship
}

export interface F360ManagedRow {
  id: string
  title: string
  subjectEmployeeId: string
  subjectName: string | null
  status: 'Open' | 'Closed'
  dueDate: string | null
  releasedToSubject: boolean
  createdBy: string
  createdAt: string
  closedAt: string | null
  minResponses: number
  invited: number
  submitted: number
}

export interface F360Detail extends Omit<F360ManagedRow, 'invited' | 'submitted'> {
  competencies: F360Competency[]
  canRelease: boolean
  participants: { reviewerEmployeeId: string; name: string | null; relationship: F360Relationship; submitted: boolean }[]
}

export interface F360Results {
  id: string
  title: string
  subjectName: string | null
  status: string
  closed: boolean
  hidden: boolean
  responses: number
  threshold: number
  competencies: { key: string; label: string; ratings: number; average: number | null; distribution: number[] | null; hidden: boolean }[]
  comments: string[]
  notice: string
}

export interface F360CreateInput {
  subjectEmployeeId: string
  title: string
  cycleId?: string | null
  dueDate?: string | null
  competencies?: F360Competency[]
  reviewers: { employeeId: string; relationship: F360Relationship }[]
}

export const f360Api = {
  mine: (signal?: AbortSignal) => apiFetch<F360Invitation[]>(`${BASE}/feedback360/mine`, { signal }),
  respond: (id: string, body: { ratings: Record<string, number>; comment?: string }) =>
    apiFetch<{ submitted: boolean }>(`${BASE}/feedback360/${id}/respond`, { method: 'POST', body }),
  managed: (signal?: AbortSignal) => apiFetch<F360ManagedRow[]>(`${BASE}/feedback360/managed`, { signal }),
  get: (id: string, signal?: AbortSignal) => apiFetch<F360Detail>(`${BASE}/feedback360/${id}`, { signal }),
  create: (body: F360CreateInput) => apiFetch<{ id: string; reviewers: number }>(`${BASE}/feedback360`, { method: 'POST', body }),
  close: (id: string) => apiFetch<{ status: string }>(`${BASE}/feedback360/${id}/close`, { method: 'POST' }),
  release: (id: string, released: boolean) =>
    apiFetch<{ releasedToSubject: boolean }>(`${BASE}/feedback360/${id}/release`, { method: 'POST', body: { released } }),
  remove: (id: string) => apiFetch<void>(`${BASE}/feedback360/${id}`, { method: 'DELETE' }),
  results: (id: string, signal?: AbortSignal) => apiFetch<F360Results>(`${BASE}/feedback360/${id}/results`, { signal }),
  myResults: (signal?: AbortSignal) => apiFetch<{ id: string; title: string; closedAt: string | null }[]>(`${BASE}/feedback360/my-results`, { signal }),
}

/** Sürükle-bırak için 9-kutu hücre ↔ bantlar (hücre = (potansiyel − 1) × 3 + performans). */
export const cellOf = (performanceBand: number, potentialBand: number) => (potentialBand - 1) * 3 + performanceBand
export const bandsOf = (cell: number) => ({ performanceBand: ((cell - 1) % 3) + 1, potentialBand: Math.floor((cell - 1) / 3) + 1 })

/** OKR ağacını düzleştirir (derinlik + görünürlük için açık düğümler). */
export function flattenOkr(roots: OkrNodeView[], collapsed: ReadonlySet<string>): { node: OkrNodeView; depth: number }[] {
  const out: { node: OkrNodeView; depth: number }[] = []
  const walk = (n: OkrNodeView, depth: number) => {
    out.push({ node: n, depth })
    if (!collapsed.has(n.id)) n.children.forEach((c) => walk(c, depth + 1))
  }
  roots.forEach((r) => walk(r, 0))
  return out
}
