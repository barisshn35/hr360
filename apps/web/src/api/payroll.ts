import { apiFetch, qs } from './client'

/** Gateway öneki ile controller yolu üst üste bindiği için "compensation" iki kez geçer. */
const BASE = '/api/compensation/compensation'

export type PayrollPeriodStatus = 'Open' | 'Calculated' | 'Closed'

export interface PayrollPeriod {
  id: string
  year: number
  month: number
  status: PayrollPeriodStatus
  calculatedAt: string | null
  /** Son hesaplayan (görevler ayrılığı: bu kişi dönemi kapatamaz). */
  calculatedBy?: string | null
  closedAt: string | null
  closedBy: string | null
  employeeCount: number
  totalGross: number
  totalNet: number
  totalEmployerCost: number
  /** Dönemin ayı için bordro parametresi girilmiş mi (girilmemişse hesaplama 409 payroll_parameters_missing). */
  parametersConfigured?: boolean
}

export interface TaxBracket { upTo: number | null; rate: number }

/**
 * Bir yürürlük satırı (yıl + yürürlük ayı). Değerleri bordro yetkilisi girer; uygulama yıllık değer varsaymaz.
 * seed: eski kurulumların tohum satırı (2026-10-21 göçü) — "örnek değer, kontrol edip kaydedin".
 */
export interface PayrollParameterRow {
  id: string
  year: number
  validFromMonth: number
  minimumWageGross: number
  minimumWageNet: number | null
  sgkEmployeeRate: number
  unemploymentEmployeeRate: number
  sgkEmployerRate: number
  employerIncentivePoints: number
  unemploymentEmployerRate: number
  stampTaxRate: number
  sgkCeilingMultiplier: number
  brackets: TaxBracket[]
  minimumWageExemption: boolean
  agiMonthly: number | null
  severanceCeilingH1: number | null
  severanceCeilingH2: number | null
  verified: boolean
  seed: boolean
  source: string | null
  updatedBy: string | null
  updatedAt: string
}

/**
 * GET payroll/parameters/{yıl}?month=: istenen ay için geçerli değerler (configured=false ise hepsi null —
 * o dönem hesaplanamaz), yılın satırları ve önceki yılların en son satırı.
 */
export interface PayrollParameters {
  year: number
  month: number
  configured: boolean
  /** Geriye uyum: configured ile aynı. */
  isCustom: boolean
  validFromMonth: number | null
  minimumWageGross: number | null
  minimumWageNet: number | null
  sgkEmployeeRate: number | null
  unemploymentEmployeeRate: number | null
  sgkEmployerRate: number | null
  employerIncentivePoints: number | null
  effectiveEmployerSgkRate: number | null
  unemploymentEmployerRate: number | null
  stampTaxRate: number | null
  sgkCeilingMultiplier: number | null
  sgkFloor: number | null
  sgkCeiling: number | null
  overtimeMultiplier: number
  monthlyHours: number
  brackets: TaxBracket[] | null
  minimumWageExemption: boolean | null
  agiMonthly: number | null
  severanceCeilingH1: number | null
  severanceCeilingH2: number | null
  verified: boolean | null
  seed: boolean
  source: string | null
  updatedBy: string | null
  updatedAt: string | null
  rows: PayrollParameterRow[]
  previous: PayrollParameterRow | null
}

/** PUT gövdesi: oranlar kesir (0,14 = %14). Zorunlu alanlar sunucuda da denetlenir (koddan doldurulmaz). */
export interface PayrollParametersInput {
  validFromMonth: number
  minimumWageGross: number
  minimumWageNet: number | null
  sgkEmployeeRate: number
  unemploymentEmployeeRate: number
  sgkEmployerRate: number
  employerIncentivePoints: number
  unemploymentEmployerRate: number
  stampTaxRate: number
  sgkCeilingMultiplier: number
  brackets: TaxBracket[]
  minimumWageExemption: boolean
  agiMonthly: number | null
  severanceCeilingH1: number | null
  severanceCeilingH2: number | null
  source: string | null
}

export interface PayrollCalcResult {
  paidDays: number; baseGross: number; overtimePay: number; additions: number; gross: number; sgkBase: number
  sgkEmployee: number; unemploymentEmployee: number; taxBase: number; cumulativeTaxBase: number; incomeTax: number
  incomeTaxExemption: number; stampTax: number; stampTaxExemption: number; deductions: number; net: number
  sgkEmployer: number; unemploymentEmployer: number; employerCost: number
}

/** POST payroll/parameters/{yıl}/preview: kaydetmeden örnek hesap. */
export interface PayrollParametersPreview {
  year: number
  month: number
  sample: PayrollCalcResult
  minimumWageNetCalculated: number
  minimumWageNetEntered: number | null
  sgkFloor: number
  sgkCeiling: number
  effectiveEmployerSgkRate: number
}

