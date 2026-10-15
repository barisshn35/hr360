import { apiFetch } from './client'
import { downloadAuthed } from './engagement'

/**
 * Dalga 10 uçları: VERBİS envanteri, başvuru veri paketi, yeniden onay kampanyası, imha önizleme ve
 * doğrulama (KVKK); kiracı verisiyle model eğitimi izni, "Sana uygun" önerileri, aday–ilan uygunluğu (ML).
 */
const G = '/api/governance'

/* ------------------------------------------------------------------ 53) VERBİS envanteri */
export interface VerbisItem {
  id: string
  key: string
  source: 'Catalog' | 'Custom'
  module: string
  activity: string
  subjects: string[]
  dataCategories: string[]
  purpose: string
  legalBasis: string
  special: boolean
  retention: string
  retentionCategory: string | null
  recipients: string[]
  transferProviders: string[]
  measures: string
  isActive: boolean
  updatedAt: string
  updatedByName: string | null
  retentionPolicy: { retentionMonths: number; action: string; isEnabled: boolean } | null
}
export interface VerbisState {
  lastUpdatedAt: string | null
  lastUpdatedBy: string | null
  catalogMissing: { id: string; module: string; activity: string }[]
  providers: { key: string; name: string; country: string; mechanism: string; inUse: boolean }[]
  retentionCategories: { value: string; label: string }[]
  items: VerbisItem[]
}
export type VerbisInput = Pick<VerbisItem, 'module' | 'activity' | 'subjects' | 'dataCategories' | 'purpose' | 'legalBasis' | 'special' | 'retention'
  | 'retentionCategory' | 'recipients' | 'transferProviders' | 'measures' | 'isActive'>

/* ------------------------------------------------------------------ 55) kampanya */
export interface ConsentCampaign {
  id: string
  type: string
  title: string
  required: boolean
  version: string
  previousVersion: string | null
  status: 'Open' | 'Closed'
  startedBy: string
  startedAt: string
  closedAt: string | null
  reminderCount: number
  lastReminderAt: string | null
  current: boolean
  target: number
  done: number
  percent: number
  pending: { employeeId: string; name: string; department: string | null }[] | null
  canRemind: boolean
}
export interface CampaignState {
  types: { type: string; title: string; version: string; required: boolean; hasCampaign: boolean }[]
  campaigns: ConsentCampaign[]
}
export interface PendingConsent { type: string; title: string; version: string; required: boolean; reason: 'unread' | 'updated' }

/* ------------------------------------------------------------------ 56/57) imha */
export interface RetentionPreview {
  category: string
  label: string
  action: string
  retentionMonths: number
  isEnabled: boolean
  subjects: number | null
  total: number
  tables: { table: string; label: string; kind: 'Anonymize' | 'Delete'; rows: number; skipped: string | null }[]
  storage: { table: string; column: string; keys: number }[]
  retained: { table: string; reason: string }[]
}
export type StorageStatus = 'Pending' | 'Deleted' | 'Absent' | 'NotStored' | 'Failed'
export interface DestructionVerification {
  storage: { category: string; table: string; column: string; status: StorageStatus; count: number }[]
  recent: { category: string; table: string; column: string; keyHash: string; status: StorageStatus; detail: string | null; attempts: number; createdAt: string; processedAt: string | null }[]
  coverage: { table: string; status: 'covered' | 'retained' | 'review'; reason: string | null }[]
  logs: {
    id: string; category: string; label: string; action: string; affected: number; trigger: string; actor: string; ranAt: string
    details: { subjects?: number; tables?: { table: string; label: string; kind: string; rows: number; skipped: string | null }[]; storageQueued?: number } | null
  }[]
  storageMap: { location: string; store: string; personal: string; destruction: string }[]
  steps: { table: string; label: string; kind: string }[]
  retained: { table: string; reason: string }[]
}

/* ------------------------------------------------------------------ 54) paket */
export interface PackageInfo { id: string; fileName: string; sha256: string; sizeBytes: number; sections: Record<string, number> }

