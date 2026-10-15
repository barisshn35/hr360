import { apiFetch } from './client'

const BASE = '/api/governance'

/* ---------------------------------------------------------------- aydınlatma metinleri (K2) */
export interface NoticeState {
  type: string
  title: string
  version: string
  required: boolean
  text: string
  custom: boolean
  builtIn: string
  acknowledgedCurrent: number
  onOlderVersion: number
  history: { id: string; version: string; title: string; changeNote: string | null; publishedBy: string; publishedAt: string }[]
}

/* ---------------------------------------------------------------- veri ihlali (K3) */
export type BreachSeverity = 'Low' | 'Medium' | 'High'
export interface DataBreach {
  id: string
  title: string
  description: string
  detectedAt: string
  occurredAt: string | null
  dataCategories: string | null
  affectedCount: number | null
  affectedEmployees: string[]
  severity: BreachSeverity
  cause: string | null
  measures: string | null
  status: 'Open' | 'Reported' | 'Closed'
  reportedToBoardAt: string | null
  boardReference: string | null
  subjectsNotifiedAt: string | null
  createdBy: string
  createdAt: string
  boardDeadline: string
  hoursLeft: number | null
  overdue: boolean
  lateReport: boolean
}
export interface BreachInput {
  title: string
  description: string
  detectedAt?: string
  occurredAt?: string | null
  dataCategories?: string
  affectedCount?: number | null
  affectedEmployees?: string[]
  severity?: BreachSeverity
  cause?: string
  measures?: string
}

/* ---------------------------------------------------------------- başvuru (K8) */
export interface RequestMeta {
  verificationMethods: { value: string; label: string }[]
  channels: string[]
  templates: { key: string; kind: string; outcome: string; title: string; text: string }[]
}

/* ---------------------------------------------------------------- PIA (K9) */
export type PiaAnswer = 'yes' | 'no' | 'na'
export interface PiaQuestion { code: string; text: string; riskyAnswer: PiaAnswer; weight: number }
export interface PrivacyAssessment {
  id: string
  subject: string
  kind: 'Integration' | 'CustomField' | 'Process'
  providerKey: string | null
  answers: Record<string, { answer: PiaAnswer; note: string | null }>
  risk: 'Low' | 'Medium' | 'High'
  status: 'Draft' | 'Approved'
  createdBy: string
  approvedBy: string | null
  approvedAt: string | null
  updatedAt: string
}
export interface AssessmentInput {
  subject: string
  kind: PrivacyAssessment['kind']
  providerKey: string | null
  answers: Record<string, { answer: PiaAnswer; note?: string | null }>
}

/* ---------------------------------------------------------------- alan yetkisi (G20) */
export type FieldLevel = 'everyone' | 'manager' | 'hr' | 'self'
export interface FieldPolicy {
  field: string
  label: string
  sensitive: boolean
  defaultLevel: FieldLevel
  level: FieldLevel
  updatedBy: string | null
  updatedAt: string | null
  allowed: FieldLevel[]
}

/* ---------------------------------------------------------------- denetim zinciri (G21) */
export interface AuditVerify {
  ok: boolean
  rows: number
  tampered: number
  broken: number
  gaps: number
  unchained: number
  firstProblemSeq: number | null
  fromSeq: number | null
  toSeq: number | null
  head: string | null
  anchorProblem: string | null
  nightly: { checkedAt: string; ok: boolean; toSeq: number | null; problem: string | null } | null
  checkedAt: string
}
export interface SiemStatus {
  configured: boolean
  endpoint: string | null
  state: { lastAuditId: number; lastSentAt: string | null; sent: number; lastError: string | null } | null
}

