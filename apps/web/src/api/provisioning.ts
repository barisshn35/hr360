import { apiFetch } from './client'

/**
 * Dalga 12 (madde 92): Google Workspace / Microsoft 365 hesap açma (işe giriş) ve askıya alma (ayrılış).
 * Her istek İK onayından sonra sağlayıcıya gönderilir; kurulumda ACCOUNT_PROVISIONING_ENABLED=true gerekir.
 */
const P = '/api/governance/account-provisioning'

export type ProvisioningProvider = 'Google' | 'Microsoft'
export type ProvisioningStatus = 'Pending' | 'Processing' | 'Done' | 'Failed' | 'Rejected'

export interface ProvisioningProviderInfo {
  provider: ProvisioningProvider
  configured: boolean
  isEnabled: boolean
  domain: string | null
  autoCreate: boolean
  autoSuspend: boolean
  /** Google: servis hesabı e-postası; Microsoft: uygulama (istemci) kimliği. */
  clientId: string | null
  hasCredentials: boolean
  adminSubject: string | null
  orgUnit: string | null
  msTenant: string | null
  usageLocation: string | null
  lastTestAt: string | null
  lastError: string | null
  scopes: string
}

export interface ProvisioningState {
  enabled: boolean
  pending: number
  providers: ProvisioningProviderInfo[]
}

export interface ProvisioningConfigInput {
  domain: string
  isEnabled: boolean
  autoCreate: boolean
  autoSuspend: boolean
  clientId?: string
  /** Google: servis hesabı JSON anahtarı; Microsoft: client secret. Boşsa mevcut sır korunur. */
  credentials?: string
  adminSubject?: string
  orgUnit?: string
  msTenant?: string
  usageLocation?: string
}

export interface ProvisioningRequest {
  id: string
  employeeId: string
  employeeName: string | null
  employeeStatus: string | null
  hireDate: string | null
  provider: ProvisioningProvider
  action: 'Create' | 'Suspend'
  status: ProvisioningStatus
  source: 'Auto' | 'Manual'
  accountEmail: string
  note: string | null
  error: string | null
  requestedByName: string | null
  decidedByName: string | null
  decidedAt: string | null
  attempts: number
  createdAt: string
  completedAt: string | null
}

export interface ApproveResult {
  ok: boolean
  status: string
  accountEmail: string
  /** Yalnızca hesap açmada, bir kez döner; saklanmaz. */
  initialPassword?: string | null
}

export const provisioningApi = {
  state: (signal?: AbortSignal) => apiFetch<ProvisioningState>(P, { signal }),
  saveConfig: (provider: ProvisioningProvider, body: ProvisioningConfigInput) =>
    apiFetch<{ provider: string }>(`${P}/configs/${provider.toLowerCase()}`, { method: 'PUT', body }),
  deleteConfig: (provider: ProvisioningProvider) => apiFetch<void>(`${P}/configs/${provider.toLowerCase()}`, { method: 'DELETE' }),
  testConfig: (provider: ProvisioningProvider) =>
    apiFetch<{ ok: boolean; message: string }>(`${P}/configs/${provider.toLowerCase()}/test`, { method: 'POST' }),
  scan: () => apiFetch<{ create: number; suspend: number }>(`${P}/scan`, { method: 'POST' }),
  requests: (status?: ProvisioningStatus, signal?: AbortSignal) =>
    apiFetch<ProvisioningRequest[]>(`${P}/requests${status ? `?status=${status}` : ''}`, { signal }),
  createRequest: (body: { employeeId: string; provider: ProvisioningProvider; action: 'Create' | 'Suspend'; accountEmail?: string; note?: string }) =>
    apiFetch<{ id: string; accountEmail: string }>(`${P}/requests`, { method: 'POST', body }),
  approve: (id: string, accountEmail?: string) =>
    apiFetch<ApproveResult>(`${P}/requests/${id}/approve`, { method: 'POST', body: { accountEmail: accountEmail || null } }),
  reject: (id: string, reason?: string) => apiFetch<{ status: string }>(`${P}/requests/${id}/reject`, { method: 'POST', body: { reason: reason || null } }),
}
