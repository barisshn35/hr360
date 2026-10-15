import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiFetch } from './client'

/* ============================ Parola linkleri ============================
 * HR360'ın kendi süreli, tek kullanımlık parola belirleme/sıfırlama linkleri.
 * Link yalnızca üretildiği yanıtta döner (PasswordLinkView.link); sonra hiçbir
 * uçtan tekrar okunamaz — kaybolursa yeniden üretilir (eskisi iptal olur).
 * ======================================================================== */

const BASE = '/api/tenant'

export type PasswordMethod = 'invite' | 'keycloak'
export type LinkPurpose = 'setup' | 'reset'
export type LinkStatus = 'active' | 'expired' | 'used' | 'revoked' | 'pending' | 'invalid'

/** Üretim yanıtı. link: yalnızca bu yanıtta (gösterilecekse). */
export interface PasswordLinkView {
  inviteId: string
  method: PasswordMethod
  link: string | null
  expiresAt: string
  /** none: e-posta kapalı · queued: link e-postayla kuyrukta · keycloak: Keycloak e-postası gitti · failed */
  emailStatus: 'none' | 'queued' | 'keycloak' | 'failed'
  /** Eski yöntem ya da e-posta başarısız olduğu için linke düşüldü. */
  emailFallback: boolean
  purpose: LinkPurpose
}

export interface PasswordLinkRow {
  id: string
  email: string
  fullName: string | null
  purpose: LinkPurpose
  channel: 'link' | 'email' | 'keycloak'
  emailStatus: string
  createdAt: string
  expiresAt: string
  usedAt: string | null
  revokedAt: string | null
  createdByName: string | null
  status: LinkStatus
  keycloakUserId: string
}

export interface PasswordMethodView {
  choice: PasswordMethod | null
  selected: PasswordMethod
  effective: PasswordMethod
  platformDefault: PasswordMethod
  keycloakAvailable: boolean
  emailAvailable: boolean
  inviteTtlHours: number
}

export interface PublicLinkState {
  status: Exclude<LinkStatus, 'pending'>
  purpose?: LinkPurpose
  email?: string
  company?: string | null
  expiresAt?: string
}

export interface SystemSettings {
  smtp: {
    /** self: sunucunun kendi posta servisi (install.sh --mail-mode=self) */
    mode: 'custom' | 'env' | 'self' | 'mailpit'
    configured: boolean
    host: string | null
    port: number | null
    user: string | null
    hasPassword: boolean
    fromAddress: string
    fromName: string
    installHost: string | null
    installSelf: boolean
    tenantsWithOwnSmtp: number
  }
  signupEnabled: boolean
  passwordMethod: PasswordMethod
  inviteTtlHours: number
  site: SiteSettings
  updatedAt: string | null
  updatedBy: string | null
}

export interface FaqItem {
  q: string
  a: string
}

/** Herkese açık site: işletmeci/iletişim, yasal metinler (Markdown; boş = varsayılan şablon), SSS. */
export interface SiteSettings {
  operatorName: string | null
  address: string | null
  email: string | null
  phone: string | null
  kep: string | null
  verbisNo: string | null
  privacyMd: string | null
  termsMd: string | null
  faq: FaqItem[]
  searchIndexing: boolean
}

export interface SiteInput {
  operatorName?: string
  address?: string
  email?: string
  phone?: string
  kep?: string
  verbisNo?: string
  privacyMd?: string
  termsMd?: string
  faq: FaqItem[]
  searchIndexing: boolean
}

export interface SmtpInput {
  host: string
  port: number
  user?: string
  /** undefined: değiştirme · '': sil */
  password?: string
  fromAddress: string
  fromName?: string
}

