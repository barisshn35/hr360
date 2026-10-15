import { apiFetch, apiUploadFile, qs } from './client'
import { tx } from '@/lib/i18n'
import type { AnomalyFlag, EInvoice } from '@/lib/expenseAudit'

const BASE = '/api/expense'

/* ------------------------------------------------------------------ tipler */

export type ClaimStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Paid'
export type ExpenseCategory =
  | 'Travel'
  | 'Meal'
  | 'Accommodation'
  | 'Transport'
  | 'Supplies'
  | 'Training'
  | 'Other'
  | 'Mileage'
  | 'PerDiem'
export type CaseCategory = 'Payroll' | 'Benefits' | 'Policy' | 'Complaint' | 'ITSupport' | 'Other'
export type CasePriority = 'Low' | 'Normal' | 'High' | 'Urgent'
export type CaseStatus = 'Open' | 'InProgress' | 'WaitingOnEmployee' | 'Resolved' | 'Closed'

export const claimStatusLabels: Record<ClaimStatus, string> = {
  Draft: tx('Taslak'),
  Submitted: tx('Onayda'),
  Approved: tx('Onaylandı'),
  Rejected: tx('Reddedildi'),
  Paid: tx('Ödendi'),
}

export const expenseCategoryLabels: Record<ExpenseCategory, string> = {
  Travel: tx('Seyahat'),
  Meal: tx('Yemek'),
  Accommodation: tx('Konaklama'),
  Transport: tx('Ulaşım'),
  Supplies: tx('Sarf malzeme'),
  Training: tx('Eğitim'),
  Other: tx('Diğer'),
  Mileage: tx('Kilometre'),
  PerDiem: tx('Harcırah'),
}

export const caseCategoryLabels: Record<CaseCategory, string> = {
  Payroll: tx('Bordro'),
  Benefits: tx('Yan haklar'),
  Policy: tx('Politika'),
  Complaint: tx('Şikâyet'),
  ITSupport: tx('BT desteği'),
  Other: tx('Diğer'),
}

export const casePriorityLabels: Record<CasePriority, string> = {
  Low: tx('Düşük'),
  Normal: tx('Normal'),
  High: tx('Yüksek'),
  Urgent: tx('Acil'),
}

export const caseStatusLabels: Record<CaseStatus, string> = {
  Open: tx('Açık'),
  InProgress: tx('Sürüyor'),
  WaitingOnEmployee: tx('Çalışan bekleniyor'),
  Resolved: tx('Çözüldü'),
  Closed: tx('Kapandı'),
}

export interface ExpenseItem {
  id?: string
  category: ExpenseCategory
  amount: number
  expenseDate: string
  description?: string | null
  receiptStorageKey?: string | null
  originalCurrency?: string | null
  originalAmount?: number | null
  fxRate?: number | null
  km?: number | null
  travelRequestId?: string | null
  /** e-Fatura/e-Arşiv karekodundan: tedarikçi VKN/TCKN, fatura no, ETTN, KDV. */
  supplierTaxId?: string | null
  invoiceNo?: string | null
  ettn?: string | null
  vatAmount?: number | null
  /** Gönderimde üretilen denetim işaretleri (salt okunur; insan incelemesi için). */
  anomalyFlags?: AnomalyFlag[] | null
}

export interface CategoryLimit { perItem: number | null; monthly: number | null; receiptAbove: number | null }
export interface ExpensePolicy {
  limits: Record<string, CategoryLimit>
  kmRate: number
  perDiemDomestic: number
  perDiemAbroad: number
  perDiemAbroadCurrency: string
  updatedAt?: string
}
export type TravelStatus = 'Submitted' | 'Approved' | 'Rejected' | 'Cancelled' | 'Completed'
export interface TravelRequest {
  id: string
  employeeId: string
  destination: string
  abroad: boolean
  startDate: string
  endDate: string
  purpose: string
  transport: 'Plane' | 'Bus' | 'Train' | 'Car' | 'Other'
  needsAccommodation: boolean
  perDiemDays: number
  perDiemRate: number
  perDiemCurrency: string
  perDiemTotal: number
  advanceRequested: number | null
  status: TravelStatus
  workflowRequestId: string | null
  createdAt: string
  hasPassport: boolean
  passportPurged: boolean
}

