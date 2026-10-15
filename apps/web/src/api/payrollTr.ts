import { apiFetch, qs } from './client'

/** Bordro dalgası 8 (madde 58–65) uçları. Gateway öneki ile controller yolu üst üste bindiği için "compensation" iki kez geçer. */
const BASE = '/api/compensation/compensation'

export interface MissingDayCode { leaveType: string; code: string; reducesPay: boolean }
export interface SgkSettings {
  defaultDocumentType: string
  defaultLawNo: string
  workplaceRegistryNo: string | null
  missingDayCodes: MissingDayCode[]
  exitReasonCodes: Record<string, string>
}
export interface AccountMap { salary: string; employerSgk: string; netPayable: string; incomeTax: string; stampTax: string; sgk: string; advances: string }
export type BankTemplate = 'generic' | 'ornek-a' | 'ornek-b' | 'custom'
export interface BankCustomLayout {
  delimiter: string
  decimalSeparator: string
  header: boolean
  totalsLine: boolean
  columns: string[]
  encoding: 'utf-8' | 'utf-8-bom' | 'iso-8859-9' | 'windows-1254'
  ascii: boolean
  dateFormat: string
}
export interface BankSettings { template: BankTemplate; description: string; debitIban: string | null; companyCode: string | null; custom: BankCustomLayout }
export interface PayrollSettings {
  sgk: SgkSettings
  accounts: AccountMap
  costCenters: Record<string, string>
  bank: BankSettings
  updatedBy: string | null
  updatedAt: string | null
  bankTemplates: BankTemplate[]
  bankFields: string[]
}

export interface EmployeeSgk { employeeId: string; occupationCode: string | null; documentType: string | null; lawNo: string | null; sgdp: boolean; updatedBy?: string | null; updatedAt?: string | null }

export interface SgkIssue { employeeId: string; name: string; level: 'error' | 'warning'; code: string; message: string }
export interface SgkValidation {
  periodId: string
  year: number
  month: number
  payslips: number
  included: number
  errors: number
  warnings: number
  totals: { pek: number; days: number }
  documents: { documentType: string; lawNo: string; count: number; pek: number }[]
  issues: SgkIssue[]
}

export interface RetroCandidate {
  employeeId: string
  sourcePeriodId: string
  year: number
  month: number
  oldBase: number
  newBase: number
  oldGross: number
  newGross: number
  alreadyPaid: number
  diffGross: number
  estimatedNetDiff: number
  label: string
  applicable: boolean
}
export interface RetroDiff {
  id: string
  employeeId: string
  sourcePeriodId: string
  sourceYear: number
  sourceMonth: number
  oldBase: number
  newBase: number
  diffGross: number
  status: 'Approved' | 'Cancelled'
  targetPeriodId: string
  createdByName: string | null
  createdAt: string
}

export type ExitReason = 'Resignation' | 'Termination' | 'Retirement' | 'ContractEnd' | 'Other'
export interface SeveranceRequest {
  employeeId: string
  offboardingCaseId?: string | null
  lastWorkingDay?: string | null
  reason?: ExitReason | null
  regularAdditionsMonthly?: number | null
  otherBenefitsMonthly?: number | null
  severanceEligible?: boolean | null
  noticePaid?: boolean | null
  unusedLeaveDays?: number | null
}
export interface SeveranceResult {
  tenureDays: number
  tenureYears: number
  dressedMonthlyGross: number
  severanceBasis: number
  ceilingApplied: boolean
  severanceEligible: boolean
  severanceGross: number
  severanceStampTax: number
  severanceNet: number
  noticeWeeks: number
  noticePaid: boolean
  noticeGross: number
  noticeIncomeTax: number
  noticeStampTax: number
  noticeNet: number
  unusedLeaveDays: number
  unusedLeaveGross: number
  totalNet: number
}
export interface SeverancePreview {
  input: {
    hireDate: string
    lastWorkingDay: string
    reason: ExitReason
    monthlyBaseGross: number
    regularAdditionsMonthly: number
    otherBenefitsMonthly: number
    severanceCeiling: number
    unusedLeaveDays: number
    priorCumulativeTaxBase: number
  }
  result: SeveranceResult
  sources: { suggestedAdditions: number; payslipsUsed: number; ceiling: number; ceilingFromParameters: boolean; remainingLeave: number; priorCumulativeTaxBase?: number; openingTaxBase?: number; currency: string; employee: string }
  basis: { severance: string; notice: string; leave: string }
  disclaimer: string
}
export interface SeveranceCalc {
  id: string
  employeeId: string
  offboardingCaseId: string | null
  hireDate: string
  lastWorkingDay: string
  reason: ExitReason
  totalNet: number
  status: 'Draft' | 'Approved' | 'Rejected'
  preparedByName: string | null
  decidedByName: string | null
  decisionNote: string | null
  decidedAt: string | null
  createdAt: string
  detail: SeverancePreview
}

export type Integrity = 'ok' | 'changed' | 'missing' | 'not_published'
export interface EPayslipStatus {
  payslips: number
  published: number
  opened: number
  acknowledged: number
  items: { employeeId: string; payslipId: string; publishedAt: string; publishedBy: string | null; emailQueued: boolean; firstOpenedAt: string | null; lastOpenedAt: string | null; openCount: number; acknowledgedAt: string | null; integrity: Integrity }[]
}
export interface MyEPayslip {
  published: boolean
  publishedAt: string | null
  contentSha256: string | null
  currentSha256: string
  integrity: Integrity
  sealVerified: boolean | null
  firstOpenedAt: string | null
  acknowledgedAt: string | null
  canAcknowledge: boolean
}