/** 409 payroll_parameters_missing gövdesi. */
export const PAYROLL_PARAMETERS_MISSING = 'payroll_parameters_missing'

export interface Payslip {
  id: string
  periodId: string
  employeeId: string
  year: number
  month: number
  currency: string
  monthlyBaseGross: number
  paidDays: number
  unpaidDays: number
  overtimeHours: number
  baseGross: number
  overtimePay: number
  additions: number
  gross: number
  sgkBase: number
  sgkEmployee: number
  unemploymentEmployee: number
  taxBase: number
  cumulativeTaxBase: number
  incomeTax: number
  incomeTaxExemption: number
  stampTax: number
  stampTaxExemption: number
  deductions: number
  net: number
  sgkEmployer: number
  unemploymentEmployer: number
  employerCost: number
}

export interface PayslipDetail {
  payslip: Payslip
  /** Dönemin parametre satırı sonradan silindiyse null. */
  rates: { sgkEmployeeRate: number; unemploymentEmployeeRate: number; stampTaxRate: number; employerSgkRate: number; unemploymentEmployerRate: number } | null
}

export type AdjustmentKind = 'Addition' | 'Deduction'
export interface PayrollAdjustment { id: string; periodId: string; employeeId: string; kind: AdjustmentKind; amount: number; description: string; createdAt: string }

export const payrollApi = {
  periods: (year?: number, signal?: AbortSignal) => apiFetch<PayrollPeriod[]>(`${BASE}/payroll/periods${qs({ year })}`, { signal }),
  createPeriod: (year: number, month: number) => apiFetch<PayrollPeriod>(`${BASE}/payroll/periods`, { method: 'POST', body: { year, month } }),
  deletePeriod: (id: string) => apiFetch<void>(`${BASE}/payroll/periods/${id}`, { method: 'DELETE' }),
  calculate: (id: string) => apiFetch<{ employeeCount: number; anomalyChecked?: boolean; anomalyFlags?: number; timesheetLocked?: boolean | null }>(`${BASE}/payroll/periods/${id}/calculate`, { method: 'POST' }),
  close: (id: string) => apiFetch<PayrollPeriod>(`${BASE}/payroll/periods/${id}/close`, { method: 'POST' }),
  reopen: (id: string, reason: string) => apiFetch<PayrollPeriod>(`${BASE}/payroll/periods/${id}/reopen`, { method: 'POST', body: { reason } }),
  payslips: (id: string, signal?: AbortSignal) => apiFetch<Payslip[]>(`${BASE}/payroll/periods/${id}/payslips`, { signal }),
  adjustments: (id: string, signal?: AbortSignal) => apiFetch<PayrollAdjustment[]>(`${BASE}/payroll/periods/${id}/adjustments`, { signal }),
  addAdjustment: (id: string, body: { employeeId: string; kind: AdjustmentKind; amount: number; description: string }) =>
    apiFetch<PayrollAdjustment>(`${BASE}/payroll/periods/${id}/adjustments`, { method: 'POST', body }),
  deleteAdjustment: (id: string, adjId: string) => apiFetch<void>(`${BASE}/payroll/periods/${id}/adjustments/${adjId}`, { method: 'DELETE' }),
  /** Parametresi girilmiş yıllar ve yürürlük ayları (değer döndürmez). */
  parameterYears: (signal?: AbortSignal) => apiFetch<Array<{ year: number; months: number[] }>>(`${BASE}/payroll/parameters`, { signal }),
  parameters: (year: number, month?: number, signal?: AbortSignal) => apiFetch<PayrollParameters>(`${BASE}/payroll/parameters/${year}${qs({ month })}`, { signal }),
  saveParameters: (year: number, body: PayrollParametersInput) =>
    apiFetch<PayrollParameters>(`${BASE}/payroll/parameters/${year}`, { method: 'PUT', body }),
  previewParameters: (year: number, body: { parameters: PayrollParametersInput; monthlyGross: number; month?: number }) =>
    apiFetch<PayrollParametersPreview>(`${BASE}/payroll/parameters/${year}/preview`, { method: 'POST', body }),
  resetParameters: (year: number, validFromMonth?: number) => apiFetch<void>(`${BASE}/payroll/parameters/${year}${qs({ validFromMonth })}`, { method: 'DELETE' }),
  myPayslips: (signal?: AbortSignal) => apiFetch<Payslip[]>(`${BASE}/payslips/me`, { signal }),
  payslip: (id: string, signal?: AbortSignal) => apiFetch<PayslipDetail>(`${BASE}/payslips/${id}`, { signal }),
}