export interface ExpenseClaim {
  id: string
  employeeId: string
  title: string
  currency: string
  status: ClaimStatus
  /** Kalemlerden hesaplanır; gönderilmez. */
  totalAmount: number
  createdAt: string
  workflowRequestId: string | null
  /** Masraf denetimi çalıştıysa zamanı; null: çalışmadı (ML erişilemedi) ya da henüz gönderilmedi. */
  anomalyCheckedAt?: string | null
  items?: ExpenseItem[]
}

export interface HrCase {
  id: string
  employeeId: string
  subject: string
  description: string | null
  category: CaseCategory
  priority: CasePriority
  status: CaseStatus
  assignedToEmployeeId: string | null
  resolution: string | null
  createdAt: string
  resolvedAt: string | null
}

/** Backend `DocumentType` (expense-service, string enum). */
export type DocumentType = 'Contract' | 'Payslip' | 'IdCard' | 'Diploma' | 'Certificate' | 'Health' | 'Other'

export const documentTypeLabels: Record<DocumentType, string> = {
  Contract: tx('Sözleşme'),
  Payslip: tx('Bordro'),
  IdCard: tx('Kimlik'),
  Diploma: tx('Diploma'),
  Certificate: tx('Sertifika'),
  Health: tx('Sağlık raporu'),
  Other: tx('Diğer'),
}

/** NOT: Önceden `name`/`createdAt` bekleniyordu; backend `fileName`/`uploadedAt`
 * döner - listede ad sütunu boş, tarih "—" görünüyordu. */
export interface HrDocument {
  id: string
  employeeId: string
  type: DocumentType
  fileName: string
  /** Dosya bağlı değilse boş string. */
  storageKey: string
  sizeBytes: number
  contentType: string | null
  uploadedAt: string
  uploadedByEmployeeId: string | null
}

export interface CreateClaimInput {
  employeeId: string
  title: string
  currency: string
  items: ExpenseItem[]
}

export interface CreateCaseInput {
  employeeId: string
  subject: string
  description?: string
  category: CaseCategory
  priority: CasePriority
}

export interface CreateDocumentInput {
  employeeId: string
  type: DocumentType
  fileName: string
  storageKey?: string
}

/* ------------------------------------------------------------------ servis */