export type BandPosition = 'below' | 'within' | 'above' | 'none'
export interface CompaRow {
  employeeId: string
  name: string
  department: string | null
  positionTitle: string | null
  grade: string | null
  salary: number
  currency: string
  compaRatio: number | null
  position: BandPosition
  band: { id: string; grade: string; title: string | null; minAmount: number; midAmount: number; maxAmount: number; currency: string; year: number; effectiveFrom: string | null } | null
}
export interface CoverageGroup { key: string; count: number; below: number | null; within: number | null; above: number | null; avgCompaRatio: number | null; hidden: boolean }
export interface BandCoverage { total: number; noBand: number; outsideBand: number | null; overall: CoverageGroup; byBand: CoverageGroup[]; byDepartment: CoverageGroup[]; minGroup: number }

/** Devreden kümülatif gelir vergisi matrahı (yıl ortası geçiş / önceki işveren), çalışan × yıl. */
export interface OpeningTaxBase { employeeId: string; year: number; amount: number; note: string | null; updatedBy: string | null; updatedAt: string }

export const payrollTrApi = {
  openingTaxBases: (year: number, signal?: AbortSignal) => apiFetch<OpeningTaxBase[]>(`${BASE}/payroll/opening-tax-bases${qs({ year })}`, { signal }),
  saveOpeningTaxBase: (employeeId: string, year: number, body: { amount: number; note: string | null }) =>
    apiFetch<OpeningTaxBase | null>(`${BASE}/payroll/opening-tax-bases/${employeeId}/${year}`, { method: 'PUT', body }),
  settings: (signal?: AbortSignal) => apiFetch<PayrollSettings>(`${BASE}/payroll/settings`, { signal }),
  saveSettings: (body: Partial<Pick<PayrollSettings, 'sgk' | 'accounts' | 'costCenters' | 'bank'>>) =>
    apiFetch<PayrollSettings>(`${BASE}/payroll/settings`, { method: 'PUT', body }),
  sgkEmployees: (signal?: AbortSignal) => apiFetch<EmployeeSgk[]>(`${BASE}/payroll/sgk/employees`, { signal }),
  saveSgkEmployee: (employeeId: string, body: Omit<EmployeeSgk, 'employeeId'>) =>
    apiFetch<EmployeeSgk>(`${BASE}/payroll/sgk/employees/${employeeId}`, { method: 'PUT', body }),
  sgkValidation: (periodId: string, signal?: AbortSignal) => apiFetch<SgkValidation>(`${BASE}/payroll/periods/${periodId}/sgk/validation`, { signal }),

  retroCandidates: (employeeId?: string, signal?: AbortSignal) => apiFetch<RetroCandidate[]>(`${BASE}/payroll/retro/candidates${qs({ employeeId })}`, { signal }),
  retroApply: (targetPeriodId: string, items: { employeeId: string; sourcePeriodId: string }[]) =>
    apiFetch<{ applied: number; total: number }>(`${BASE}/payroll/retro/apply`, { method: 'POST', body: { targetPeriodId, items } }),
  retroList: (targetPeriodId?: string, signal?: AbortSignal) => apiFetch<RetroDiff[]>(`${BASE}/payroll/retro${qs({ targetPeriodId })}`, { signal }),

  severancePreview: (body: SeveranceRequest) => apiFetch<SeverancePreview>(`${BASE}/severance/preview`, { method: 'POST', body }),
  severanceSave: (body: SeveranceRequest) => apiFetch<SeveranceCalc>(`${BASE}/severance`, { method: 'POST', body }),
  severanceList: (employeeId?: string, signal?: AbortSignal) => apiFetch<SeveranceCalc[]>(`${BASE}/severance${qs({ employeeId })}`, { signal }),
  severanceDecide: (id: string, approve: boolean, note?: string) => apiFetch<SeveranceCalc>(`${BASE}/severance/${id}/decide`, { method: 'POST', body: { approve, note } }),
  severanceDocument: (id: string) => apiFetch<{ html: string; traceCode: string; sha256: string }>(`${BASE}/severance/${id}/document`),

  publishEPayslips: (periodId: string) => apiFetch<{ created: number; republished: number; unchanged: number }>(`${BASE}/payroll/periods/${periodId}/e-payslips/publish`, { method: 'POST' }),
  ePayslipStatus: (periodId: string, signal?: AbortSignal) => apiFetch<EPayslipStatus>(`${BASE}/payroll/periods/${periodId}/e-payslips`, { signal }),
  myEPayslip: (payslipId: string, signal?: AbortSignal) => apiFetch<MyEPayslip>(`${BASE}/payslips/${payslipId}/e-payslip`, { signal }),
  acknowledge: (payslipId: string) => apiFetch<{ acknowledgedAt: string; contentSha256: string }>(`${BASE}/payslips/${payslipId}/acknowledge`, { method: 'POST' }),

  compaRatios: (signal?: AbortSignal) => apiFetch<CompaRow[]>(`${BASE}/bands/compa-ratios`, { signal }),
  bandCoverage: (signal?: AbortSignal) => apiFetch<BandCoverage>(`${BASE}/bands/coverage`, { signal }),
}
