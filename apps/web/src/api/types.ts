import { tx } from '@/lib/i18n'
/* ============================ Organization Service ============================ */

export interface Department {
  id: string
  name: string
  companyId: string
  parentDepartmentId: string | null
  /** Departman başı (opsiyonel). Yalnızca `/departments` ucu döndürür; şirket yanıtında olmayabilir. */
  headEmployeeId?: string | null
}

/** Matris organizasyon: From departmanı To departmanına noktalı çizgiyle (ikincil) raporlar. */
export type DepartmentLinkKind = 'Functional' | 'Project'

export interface DepartmentLink {
  id: string
  fromDepartmentId: string
  toDepartmentId: string
  kind: DepartmentLinkKind
  note: string | null
  createdAt: string
}

export interface CreateDepartmentLinkInput {
  fromDepartmentId: string
  toDepartmentId: string
  kind: DepartmentLinkKind
  note?: string
}

export interface Company {
  id: string
  name: string
  taxNumber: string | null
  createdAt: string
  departments: Department[]
}

export interface CreateCompanyInput {
  name: string
  taxNumber?: string
}

export interface CreateDepartmentInput {
  name: string
  companyId: string
  parentDepartmentId?: string | null
}

/* ============================== Employee Service ============================== */

/** Backend enum'u JSON'da sayı olarak döner. */
export const EmployeeStatus = {
  Active: 0,
  OnLeave: 1,
  Terminated: 2,
} as const

export type EmployeeStatusValue = (typeof EmployeeStatus)[keyof typeof EmployeeStatus]

export const employeeStatusLabels: Record<EmployeeStatusValue, string> = {
  0: tx('Aktif'),
  1: tx('İzinde'),
  2: tx('Ayrıldı'),
}

export interface Assignment {
  id: string
  departmentId: string
  positionTitle: string | null
  effectiveFrom: string
  effectiveTo: string | null
}

export interface Employee {
  id: string
  firstName: string
  lastName: string
  email: string
  phone?: string | null
  hireDate: string
  status: EmployeeStatusValue
  assignments: Assignment[]
}

export interface CreateEmployeeInput {
  firstName: string
  lastName: string
  email: string
  phone?: string
  hireDate: string
}

export interface CreateAssignmentInput {
  departmentId: string
  positionTitle?: string
  effectiveFrom: string
}

/* ============================== Workflow Service ============================== */

export type WorkflowType =
  | 'LeaveRequest'
  | 'ExpenseClaim'
  | 'PositionChange'
  | 'AssetRequest'
  | 'Other'
  | 'Overtime'
  | 'DocumentRequest'
  | 'Travel'
  | 'OfferApproval'

export type WorkflowStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled'

export type StepDecision = 'Pending' | 'Approved' | 'Rejected' | 'Delegated'

export const workflowTypeLabels: Record<WorkflowType, string> = {
  LeaveRequest: tx('İzin Talebi'),
  ExpenseClaim: tx('Masraf Talebi'),
  PositionChange: tx('Pozisyon Değişikliği'),
  AssetRequest: tx('Zimmet Talebi'),
  Other: tx('Diğer'),
  Overtime: tx('Fazla Mesai'),
  DocumentRequest: tx('Belge Talebi'),
  Travel: tx('Seyahat Talebi'),
  OfferApproval: tx('Teklif Onayı'),
}

export const workflowStatusLabels: Record<WorkflowStatus, string> = {
  Pending: tx('Beklemede'),
  Approved: tx('Onaylandı'),
  Rejected: tx('Reddedildi'),
  Cancelled: tx('İptal Edildi'),
}

export const stepDecisionLabels: Record<StepDecision, string> = {
  Pending: tx('Bekliyor'),
  Approved: tx('Onaylandı'),
  Rejected: tx('Reddedildi'),
  Delegated: tx('Devredildi'),
}

export interface ApprovalStep {
  id: string
  order: number
  approverEmployeeId: string
  decision: StepDecision
  comment: string | null
  decidedAt: string | null
  delegatedToEmployeeId?: string | null
  /** Süre aşımıyla üst yöneticiye iletildiği an. */
  escalatedAt?: string | null
  delegationId?: string | null
}

export interface Workflow {
  id: string
  type: WorkflowType
  status: WorkflowStatus
  requesterEmployeeId: string
  subject: string | null
  payload?: string | null
  createdAt: string
  slaDueAt: string | null
  steps?: ApprovalStep[]
}

export interface CreateWorkflowInput {
  type: WorkflowType
  requesterEmployeeId: string
  subject?: string
  payload?: string
  approverEmployeeIds: string[]
  slaHours?: number
}

export interface DecideStepInput {
  decision: 'Approved' | 'Rejected'
  comment?: string
}

export interface DelegateStepInput {
  delegateToEmployeeId: string
  comment?: string
}

/* ================================ ML Inference ================================ */

export interface MlHealth {
  status: string
  service: string
  model_loaded: boolean
}

export interface PredictResponse {
  prediction: number
  /** [kalma, ayrılma] — kalibre olasılık (kalibrasyonu olmayan eski sürümde ham). */
  probability: number[]
  model: string
  raw_probability?: number
  calibration?: 'none' | 'sigmoid' | 'isotonic'
  /** Şirketin İK'ca seçilen inceleme eşiği. */
  threshold?: number
  /** Skor eşiğin üstünde mi: karar değil, "insan incelemesi önerilir" işareti. */
  flagged?: boolean
}

/** Sade dilde "neden?" (en etkili özellikler; + riski artırır, − azaltır). */
export interface PredictionReason {
  feature: string
  code: string
  params: Array<string | number>
  direction: 'up' | 'down'
  contribution: number
  text: string
}

export interface FeatureContribution {
  feature: string
  contribution: number
}

export interface ExplainResponse {
  /** ml-inference giriş sırasına göre `number[]` döner. */
  feature_contributions: number[] | FeatureContribution[] | Record<string, number>
  /** Katkıların giriş sırasındaki özellik adları (G5; model kartındaki sıra). */
  feature_names?: string[]
  base_value: number
  reasons?: PredictionReason[]
}

/* ==========================================================================
   Yeni modüller

   Her modül kendi tiplerini ve etiket sözlüklerini kendi servis dosyasında
   taşır; burada yeniden dışa aktarılır ki `@/api/types` tek giriş noktası
   olarak kalsın.
   ========================================================================== */

export * from './leave'
export * from './recruitment'
export * from './onboarding'
export * from './timeshift'
export * from './learning'
export * from './compensation'
export * from './expense'
export * from './notification'