export const kvkkOpsApi = {
  notices: (signal?: AbortSignal) => apiFetch<NoticeState[]>(`${BASE}/privacy/notices`, { signal }),
  publishNotice: (body: { type: string; title: string; text: string; version?: string; changeNote?: string }) =>
    apiFetch<unknown>(`${BASE}/privacy/notices`, { method: 'POST', body }),

  breaches: (signal?: AbortSignal) => apiFetch<DataBreach[]>(`${BASE}/privacy/breaches`, { signal }),
  createBreach: (body: BreachInput) => apiFetch<DataBreach>(`${BASE}/privacy/breaches`, { method: 'POST', body }),
  updateBreach: (id: string, body: BreachInput) => apiFetch<DataBreach>(`${BASE}/privacy/breaches/${id}`, { method: 'PUT', body }),
  reportBreach: (id: string, body: { reportedAt?: string; reference?: string }) =>
    apiFetch<DataBreach>(`${BASE}/privacy/breaches/${id}/report`, { method: 'POST', body }),
  notifyBreach: (id: string, message?: string) =>
    apiFetch<{ notified: number }>(`${BASE}/privacy/breaches/${id}/notify`, { method: 'POST', body: { message } }),
  closeBreach: (id: string) => apiFetch<DataBreach>(`${BASE}/privacy/breaches/${id}/close`, { method: 'POST' }),
  boardForm: (id: string) => apiFetch<{ text: string; late: boolean }>(`${BASE}/privacy/breaches/${id}/board-form`),

  requestMeta: (signal?: AbortSignal) => apiFetch<RequestMeta>(`${BASE}/privacy/request-meta`, { signal }),
  createExternalRequest: (body: { kind: string; personName: string; employeeId?: string | null; channel: string; contact?: string; details?: string; receivedAt?: string }) =>
    apiFetch<unknown>(`${BASE}/privacy/requests/external`, { method: 'POST', body }),
  verifyRequest: (id: string, method: string) =>
    apiFetch<unknown>(`${BASE}/privacy/requests/${id}/verify`, { method: 'POST', body: { method } }),

  piaQuestions: (signal?: AbortSignal) =>
    apiFetch<{ questions: PiaQuestion[]; providers: { key: string; name: string }[] }>(`${BASE}/privacy/assessments/questions`, { signal }),
  assessments: (signal?: AbortSignal) => apiFetch<PrivacyAssessment[]>(`${BASE}/privacy/assessments`, { signal }),
  createAssessment: (body: AssessmentInput) => apiFetch<PrivacyAssessment>(`${BASE}/privacy/assessments`, { method: 'POST', body }),
  updateAssessment: (id: string, body: AssessmentInput) => apiFetch<PrivacyAssessment>(`${BASE}/privacy/assessments/${id}`, { method: 'PUT', body }),
  approveAssessment: (id: string) => apiFetch<PrivacyAssessment>(`${BASE}/privacy/assessments/${id}/approve`, { method: 'POST' }),
  deleteAssessment: (id: string) => apiFetch<void>(`${BASE}/privacy/assessments/${id}`, { method: 'DELETE' }),

  fieldPolicies: (signal?: AbortSignal) => apiFetch<FieldPolicy[]>(`${BASE}/privacy/field-policies`, { signal }),
  setFieldPolicy: (field: string, level: FieldLevel) =>
    apiFetch<unknown>(`${BASE}/privacy/field-policies/${field}`, { method: 'PUT', body: { level } }),

  verifyAudit: () => apiFetch<AuditVerify>(`${BASE}/audit/verify`),
  siem: (signal?: AbortSignal) => apiFetch<SiemStatus>(`${BASE}/audit/siem`, { signal }),
}

/* ---------------------------------------------------------------- oturum/IP/passkey (G22) */
export interface MySessions {
  sessions: { id: string; ipAddress: string | null; start: string; lastAccess: string; current: boolean }[]
  hasOtp: boolean
  hasPasskey: boolean
}
export const accountSecurityApi = {
  mySessions: (signal?: AbortSignal) => apiFetch<MySessions>('/api/tenant/my-tenant/me/sessions', { signal }),
  endSession: (id: string) => apiFetch<void>(`/api/tenant/my-tenant/me/sessions/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  endOthers: () => apiFetch<{ closed: number }>('/api/tenant/my-tenant/me/sessions/logout-others', { method: 'POST' }),
  ipAllowlist: (signal?: AbortSignal) => apiFetch<{ entries: string[]; yourIp: string | null }>('/api/tenant/security/ip-allowlist', { signal }),
  setIpAllowlist: (entries: string[]) => apiFetch<{ entries: string[] }>('/api/tenant/security/ip-allowlist', { method: 'PUT', body: { entries } }),
  tenantSessions: (signal?: AbortSignal) =>
    apiFetch<{ userId: string; username: string | null; sessions: { id: string; ipAddress: string | null; start: string; lastAccess: string }[] }[]>('/api/tenant/security/sessions', { signal }),
  logoutUser: (userId: string) => apiFetch<void>(`/api/tenant/security/sessions/${encodeURIComponent(userId)}/logout`, { method: 'POST' }),
  passkeyStats: (signal?: AbortSignal) => apiFetch<{ members: number; withPasskey: number }>('/api/tenant/security/passkeys', { signal }),
}
