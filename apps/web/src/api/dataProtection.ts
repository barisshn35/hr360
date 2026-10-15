import { apiFetch } from './client'

/**
 * Güvenlik dalgası 2B — veri koruma (governance-service /data-protection): kiracı ayarları
 * (bordro görevler ayrılığı, toplu görüntüleme eşiği), güvenlik uyarıları, iz kodu sorgulama
 * ve erişim gözden geçirme kampanyaları.
 */
const BASE = '/api/governance/data-protection'

export interface SecuritySettings {
  payrollSod: boolean
  payrollSodReason: string | null
  massViewThreshold: number
  massViewWindowMinutes: number
  massViewBlock: boolean
  alertRecipients: { id: string; name: string | null }[]
  updatedAt: string | null
  updatedBy: string | null
  blockMinutes: number
  canEdit: boolean
}
export interface SecuritySettingsInput {
  payrollSod: boolean
  payrollSodReason?: string | null
  massViewThreshold: number
  massViewWindowMinutes: number
  massViewBlock: boolean
  alertEmployeeIds: string[]
}
export interface SecurityAlert {
  id: string
  kind: string
  userName: string | null
  distinctCount: number
  windowMinutes: number
  threshold: number
  detectedAt: string
  blockedUntil: string | null
  blocked: boolean
  acknowledgedAt: string | null
  acknowledgedBy: string | null
}
export interface TraceLookup {
  code: string
  service: string
  entityType: string
  entityId: string | null
  action: string
  userName: string | null
  occurredAt: string
  changes: Record<string, unknown>
  auditId: number
}
export interface AccessReviewSummary {
  id: string
  title: string
  status: 'Open' | 'Closed'
  dueDate: string | null
  createdBy: string | null
  createdAt: string
  closedAt: string | null
  total: number
  decided: number
  removals: number
  applied: number
}
export interface AccessReviewItem {
  id: string
  reviewId: string
  /** Çalışan kaydı olmayan hesapta (ör. yalnızca yönetim için açılmış yönetici) boş; gözden geçiren İK. */
  employeeId: string | null
  hasEmployeeRecord?: boolean
  employeeName: string | null
  position: string | null
  department: string | null
  roles: string[]
  permissions: string[]
  reviewerEmployeeId: string | null
  reviewerName: string | null
  decision: 'Keep' | 'Remove' | null
  removeRoles: string[]
  note: string | null
  decidedBy: string | null
  decidedAt: string | null
  appliedAt: string | null
  applyResult: string | null
}
export interface AccessReviewDetail {
  id: string
  title: string
  status: 'Open' | 'Closed'
  dueDate: string | null
  createdBy: string | null
  createdAt: string
  closedAt: string | null
  items: AccessReviewItem[]
}
export interface MyReviewRow { review: string; dueDate: string | null; item: AccessReviewItem }

export const dataProtectionApi = {
  settings: (signal?: AbortSignal) => apiFetch<SecuritySettings>(`${BASE}/settings`, { signal }),
  saveSettings: (b: SecuritySettingsInput) => apiFetch<SecuritySettings>(`${BASE}/settings`, { method: 'PUT', body: b }),
  alerts: (signal?: AbortSignal) => apiFetch<SecurityAlert[]>(`${BASE}/alerts?days=90`, { signal }),
  ackAlert: (id: string, unblock: boolean) => apiFetch<{ id: string }>(`${BASE}/alerts/${id}/ack`, { method: 'POST', body: { unblock } }),
  findTrace: (code: string) => apiFetch<TraceLookup>(`${BASE}/trace/${encodeURIComponent(code)}`),

  reviews: (signal?: AbortSignal) =>
    apiFetch<{ items: AccessReviewSummary[]; nextDue: string | null; overdue: boolean }>(`${BASE}/access-reviews`, { signal }),
  createReview: (b: { title?: string; dueDate?: string }) =>
    apiFetch<{ id: string; title: string; items: number }>(`${BASE}/access-reviews`, { method: 'POST', body: b }),
  review: (id: string, signal?: AbortSignal) => apiFetch<AccessReviewDetail>(`${BASE}/access-reviews/${id}`, { signal }),
  closeReview: (id: string) => apiFetch<{ id: string }>(`${BASE}/access-reviews/${id}/close`, { method: 'POST' }),
  remind: (id: string) => apiFetch<{ notified: number }>(`${BASE}/access-reviews/${id}/remind`, { method: 'POST' }),
  apply: (id: string, itemId: string) =>
    apiFetch<{ applied: boolean; result: string }>(`${BASE}/access-reviews/${id}/items/${itemId}/apply`, { method: 'POST' }),
  mine: (signal?: AbortSignal) => apiFetch<{ items: MyReviewRow[] }>(`${BASE}/access-reviews/mine`, { signal }),
  decide: (itemId: string, b: { decision: 'Keep' | 'Remove'; removeRoles?: string[]; note?: string }) =>
    apiFetch<{ itemId: string }>(`${BASE}/access-reviews/items/${itemId}/decide`, { method: 'POST', body: b }),
}

/** Gözden geçirme ilerlemesi (yüzde, 0-100). */
export function reviewProgress(r: Pick<AccessReviewSummary, 'total' | 'decided'>): number {
  return r.total > 0 ? Math.round((r.decided / r.total) * 100) : 0
}

/** Bordroyu hazırlayıp kapatabilecek (İK ya da şirket yöneticisi rolü olan, giriş hesabı açık) kullanıcı sayısı. */
export function payrollCapableCount(members: { hasLoginAccess: boolean; roles: string[]; extraPermissions?: string[] }[]): number {
  return members.filter((m) => m.hasLoginAccess && (m.roles.includes('hr-admin') || m.roles.includes('tenant-admin'))).length
}

/** Kaldırma seçenekleri: standart roller (temel "employee" hariç) ve ek izinler. */
export function removableAccess(item: Pick<AccessReviewItem, 'roles' | 'permissions'>): string[] {
  return [...item.roles.filter((r) => r !== 'employee'), ...item.permissions]
}