export const passwordLinkApi = {
  list: (signal?: AbortSignal) => apiFetch<PasswordLinkRow[]>(`${BASE}/my-tenant/password-links`, { signal }),
  revoke: (id: string) => apiFetch<{ status: string }>(`${BASE}/my-tenant/password-links/${id}/revoke`, { method: 'POST' }),
  regenerate: (id: string) => apiFetch<PasswordLinkView>(`${BASE}/my-tenant/password-links/${id}/regenerate`, { method: 'POST' }),
  resetFor: (keycloakUserId: string) =>
    apiFetch<PasswordLinkView>(`${BASE}/my-tenant/password-links/users/${keycloakUserId}/reset`, { method: 'POST' }),
  tenantAdminLink: (tenantId: string) => apiFetch<PasswordLinkView>(`${BASE}/tenants/${tenantId}/admin-link`, { method: 'POST' }),

  method: (signal?: AbortSignal) => apiFetch<PasswordMethodView>(`${BASE}/my-tenant/password-method`, { signal }),
  setMethod: (method: PasswordMethod | null) =>
    apiFetch<PasswordMethodView>(`${BASE}/my-tenant/password-method`, { method: 'PUT', body: { method } }),

  // --- Anonim ---
  linkState: (token: string, signal?: AbortSignal) =>
    apiFetch<PublicLinkState>(`${BASE}/public/password/link/${encodeURIComponent(token)}`, { signal, anonymous: true }),
  setPassword: (token: string, password: string) =>
    apiFetch<{ status: string }>(`${BASE}/public/password/link/${encodeURIComponent(token)}`, {
      method: 'POST', body: { password }, anonymous: true,
    }),
  requestReset: (email: string, language?: string) =>
    apiFetch<{ message: string }>(`${BASE}/public/password/reset-request`, {
      method: 'POST', body: { email, language }, anonymous: true,
    }),

  // --- Platform: Sistem ayarları ---
  systemSettings: (signal?: AbortSignal) => apiFetch<SystemSettings>(`${BASE}/platform/settings`, { signal }),
  saveSmtp: (input: SmtpInput) => apiFetch<SystemSettings>(`${BASE}/platform/settings/smtp`, { method: 'PUT', body: input }),
  setSmtpMode: (mode: 'mailpit' | 'install') =>
    apiFetch<SystemSettings>(`${BASE}/platform/settings/smtp/mode`, { method: 'PUT', body: { mode } }),
  testSmtp: (to: string) =>
    apiFetch<{ ok: boolean; mode: string; message: string }>(`${BASE}/platform/settings/smtp/test`, { method: 'POST', body: { to } }),
  saveGeneral: (input: { signupEnabled?: boolean; passwordMethod?: PasswordMethod; inviteTtlHours?: number }) =>
    apiFetch<SystemSettings>(`${BASE}/platform/settings/general`, { method: 'PUT', body: input }),
  saveSite: (input: SiteInput) => apiFetch<SystemSettings>(`${BASE}/platform/settings/site`, { method: 'PUT', body: input }),

  // --- Anonim: herkese açık site sayfaları ---
  publicSite: (signal?: AbortSignal) => apiFetch<SiteSettings>(`${BASE}/public/site`, { signal, anonymous: true }),
}

export const passwordLinkKeys = {
  list: ['password-links'] as const,
  method: ['password-method'] as const,
  system: ['system-settings'] as const,
}

export function usePasswordLinks(enabled = true) {
  return useQuery({ queryKey: passwordLinkKeys.list, queryFn: ({ signal }) => passwordLinkApi.list(signal), enabled })
}

export function usePasswordMethod(enabled = true) {
  return useQuery({ queryKey: passwordLinkKeys.method, queryFn: ({ signal }) => passwordLinkApi.method(signal), enabled })
}

export function useSystemSettings() {
  return useQuery({ queryKey: passwordLinkKeys.system, queryFn: ({ signal }) => passwordLinkApi.systemSettings(signal) })
}

/** Liste değiştiren işlemler için ortak mutasyon (listeyi tazeler). */
export function useLinkMutation<TIn, TOut>(fn: (input: TIn) => Promise<TOut>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => void qc.invalidateQueries({ queryKey: passwordLinkKeys.list }),
  })
}
