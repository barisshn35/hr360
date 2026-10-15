import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

/**
 * Gateway öneki ile controller yolu üst üste bindiği için "compensation"
 * adresin içinde iki kez geçer. Yazım hatası değil.
 */
const BASE = '/api/compensation/compensation'

/* ------------------------------------------------------------------ tipler */

export type CompensationChangeReason =
  | 'Hire'
  | 'AnnualIncrease'
  | 'Promotion'
  | 'MarketAdjustment'
  | 'Demotion'
  | 'Other'

export const compensationReasonLabels: Record<CompensationChangeReason, string> = {
  Hire: tx('İşe alım'),
  AnnualIncrease: tx('Yıllık zam'),
  Promotion: tx('Terfi'),
  MarketAdjustment: tx('Piyasa düzeltmesi'),
  Demotion: tx('Görev değişikliği'),
  Other: tx('Diğer'),
}

export interface CompensationBand {
  id: string
  grade: string
  title: string | null
  minAmount: number
  midAmount: number
  maxAmount: number
  currency: string
  year: number
  /** Yürürlük tarihi (boşsa yılın başı). */
  effectiveFrom?: string | null
}

export interface CompensationRecord {
  id: string
  employeeId: string
  baseSalary: number
  currency: string
  grade: string | null
  reason: CompensationChangeReason
  effectiveFrom: string
  effectiveTo: string | null
  note: string | null
}

export interface SimulationLine {
  employeeId: string
  currentSalary: number
  proposedSalary: number
  increaseAmount: number
  increasePercent: number
  grade: string | null
  /** null: çalışana bant atanmamış (kademe yok ya da o yıl için bant tanımsız). */
  withinBand: boolean | null
  bandMax: number | null
}

export interface SimulationResult {
  employeeCount: number
  currentTotal: number
  proposedTotal: number
  budgetImpact: number
  outOfBandCount: number
  /** Bant atanmamış çalışan sayısı. */
  noBandCount?: number
  lines: SimulationLine[]
}

export interface CreateBandInput {
  grade: string
  title?: string
  minAmount: number
  midAmount: number
  maxAmount: number
  currency: string
  year: number
  effectiveFrom?: string | null
}

export interface CreateRecordInput {
  employeeId: string
  baseSalary: number
  currency: string
  grade?: string
  reason: CompensationChangeReason
  effectiveFrom: string
  note?: string
}

export interface SimulateInput {
  employeeIds: string[]
  increasePercent?: number
  flatIncrease?: number
  year: number
}

/* ------------------------------------------------------------------ servis */

export const compensationApi = {
  listBands: (year?: number, signal?: AbortSignal) =>
    apiFetch<CompensationBand[]>(`${BASE}/bands${qs({ year })}`, { signal }),

  createBand: (input: CreateBandInput) =>
    apiFetch<CompensationBand>(`${BASE}/bands`, { method: 'POST', body: input }),

  updateBand: (id: string, input: CreateBandInput) =>
    apiFetch<CompensationBand>(`${BASE}/bands/${id}`, { method: 'PUT', body: input }),

  deleteBand: (id: string) => apiFetch<void>(`${BASE}/bands/${id}`, { method: 'DELETE' }),

  listRecords: (employeeId?: string, signal?: AbortSignal) =>
    apiFetch<CompensationRecord[]>(`${BASE}/records${qs({ employeeId })}`, { signal }),

  createRecord: (input: CreateRecordInput) =>
    apiFetch<CompensationRecord>(`${BASE}/records`, { method: 'POST', body: input }),

  simulate: (input: SimulateInput) =>
    apiFetch<SimulationResult>(`${BASE}/simulate`, { method: 'POST', body: input }),
}
