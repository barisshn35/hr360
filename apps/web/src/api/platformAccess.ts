import { invalidatePlatformGrants } from '@/auth/platformAccess'
import { apiFetch } from './client'

/** Güvenlik dalgası 2A: platform yöneticisinin süreli kiracı erişim izni (break-glass). */
export interface PlatformAccessGrant {
  id: string
  tenantSlug: string
  grantedToUserId: string
  grantedToName: string | null
  reason: string
  createdAt: string
  expiresAt: string
  revokedAt: string | null
  revokedByName: string | null
  active: boolean
}

/** Şüpheli giriş uyarısı (IP adresi tutulmaz). */
export interface LoginAlert {
  id: string
  kind: 'new_network' | 'failed_user' | 'failed_network'
  username: string | null
  count: number
  createdAt: string
}

export type MfaPolicy = 'off' | 'privileged' | 'all'

const BASE = '/api/tenant/platform-access'

export const platformAccessApi = {
  /** Platform yöneticisi: izin aç (en fazla 4 saat, gerekçe zorunlu). */
  open: (body: { tenantSlug: string; reason: string; hours: number }) =>
    apiFetch<PlatformAccessGrant>(`${BASE}/grants`, { method: 'POST', body }).finally(invalidatePlatformGrants),
  mine: (signal?: AbortSignal) => apiFetch<PlatformAccessGrant[]>(`${BASE}/grants/mine`, { signal }),
  /** Şirket yöneticisi: şirketine açılan etkin ve son 90 günün izinleri. */
  forMyTenant: (signal?: AbortSignal) => apiFetch<PlatformAccessGrant[]>(`${BASE}/grants`, { signal }),
  revoke: (id: string) => apiFetch<PlatformAccessGrant>(`${BASE}/grants/${id}/revoke`, { method: 'POST' }).finally(invalidatePlatformGrants),
}

export const identitySecurityApi = {
  setMfaPolicy: (policy: MfaPolicy) =>
    apiFetch<{ policy: MfaPolicy; required: number }>('/api/tenant/security/mfa/policy', { method: 'PUT', body: { policy } }),
  loginAlerts: (signal?: AbortSignal) => apiFetch<LoginAlert[]>('/api/tenant/security/login-alerts', { signal }),
}