export const kvkk10Api = {
  verbis: (signal?: AbortSignal) => apiFetch<VerbisState>(`${G}/privacy/verbis`, { signal }),
  verbisSync: () => apiFetch<{ added: number }>(`${G}/privacy/verbis/sync`, { method: 'POST' }),
  createVerbis: (body: VerbisInput) => apiFetch<{ id: string }>(`${G}/privacy/verbis/items`, { method: 'POST', body }),
  updateVerbis: (id: string, body: VerbisInput) => apiFetch<{ id: string; changed: string[] }>(`${G}/privacy/verbis/items/${id}`, { method: 'PUT', body }),
  deleteVerbis: (id: string) => apiFetch<void>(`${G}/privacy/verbis/items/${id}`, { method: 'DELETE' }),
  verbisRows: () => apiFetch<{ columns: string[]; rows: string[][]; lastUpdatedAt: string; lastUpdatedBy: string | null }>(`${G}/privacy/verbis/export?format=json`),
  downloadVerbis: (format: 'csv' | 'html') => downloadAuthed(`${G}/privacy/verbis/export?format=${format}`, `verbis-envanter.${format}`),

  createPackage: (requestId: string) => apiFetch<PackageInfo>(`${G}/privacy/requests/${requestId}/package`, { method: 'POST' }),
  downloadPackage: (requestId: string) => downloadAuthed(`${G}/privacy/requests/${requestId}/package`, 'kisisel-veri-paketi.zip'),

  campaigns: (signal?: AbortSignal) => apiFetch<CampaignState>(`${G}/privacy/consent-campaigns`, { signal }),
  startCampaign: (type: string) => apiFetch<{ id: string }>(`${G}/privacy/consent-campaigns`, { method: 'POST', body: { type } }),
  remindCampaign: (id: string) => apiFetch<{ sent: number }>(`${G}/privacy/consent-campaigns/${id}/remind`, { method: 'POST' }),
  closeCampaign: (id: string) => apiFetch<{ closed: boolean }>(`${G}/privacy/consent-campaigns/${id}/close`, { method: 'POST' }),
  pendingConsents: (signal?: AbortSignal) => apiFetch<PendingConsent[]>(`${G}/privacy/consents/pending`, { signal }),

  retentionPreview: (id: string, signal?: AbortSignal) => apiFetch<RetentionPreview>(`${G}/privacy/retention/${id}/preview`, { signal }),
  verification: (signal?: AbortSignal) => apiFetch<DestructionVerification>(`${G}/privacy/destruction-verification`, { signal }),
  processStorage: () => apiFetch<{ processed: number }>(`${G}/privacy/destruction-verification/process`, { method: 'POST' }),
}

/* ================================================================== ML */
export interface TenantTrainingSummary {
  trainSnapshot: string
  evalSnapshot: string
  train: { rows: number; leavers: number; imputed: Record<string, number> }
  eval: { rows: number; leavers: number; imputed: Record<string, number> }
  ok: boolean
  refusals: string[]
}
export interface TenantTrainingState {
  enabled: boolean
  consentBy: string | null
  consentAt: string | null
  lastTrainingAt: string | null
  lastTraining: { at: string; candidateVersion: string | null; awaitingApproval: boolean; promoted: boolean; reason: string | null; candidate: { auc?: number } | null; current: { auc?: number } | null } | null
  canConsent: boolean
  thresholds: { minTrainRows: number; minTrainLeavers: number; minEvalRows: number; minEvalLeavers: number }
  trainSnapshot: string
  evalSnapshot: string
  features: string[]
}

export interface Recommendation<T> { id: string; score: number; reasons: string[]; info: T }
export interface Recommendations {
  linked: boolean
  postings: Recommendation<{ title: string; department: string | null; applied: boolean }>[]
  mentors: Recommendation<{ userId: string; name: string; department: string | null; offers: string[] }>[]
  courses: Recommendation<{ title: string; category: string | null }>[]
  gaps: { id: string; name: string; required: number; current: number }[]
  skillsUsed?: string[]
  note?: string
}

export interface FitComponent { value: number; matched?: string[]; missing?: string[]; years?: number | null; required?: number; evidence?: string | null; met?: string[]; unmet?: string[] }
export interface CandidateFit {
  applicationId: string
  candidateId: string
  score: number | null
  components: Partial<Record<'required_skills' | 'experience_years' | 'qualifications' | 'preferred_skills', FitComponent>>
  reasons: string[]
  flags: string[]
  redactions: Record<string, number>
}
export interface FitResult {
  posting: { required_skills: string[]; preferred_skills: string[]; min_years: number | null; qualifications: string[] }
  weights: Record<string, number>
  note: string
  results: CandidateFit[]
}

export const ml10Api = {
  tenantTraining: (signal?: AbortSignal) => apiFetch<TenantTrainingState>(`${G}/model/tenant-training`, { signal }),
  setTrainingConsent: (enabled: boolean) => apiFetch<TenantTrainingState>(`${G}/model/tenant-training/consent`, { method: 'PUT', body: { enabled } }),
  trainingPreview: (signal?: AbortSignal) => apiFetch<TenantTrainingSummary>(`${G}/model/tenant-training/preview`, { signal }),
  runTenantTraining: () => apiFetch<NonNullable<TenantTrainingState['lastTraining']>>(`${G}/model/tenant-training/run`, { method: 'POST' }),
  recommendations: (signal?: AbortSignal) => apiFetch<Recommendations>(`${G}/growth/recommendations`, { signal }),
  candidateFit: (postingId: string, signal?: AbortSignal) => apiFetch<FitResult>(`${G}/ai/recruit-fit/${postingId}`, { signal }),
}
