import { apiFetch } from './client'

/** Y28 basit elektronik imza — talep yaşam döngüsü (expense-service, /api/expense/documents/...); kod/imza/kanıt governance imza motorunda. */
const BASE = '/api/expense/documents'

export type SignatureStatus = 'Pending' | 'Signed' | 'Cancelled'

export interface SignedDocumentView {
  id: string
  employeeId: string
  type: string
  fileName: string
  storageKey: string
  sizeBytes: number
  contentType: string | null
  uploadedAt: string
  signedAt: string | null
  contentHash: string
}

export interface SignatureEvidenceView {
  id: string
  signatureId: string
  documentId: string
  signerEmployeeId: string
  signedAt: string
  documentHash: string
  ipMasked: string | null
  userAgentHash: string | null
  otpChannel: string
  method: string
  evidenceHash: string
  integrityOk: boolean
  documentVersion?: number
  /** governance = tek imza motoru; legacy = birleşme öncesi expense kanıtı (salt okunur). */
  source?: 'governance' | 'legacy'
}

export interface SignatureRequestView {
  id: string
  documentId: string
  employeeId: string
  status: SignatureStatus
  message: string | null
  createdAt: string
  signedAt: string | null
  cancelledAt: string | null
  requestedDocumentHash: string
  /** Kod durumu imza motorundadır; bu alan artık null döner. */
  otp?: null
  document: SignedDocumentView | null
  evidence: SignatureEvidenceView | null
  disclaimer: string
}

export interface SignatureStatusRow {
  documentId: string
  signatureId: string
  status: SignatureStatus
  signedAt: string | null
}

export const docSignatureApi = {
  request: (documentId: string, message?: string) =>
    apiFetch<SignatureRequestView>(`${BASE}/${documentId}/signature-requests`, { method: 'POST', body: { message } }),
  forDocument: (documentId: string, signal?: AbortSignal) =>
    apiFetch<{ document: SignedDocumentView; items: SignatureRequestView[]; disclaimer: string }>(`${BASE}/${documentId}/signatures`, { signal }),
  statusSummary: (signal?: AbortSignal) => apiFetch<SignatureStatusRow[]>(`${BASE}/signature-status`, { signal }),
  /** İmzalı dokümanın imhası (saklama süresi doldu): kanıtlarla birlikte silinir. */
  deleteSigned: (documentId: string) => apiFetch<void>(`${BASE}/${documentId}?confirmSigned=true`, { method: 'DELETE' }),
  cancel: (id: string) => apiFetch<{ message: string }>(`${BASE}/signature-requests/${id}`, { method: 'DELETE' }),
  mine: (signal?: AbortSignal) =>
    apiFetch<{ items: SignatureRequestView[]; disclaimer: string }>(`${BASE}/signature-requests/mine`, { signal }),
  sendOtp: (id: string) =>
    apiFetch<{ message: string; otpId: string; channel: string; expiresAt: string; attemptsLeft: number; maxAttempts: number; sendsLeft: number }>(
      `${BASE}/signature-requests/${id}/otp`, { method: 'POST' }),
  sign: (id: string, code: string, otpId?: string) =>
    apiFetch<{ message: string; evidence: SignatureEvidenceView }>(`${BASE}/signature-requests/${id}/sign`, {
      method: 'POST', body: { code, accept: true, otpId },
    }),
}
