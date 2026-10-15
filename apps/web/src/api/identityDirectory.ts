import { ApiError, apiFetch } from './client'

/** Y26 dizin sağlama (SCIM 2.0 + LDAP/AD) ve G28 özel alan adı — tenant-service. */
const BASE = '/api/tenant/my-tenant'

export interface LdapSettings {
  enabled: boolean
  autoSync: boolean
  url: string | null
  bindDn: string | null
  hasPassword: boolean
  baseDn: string | null
  userFilter: string | null
  usernameAttr: string
  emailAttr: string
  departmentAttr: string | null
  titleAttr: string | null
  disabledAttr: string | null
  urlWarning: string | null
  lastSyncAt: string | null
  lastSyncTrigger: string | null
  lastSyncStatus: string | null
  lastSyncSummary: unknown
}

export interface DirectorySettings {
  sendInvitations: boolean
  dataMinimisation: string
  storedAttributes: string[]
  scimBaseUrl: string
  ldap: LdapSettings
  syncIntervalMinutes: number
  allowed: { username: string[]; email: string[]; department: string[]; disabled: string[]; title: string[] }
}

export interface LdapSettingsInput {
  enabled: boolean
  autoSync: boolean
  url?: string | null
  bindDn?: string | null
  /** undefined: değişmez, '': siler. */
  bindPassword?: string
  baseDn?: string | null
  userFilter?: string | null
  usernameAttr?: string | null
  emailAttr?: string | null
  departmentAttr?: string | null
  titleAttr?: string | null
  disabledAttr?: string | null
}

export interface ScimToken {
  id: string
  name: string
  tokenPrefix: string
  createdAt: string
  createdBy: string | null
  lastUsedAt: string | null
  revokedAt: string | null
  active: boolean
}

export interface CreatedScimToken {
  id: string
  name: string
  tokenPrefix: string
  createdAt: string
  /** Yalnızca bu yanıtta görünür; sunucuda saklanmaz. */
  token: string
  message: string
}

export interface SyncResult {
  dryRun: boolean
  warning: string | null
  connectionWarning: string | null
  entries: number
  created: number
  updated: number
  disabled: number
  enabled: number
  skipped: number
  unchanged: number
  actions: { kind: string; userName: string; email: string | null; detail: string | null }[]
  errors: string[]
  employeeRetried: number
  employeeFailed: number
}

export interface DirectoryUser {
  id: string
  source: 'scim' | 'ldap'
  userName: string
  givenName: string | null
  familyName: string | null
  email: string
  title: string | null
  department: string | null
  active: boolean
  employeeId: string | null
  employeeState: 'Pending' | 'Linked' | 'Failed'
  employeeError: string | null
  createdAt: string
  updatedAt: string
  deactivatedAt: string | null
}

export interface CustomDomain {
  id: string
  domain: string
  status: 'Pending' | 'Verified'
  createdAt: string
  verifiedAt: string | null
  lastCheckedAt: string | null
  lastCheckError: string | null
  txtName: string
  txtValue: string
  loginUrl: string
}

export interface PublicBranding {
  slug: string
  name: string
  logoUrl: string | null
  primaryColorHex: string | null
}

export const identityDirectoryApi = {
  settings: (signal?: AbortSignal) => apiFetch<DirectorySettings>(`${BASE}/directory/settings`, { signal }),
  saveSettings: (body: { sendInvitations?: boolean; ldap?: LdapSettingsInput }) =>
    apiFetch<{ message: string; warning: string | null; settings: DirectorySettings }>(`${BASE}/directory/settings`, { method: 'PUT', body }),
  tokens: (signal?: AbortSignal) => apiFetch<ScimToken[]>(`${BASE}/directory/scim-tokens`, { signal }),
  createToken: (name: string) => apiFetch<CreatedScimToken>(`${BASE}/directory/scim-tokens`, { method: 'POST', body: { name } }),
  revokeToken: (id: string) => apiFetch<{ message: string }>(`${BASE}/directory/scim-tokens/${id}`, { method: 'DELETE' }),
  testLdap: () => apiFetch<{ ok: boolean; entries: number; sample: string[]; warning: string | null }>(`${BASE}/directory/ldap/test`, { method: 'POST' }),
  syncLdap: (dryRun: boolean, forceDisable = false) =>
    apiFetch<SyncResult>(`${BASE}/directory/ldap/sync`, { method: 'POST', body: { dryRun, forceDisable } }),
  users: (signal?: AbortSignal) => apiFetch<{ failed: number; items: DirectoryUser[] }>(`${BASE}/directory/users`, { signal }),
  retryEmployees: () => apiFetch<{ linked: number; failed: number }>(`${BASE}/directory/users/retry`, { method: 'POST' }),

  domains: (signal?: AbortSignal) => apiFetch<{ items: CustomDomain[] }>(`${BASE}/domains`, { signal }),
  addDomain: (domain: string) => apiFetch<CustomDomain>(`${BASE}/domains`, { method: 'POST', body: { domain } }),
  verifyDomain: (id: string) =>
    apiFetch<{ message: string; warning: string | null; domain: CustomDomain }>(`${BASE}/domains/${id}/verify`, { method: 'POST' }),
  removeDomain: (id: string) => apiFetch<{ message: string }>(`${BASE}/domains/${id}`, { method: 'DELETE' }),

  /** G28: giriş ekranı — doğrulanmış özel alan adıysa şirket markası, değilse null. Anonim. */
  publicBranding: async (host: string, signal?: AbortSignal): Promise<PublicBranding | null> => {
    try {
      // Tanınmayan host: 204 (apiFetch undefined döner) → marka yok.
      return (await apiFetch<PublicBranding | undefined>(`/api/tenant/public/branding?host=${encodeURIComponent(host)}`, { signal, anonymous: true })) ?? null
    } catch (e) {
      if (e instanceof ApiError && (e.status === 404 || e.status === 429)) return null
      throw e
    }
  },
}
