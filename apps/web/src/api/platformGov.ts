import { apiFetch } from './client'
import type { NlReport } from './governance'

const BASE = '/api/governance'

/* ---------------------------------------------------------------- Y24 özel alanlar */
export type CustomFieldType = 'text' | 'number' | 'date' | 'select' | 'boolean'
export type FieldLevel = 'everyone' | 'manager' | 'hr' | 'self'

export interface CustomFieldMeta {
  types: CustomFieldType[]
  levels: FieldLevel[]
  legalBases: Array<{ value: string; label: string; special: boolean }>
  notice: string
}
export interface CustomField {
  id: string
  key: string
  label: string
  type: CustomFieldType
  options: string[]
  required: boolean
  visibility: FieldLevel
  selfEditable: boolean
  isSpecialCategory: boolean
  legalBasis: string
  legalBasisLabel: string
  purpose: string
  retentionMonths: number
  assessmentId: string | null
  isActive: boolean
  sortOrder: number
  createdBy: string
  createdAt: string
}
export interface CustomFieldInput {
  key: string
  label: string
  type: CustomFieldType
  options?: string[]
  required: boolean
  visibility: FieldLevel
  selfEditable: boolean
  isSpecialCategory: boolean
  legalBasis: string
  purpose: string
  retentionMonths: number
  assessmentId?: string | null
  sortOrder?: number
  isActive?: boolean
}
export interface CustomFieldValue {
  id: string
  key: string
  label: string
  type: CustomFieldType
  options: string[]
  required: boolean
  isSpecialCategory: boolean
  visibility: FieldLevel
  value: string | number | boolean | null
  updatedAt: string | null
  editable: boolean
}
export interface CustomFieldValues {
  employeeId: string | null
  viewer: 'self' | 'hr' | 'manager' | 'other' | 'none'
  canEdit: boolean
  fields: CustomFieldValue[]
}

/* ---------------------------------------------------------------- Y25/G4 kayıtlı raporlar */
export type ReportSchedule = 'None' | 'Daily' | 'Weekly' | 'Monthly'
export interface SavedReport {
  id: string
  name: string
  question: string
  departmentId: string | null
  from: string | null
  to: string | null
  compare: boolean | null
  metric: string
  groupBy: string
  personLevel: boolean
  pinned: boolean
  schedule: ReportSchedule
  /** Teslim saati (SS:dd, Europe/Istanbul). */
  time: string
  /** Haftalık 1–7 (pazartesi = 1), aylık 1–28; boşsa pazartesi / ayın 1'i. */
  day: number | null
  timeZone: string
  nextRunAt: string | null
  lastRunAt: string | null
  deliveryCount: number
  createdAt: string
  schedulable: boolean
  link: string
}
export interface ReportFilters {
  departmentId?: string | null
  from?: string | null
  to?: string | null
  compare?: boolean | null
}
/** G3 alanları (NlReport'a eklenen). */
export type NlReportPlus = NlReport & {
  departmentId?: string | null
  department?: string | null
  compare?: boolean
  suppressed?: number
  note?: string | null
  personLevel?: boolean
}

/* ---------------------------------------------------------------- Y28 OTP imza */
export interface SignatureEvidence {
  id: string
  documentType: string
  documentId: string
  documentVersion: number
  documentSha256: string
  signerEmployeeId: string
  signedAt: string
  /** OTP-InApp | OTP-Email | OTP-InApp+Email */
  method: string
  ipPrefix: string | null
  /** Görüntü başlığı (şablon adı / dosya adı); kanıt özetine girmez. */
  title: string | null
  disclaimer: string
  evidenceSha256: string
  /** Kanıt özeti saklı alanlardan yeniden hesaplandığında eşleşiyor mu. */
  integrityOk: boolean
  kind: 'simple-electronic-signature'
}
export interface OtpChallenge { otpId: string; channel: 'InApp' | 'Email'; expiresAt: string; maxAttempts: number; sendsLeft: number; disclaimer: string }