export const expenseApi = {
  listClaims: (
    filters: { employeeId?: string; status?: ClaimStatus } = {},
    signal?: AbortSignal,
  ) => apiFetch<ExpenseClaim[]>(`${BASE}/expense-claims${qs(filters)}`, { signal }),

  getClaim: (id: string, signal?: AbortSignal) =>
    apiFetch<ExpenseClaim>(`${BASE}/expense-claims/${id}`, { signal }),

  createClaim: (input: CreateClaimInput) =>
    apiFetch<ExpenseClaim>(`${BASE}/expense-claims`, { method: 'POST', body: input }),

  /** Yalnızca taslak; kalemlerin tamamı yeniden yazılır. */
  updateClaim: (id: string, input: Omit<CreateClaimInput, 'employeeId'>) =>
    apiFetch<ExpenseClaim>(`${BASE}/expense-claims/${id}`, { method: 'PUT', body: input }),

  deleteClaim: (id: string) =>
    apiFetch<void>(`${BASE}/expense-claims/${id}`, { method: 'DELETE' }),

  submitClaim: (id: string, workflowRequestId?: string) =>
    apiFetch<ExpenseClaim>(`${BASE}/expense-claims/${id}/submit`, {
      method: 'POST',
      body: { workflowRequestId },
    }),

  resolveClaim: (id: string, approved: boolean) =>
    apiFetch<ExpenseClaim>(`${BASE}/expense-claims/${id}/resolve`, {
      method: 'POST',
      body: { approved },
    }),

  markPaid: (id: string) =>
    apiFetch<ExpenseClaim>(`${BASE}/expense-claims/${id}/mark-paid`, { method: 'POST' }),

  listDocuments: (
    filters: { employeeId?: string; type?: string } = {},
    signal?: AbortSignal,
  ) => apiFetch<HrDocument[]>(`${BASE}/documents${qs(filters)}`, { signal }),

  createDocument: (input: CreateDocumentInput) =>
    apiFetch<HrDocument>(`${BASE}/documents`, { method: 'POST', body: input }),

  deleteDocument: (id: string) =>
    apiFetch<void>(`${BASE}/documents/${id}`, { method: 'DELETE' }),

  listCases: (
    filters: { employeeId?: string; status?: CaseStatus; priority?: CasePriority } = {},
    signal?: AbortSignal,
  ) => apiFetch<HrCase[]>(`${BASE}/hr-cases${qs(filters)}`, { signal }),

  getCase: (id: string, signal?: AbortSignal) =>
    apiFetch<HrCase>(`${BASE}/hr-cases/${id}`, { signal }),

  createCase: (input: CreateCaseInput) =>
    apiFetch<HrCase>(`${BASE}/hr-cases`, { method: 'POST', body: input }),

  assignCase: (id: string, assignedToEmployeeId: string) =>
    apiFetch<HrCase>(`${BASE}/hr-cases/${id}/assign`, {
      method: 'POST',
      body: { assignedToEmployeeId },
    }),

  policy: (signal?: AbortSignal) => apiFetch<ExpensePolicy>(`${BASE}/expense-policy`, { signal }),
  savePolicy: (body: Omit<ExpensePolicy, 'updatedAt'>) => apiFetch<unknown>(`${BASE}/expense-policy`, { method: 'PUT', body }),
  fx: (currency: string, date?: string, signal?: AbortSignal) =>
    apiFetch<{ currency: string; rate: number; rateDate: string; source: string }>(`${BASE}/expense-fx${qs({ currency, date })}`, { signal }),
  setFx: (body: { currency: string; date: string; rate: number }) => apiFetch<unknown>(`${BASE}/expense-fx`, { method: 'PUT', body }),
  /** e-Fatura/e-Arşiv karekod metnini ayrıştırır ve doğrular (ml-inference; metin saklanmaz). */
  einvoice: (text: string) => apiFetch<EInvoice>(`${BASE}/expense-claims/einvoice`, { method: 'POST', body: { text } }),
  ocr: (file: File) => apiUploadFile<{ amount: number | null; date: string | null; taxNo: string | null; confidence: number | null }>(`${BASE}/expense-claims/ocr`, file),
  travels: (signal?: AbortSignal) => apiFetch<TravelRequest[]>(`${BASE}/travel`, { signal }),
  createTravel: (body: { destination: string; abroad: boolean; startDate: string; endDate: string; purpose: string; transport: string; needsAccommodation: boolean; advanceRequested?: number | null; passportNumber?: string }) =>
    apiFetch<TravelRequest>(`${BASE}/travel`, { method: 'POST', body }),
  cancelTravel: (id: string) => apiFetch<TravelRequest>(`${BASE}/travel/${id}/cancel`, { method: 'POST' }),
  decideTravel: (id: string, approve: boolean) => apiFetch<TravelRequest>(`${BASE}/travel/${id}/decide`, { method: 'POST', body: { approve } }),
  perDiemClaim: (id: string) => apiFetch<{ claimId: string; amount: number }>(`${BASE}/travel/${id}/per-diem-claim`, { method: 'POST' }),
  passport: (id: string) => apiFetch<{ passportNumber: string }>(`${BASE}/travel/${id}/passport`),

  resolveCase: (id: string, resolution: string) =>
    apiFetch<HrCase>(`${BASE}/hr-cases/${id}/resolve`, { method: 'POST', body: { resolution } }),
}
