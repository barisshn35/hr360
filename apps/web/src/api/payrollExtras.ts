import { apiFetch, qs } from './client'
import { downloadAuthed } from './engagement'

const BASE = '/api/compensation/compensation'

export type ExportKind = 'SgkAphb' | 'SgkHires' | 'Bank' | 'Accounting'
export interface PayrollExportRow {
  id: string
  kind: ExportKind
  fileName: string
  rowCount: number
  singleUse: boolean
  createdBy: string
  createdAt: string
  expiresAt: string
  downloadedAt: string | null
  downloadedBy: string | null
  downloadCount: number
  available: boolean
  purgedAt: string | null
  /** Bordro dalgası 8: biçim/şablon ve dosyanın SHA-256 özeti. */
  format?: string | null
  contentSha256?: string | null
}

export type AdvanceStatus = 'Pending' | 'Approved' | 'Rejected' | 'Closed' | 'Cancelled'
export interface SalaryAdvance {
  id: string
  employeeId: string
  kind: 'Advance' | 'Loan'
  amount: number
  installments: number
  startYear: number
  startMonth: number
  reason: string | null
  status: AdvanceStatus
  repaidAmount: number
  remaining: number
  installment: number
  decidedBy: string | null
  decisionNote: string | null
  decidedAt: string | null
  createdAt: string
  installmentShare: number | null
}

export interface BenefitOption { id: string; name: string; category: string; annualCost: number; description: string | null; isActive: boolean }
export interface BenefitsState {
  plan: { id: string; year: number; budgetPerEmployee: number; windowStart: string; windowEnd: string; open: boolean } | null
  options: BenefitOption[]
  election: { optionIds: string[]; total: number; updatedAt: string } | null
  summary: { elections: number; total: number; byOption: { id: string; name: string; count: number }[] } | null
}

export interface RaiseCycle { id: string; name: string; year: number; budgetPercent: number; effectiveDate: string; status: 'Draft' | 'Open' | 'Closed'; appliedAt: string | null }
export interface WorksheetRow {
  employeeId: string
  name: string
  department: string | null
  grade: string | null
  currentSalary: number
  currency: string
  band: { minAmount: number; midAmount: number; maxAmount: number } | null
  compaRatio: number | null
  proposal: { id: string; proposedPercent: number; proposedSalary: number; note: string | null; status: 'Proposed' | 'Approved' | 'Rejected' | 'Applied'; proposedBy: string; decidedBy: string | null } | null
  outOfBand: boolean
}
export interface Worksheet { cycle: RaiseCycle; rows: WorksheetRow[]; budget: number; used: number }

export const payrollExtrasApi = {
  exports: (periodId: string, signal?: AbortSignal) => apiFetch<PayrollExportRow[]>(`${BASE}/payroll/periods/${periodId}/exports`, { signal }),
  createExport: (periodId: string, body: { kind: ExportKind; format?: string; payDate?: string }) =>
    apiFetch<{ id: string; fileName: string; rowCount: number; singleUse: boolean; warnings: string[]; contentSha256?: string }>(`${BASE}/payroll/periods/${periodId}/exports`, { method: 'POST', body }),
  download: (id: string, fileName: string) => downloadAuthed(`${BASE}/payroll/exports/${id}/download`, fileName),

  advances: (status?: AdvanceStatus, signal?: AbortSignal) => apiFetch<SalaryAdvance[]>(`${BASE}/advances${qs({ status })}`, { signal }),
  requestAdvance: (body: { kind: 'Advance' | 'Loan'; amount: number; installments: number; reason?: string; employeeId?: string }) =>
    apiFetch<SalaryAdvance>(`${BASE}/advances`, { method: 'POST', body }),
  decideAdvance: (id: string, approve: boolean, note?: string) => apiFetch<SalaryAdvance>(`${BASE}/advances/${id}/decide`, { method: 'POST', body: { approve, note } }),
  cancelAdvance: (id: string) => apiFetch<SalaryAdvance>(`${BASE}/advances/${id}/cancel`, { method: 'POST' }),

  benefits: (year: number, signal?: AbortSignal) => apiFetch<BenefitsState>(`${BASE}/benefits/${year}`, { signal }),
  savePlan: (body: { year: number; budgetPerEmployee: number; windowStart: string; windowEnd: string }) => apiFetch<unknown>(`${BASE}/benefits/plan`, { method: 'PUT', body }),
  addOption: (year: number, body: Omit<BenefitOption, 'id'>) => apiFetch<BenefitOption>(`${BASE}/benefits/${year}/options`, { method: 'POST', body }),
  updateOption: (id: string, body: Omit<BenefitOption, 'id'>) => apiFetch<BenefitOption>(`${BASE}/benefits/options/${id}`, { method: 'PUT', body }),
  elect: (year: number, optionIds: string[]) => apiFetch<{ total: number; remaining: number }>(`${BASE}/benefits/${year}/election`, { method: 'PUT', body: { optionIds } }),

  cycles: (signal?: AbortSignal) => apiFetch<RaiseCycle[]>(`${BASE}/raise-cycles`, { signal }),
  createCycle: (body: { name: string; year: number; budgetPercent: number; effectiveDate: string }) => apiFetch<{ id: string }>(`${BASE}/raise-cycles`, { method: 'POST', body }),
  deleteCycle: (id: string) => apiFetch<void>(`${BASE}/raise-cycles/${id}`, { method: 'DELETE' }),
  setCycleStatus: (id: string, status: RaiseCycle['status']) => apiFetch<unknown>(`${BASE}/raise-cycles/${id}/status`, { method: 'POST', body: { status } }),
  worksheet: (id: string, signal?: AbortSignal) => apiFetch<Worksheet>(`${BASE}/raise-cycles/${id}/worksheet`, { signal }),
  propose: (id: string, body: { employeeId: string; proposedPercent: number; note?: string }) => apiFetch<unknown>(`${BASE}/raise-cycles/${id}/proposals`, { method: 'PUT', body }),
  decideProposals: (id: string, proposalIds: string[], approve: boolean) => apiFetch<{ decided: number }>(`${BASE}/raise-cycles/${id}/decide`, { method: 'POST', body: { proposalIds, approve } }),
  applyCycle: (id: string) => apiFetch<{ applied: number }>(`${BASE}/raise-cycles/${id}/apply`, { method: 'POST' }),
  cycleSummary: (id: string, signal?: AbortSignal) =>
    apiFetch<{ groups: { department: string; count: number; avgPercent: number | null; hidden: boolean }[]; overallAvg: number | null; count: number }>(`${BASE}/raise-cycles/${id}/summary`, { signal }),
}