export const platformGovApi = {
  /* özel alanlar */
  fieldMeta: (signal?: AbortSignal) => apiFetch<CustomFieldMeta>(`${BASE}/custom-fields/meta`, { signal }),
  fields: (signal?: AbortSignal) => apiFetch<Array<{ field: CustomField; values: number }>>(`${BASE}/custom-fields`, { signal }),
  createField: (body: CustomFieldInput) => apiFetch<CustomField>(`${BASE}/custom-fields`, { method: 'POST', body }),
  updateField: (id: string, body: CustomFieldInput) => apiFetch<CustomField>(`${BASE}/custom-fields/${id}`, { method: 'PUT', body }),
  deleteField: (id: string) => apiFetch<void>(`${BASE}/custom-fields/${id}`, { method: 'DELETE' }),
  myFieldValues: (signal?: AbortSignal) => apiFetch<CustomFieldValues>(`${BASE}/custom-fields/values/me`, { signal }),
  fieldValues: (employeeId: string, signal?: AbortSignal) => apiFetch<CustomFieldValues>(`${BASE}/custom-fields/values/${employeeId}`, { signal }),
  setFieldValues: (employeeId: string, values: Record<string, string | number | boolean | null>) =>
    apiFetch<CustomFieldValues>(`${BASE}/custom-fields/values/${employeeId}`, { method: 'PUT', body: { values } }),
  assessments: (signal?: AbortSignal) =>
    apiFetch<Array<{ id: string; subject: string; kind: string; status: string; risk: string }>>(`${BASE}/privacy/assessments`, { signal }),

  /* rapor asistanı (G3 süzgeçleri) + kayıtlı raporlar */
  report: (question: string, filters: ReportFilters = {}) =>
    apiFetch<NlReportPlus>(`${BASE}/insights/report`, { method: 'POST', body: { question, ...filters } }),
  departments: (signal?: AbortSignal) => apiFetch<Array<{ id: string; name: string }>>(`${BASE}/insights/departments`, { signal }),
  savedReports: (signal?: AbortSignal) => apiFetch<SavedReport[]>(`${BASE}/saved-reports`, { signal }),
  saveReport: (body: { name: string; question: string; pinned?: boolean; schedule?: ReportSchedule; time?: string; day?: number | null } & ReportFilters) =>
    apiFetch<{ report: SavedReport; result: NlReportPlus }>(`${BASE}/saved-reports`, { method: 'POST', body }),
  updateSavedReport: (id: string, body: { name?: string; pinned?: boolean; schedule?: ReportSchedule; time?: string; day?: number | null }) =>
    apiFetch<SavedReport>(`${BASE}/saved-reports/${id}`, { method: 'PUT', body }),
  deleteSavedReport: (id: string) => apiFetch<void>(`${BASE}/saved-reports/${id}`, { method: 'DELETE' }),
  runSavedReport: (id: string) => apiFetch<{ report: SavedReport; result: NlReportPlus }>(`${BASE}/saved-reports/${id}/run`, { method: 'POST' }),
  pinnedReports: (signal?: AbortSignal) => apiFetch<Array<{ report: SavedReport; result: NlReportPlus }>>(`${BASE}/saved-reports/pinned`, { signal }),

  /* OTP imza */
  requestSignOtp: (docId: string, channel: 'InApp' | 'Email') =>
    apiFetch<OtpChallenge>(`${BASE}/documents/requests/${docId}/sign/otp`, { method: 'POST', body: { channel } }),
  sign: (docId: string, otpId: string, code: string) =>
    apiFetch<SignatureEvidence>(`${BASE}/documents/requests/${docId}/sign`, { method: 'POST', body: { otpId, code } }),
  signature: (docId: string, signal?: AbortSignal) =>
    apiFetch<{ signed: boolean; evidence: SignatureEvidence | null; disclaimer: string }>(`${BASE}/documents/requests/${docId}/signature`, { signal }),
  /** İmzaladığım belgeler — tüm türler (belge talepleri + İK özlük dokümanları). */
  mySignatures: (signal?: AbortSignal) =>
    apiFetch<{ items: SignatureEvidence[]; disclaimer: string }>(`${BASE}/documents/signatures/mine`, { signal }),
}
