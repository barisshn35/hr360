import { apiFetch } from './client'
import type {
  CreateWorkflowInput,
  DecideStepInput,
  DelegateStepInput,
  Workflow,
  WorkflowStatus,
  WorkflowType,
} from './types'

const BASE = '/api/workflow'

export interface WorkflowFilters {
  status?: WorkflowStatus
  requesterId?: string
}

export const workflowApi = {
  list: (filters: WorkflowFilters = {}, signal?: AbortSignal) => {
    const params = new URLSearchParams()
    if (filters.status) params.set('status', filters.status)
    if (filters.requesterId) params.set('requesterId', filters.requesterId)
    const qs = params.toString()
    return apiFetch<Workflow[]>(`${BASE}/workflows${qs ? `?${qs}` : ''}`, { signal })
  },

  get: (id: string, signal?: AbortSignal) =>
    apiFetch<Workflow>(`${BASE}/workflows/${id}`, { signal }),

  listOverdue: (signal?: AbortSignal) =>
    apiFetch<Workflow[]>(`${BASE}/workflows/overdue`, { signal }),

  create: (input: CreateWorkflowInput) =>
    apiFetch<Workflow>(`${BASE}/workflows`, { method: 'POST', body: input }),

  decide: (workflowId: string, stepId: string, input: DecideStepInput) =>
    apiFetch<Workflow>(`${BASE}/workflows/${workflowId}/steps/${stepId}/decide`, {
      method: 'POST',
      body: input,
    }),

  delegate: (workflowId: string, stepId: string, input: DelegateStepInput) =>
    apiFetch<Workflow>(`${BASE}/workflows/${workflowId}/steps/${stepId}/delegate`, {
      method: 'POST',
      body: input,
    }),

  bulkDecide: (items: Array<{ workflowId: string; stepId: string }>, decision: 'Approved' | 'Rejected', comment?: string) =>
    apiFetch<{ done: number; results: Array<{ workflowId: string; stepId: string; ok: boolean; error: string | null }> }>(
      `${BASE}/workflows/bulk-decide`, { method: 'POST', body: { items, decision, comment } }),

  emailActionPreview: (token: string, signal?: AbortSignal) =>
    apiFetch<{ type: WorkflowType; createdAt: string; stepOrder: number; stepCount: number; workflowId: string }>(
      `${BASE}/workflows/email-action/${encodeURIComponent(token)}`, { signal, anonymous: true }),
  emailAction: (token: string, decision: 'Approved' | 'Rejected', comment?: string) =>
    apiFetch<{ status: string; decision: string }>(`${BASE}/workflows/email-action`, { method: 'POST', body: { token, decision, comment }, anonymous: true }),

  /** Serbest talepte seçilebilecek onaycılar: giriş hesabı olan, ayrılmamış çalışanlar (çağıran hariç). */
  approverCandidates: (signal?: AbortSignal) =>
    apiFetch<Array<{ id: string; firstName: string; lastName: string; fullName: string }>>(`${BASE}/workflows/approver-candidates`, { signal }),
  /** İzin talebinin onayında çalışanın güncel bakiyesi (talebi görebilenler). */
  leaveBalance: (id: string, signal?: AbortSignal) =>
    apiFetch<WorkflowLeaveBalance>(`${BASE}/workflows/${id}/leave-balance`, { signal }),
  /** Onay ayarları (İK): üst onaycısı bulunamayan talepler İK onaycısına gider. */
  settings: (signal?: AbortSignal) =>
    apiFetch<{ hrApproverEmployeeId: string | null; updatedAt: string | null }>(`${BASE}/workflows/settings`, { signal }),
  saveSettings: (body: { hrApproverEmployeeId: string | null }) =>
    apiFetch<{ hrApproverEmployeeId: string | null; updatedAt: string | null }>(`${BASE}/workflows/settings`, { method: 'PUT', body }),

  /* ------------------------------------------------ akış tanımları ve vekâlet */
  definitions: (signal?: AbortSignal) => apiFetch<WorkflowDefinition[]>(`${BASE}/workflows/definitions`, { signal }),
  validateDefinition: (body: DefinitionInput) =>
    apiFetch<{ valid: boolean; error: string | null; warnings: string[] }>(`${BASE}/workflows/definitions/validate`, { method: 'POST', body }),
  saveDefinition: (id: string | null, body: DefinitionInput) =>
    apiFetch<{ definition: WorkflowDefinition; warnings: string[] }>(`${BASE}/workflows/definitions${id ? `/${id}` : ''}`, { method: id ? 'PUT' : 'POST', body }),
  deleteDefinition: (id: string) => apiFetch<void>(`${BASE}/workflows/definitions/${id}`, { method: 'DELETE' }),
  previewDefinition: (body: { type: WorkflowType; requesterEmployeeId: string; days?: number; amount?: number; hours?: number }) =>
    apiFetch<{ usesDefinition: boolean; approvers: Array<{ employeeId: string; slaHours: number | null }> }>(`${BASE}/workflows/definitions/preview`, { method: 'POST', body }),
  delegations: (all = false, signal?: AbortSignal) => apiFetch<Delegation[]>(`${BASE}/workflows/delegations${all ? '?all=true' : ''}`, { signal }),
  createDelegation: (body: { fromEmployeeId?: string; toEmployeeId: string; startDate: string; endDate: string; reason?: string }) =>
    apiFetch<{ id: string; moved: number }>(`${BASE}/workflows/delegations`, { method: 'POST', body }),
  revokeDelegation: (id: string) => apiFetch<void>(`${BASE}/workflows/delegations/${id}`, { method: 'DELETE' }),
}

export interface WorkflowLeaveBalance {
  available: boolean
  leaveType?: string
  year?: number
  requestDays?: number
  requestStatus?: string
  entitledDays?: number | null
  usedDays?: number | null
  pendingDays?: number | null
  remainingDays?: number | null
}

/**
 * Kendi modül ekranından açılan talep türleri: genel "Yeni talep" ucu bunları reddeder
 * (workflow-service WorkflowsController.ModuleTypes ile aynı).
 */
export const MODULE_WORKFLOW_TYPES: readonly WorkflowType[] = ['LeaveRequest', 'ExpenseClaim', 'Travel', 'Overtime', 'OfferApproval']

export type ApproverKind = 'DepartmentHead' | 'ParentDepartmentHead' | 'Employee'
export type ConditionField = 'Days' | 'Amount' | 'Hours'
export interface DefinitionStep {
  approver: ApproverKind
  employeeId?: string | null
  conditionField?: ConditionField | null
  conditionOp?: '>' | '>=' | '<' | '<=' | null
  conditionValue?: number | null
  slaHours?: number | null
}
export interface WorkflowDefinition { id: string; type: WorkflowType; name: string; isActive: boolean; updatedAt: string; steps: DefinitionStep[]; hiddenFields: string[] }
export interface DefinitionInput { type: WorkflowType; name: string; isActive: boolean; steps: DefinitionStep[]; hiddenFields: string[] }
export interface Delegation {
  id: string; fromEmployeeId: string; toEmployeeId: string; startDate: string; endDate: string; reason: string | null
  createdAt: string; revokedAt: string | null; active: boolean
}
