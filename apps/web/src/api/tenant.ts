import { apiFetch, apiUploadFile, qs } from './client'
import { tx } from '@/lib/i18n'
import type { PasswordLinkView } from './password-links'

/* ================================ Tenant Service ================================
 * Çok kiracılılığın backend karşılığı. Diğer 14 modülden farklı olarak
 * `registration` uçları ANONİM — şirket kaydı sırasında henüz token yok.
 * ============================================================================== */

const BASE = '/api/tenant'

export type TenantStatus = 'Pending' | 'Active' | 'Suspended' | 'Cancelled'

export const tenantStatusLabels: Record<TenantStatus, string> = {
  Pending: tx('Hazırlanıyor'),
  Active: tx('Aktif'),
  Suspended: tx('Askıda'),
  Cancelled: tx('İptal edildi'),
}

export interface Tenant {
  id: string
  name: string
  slug: string
  status: TenantStatus
  employeeCount?: number | null
  emailDomain?: string | null
  taxNumber?: string | null
  adminEmail?: string | null
  createdAt: string
  suspendedAt?: string | null
  suspensionReason?: string | null
  logoUrl?: string | null
  primaryColorHex?: string | null
  hasCustomSmtp?: boolean
  smtpFromAddress?: string | null
  smtpFromName?: string | null
  /** Platform yöneticisinin şirket görünümü (break-glass): salt okunur özet. */
  readOnly?: boolean
  /** Etkin süreli erişim izni (yoksa null). */
  platformAccess?: { grantId: string; expiresAt: string } | null
}

/**
 * Kayıt sırasında arka planda çalışan adımlar (realm grubu, admin kullanıcı,
 * varsayılan roller, örnek veri...). Bir adım patlarsa kiracı `Pending`te
 * takılı kalır — platform panelindeki zaman çizelgesi tek teşhis yolu.
 */
export interface ProvisioningLogEntry {
  id?: string
  step: string
  status: 'Pending' | 'Succeeded' | 'Failed' | string
  message?: string | null
  startedAt?: string | null
  completedAt?: string | null
}

export interface TenantDetail extends Tenant {
  provisioningLog?: ProvisioningLogEntry[] | null
}

/* ------------------------------- Kayıt (anonim) ------------------------------- */

export interface PublicConfig {
  signupEnabled: boolean
  /** "Parolamı unuttum" e-postayla mı (yoksa "İK yöneticinize başvurun"). */
  passwordResetByEmail?: boolean
}

export interface TenantRegistrationInput {
  companyName: string
  adminEmail: string
  adminFullName?: string
  slug?: string
  emailDomain?: string
  taxNumber?: string
}

export interface TenantRegistrationResult {
  tenantId: string
  slug: string
  companyName: string
  status: TenantStatus
  message?: string | null
  /** Parola linki (gösterilecekse link dolu) — bkz. api/password-links. */
  passwordLink?: PasswordLinkView | null
}

export interface SlugAvailability {
  slug: string
  available: boolean
}

/* --------------------------------- İstemci ---------------------------------- */

export interface TenantFilters {
  status?: TenantStatus
}

export interface TeamMember {
  /** Çalışan kaydı olmayan hesapta (ör. yalnızca yönetim için açılmış yönetici) boş. */
  employeeId: string | null
  hasEmployeeRecord?: boolean
  keycloakUserId: string | null
  firstName: string
  lastName: string
  email: string
  hasLoginAccess: boolean
  roles: string[]
  /** Standart rolden bağımsız, tek tek atanmış ekstra izinler (yalnızca
   * Şirket/Platform Yöneticisi atayabilir) - roles.ts Permission kodları. */
  extraPermissions: string[]
}

export const tenantApi = {
  /** Oturum açmış kullanıcının bağlı olduğu kiracı. */
  myTenant: (signal?: AbortSignal) => apiFetch<Tenant>(`${BASE}/my-tenant`, { signal }),

  /** Şirket adını değiştirir. */
  renameCompany: (name: string) =>
    apiFetch<{ message: string; name: string }>(`${BASE}/my-tenant/name`, {
      method: 'PUT',
      body: { name },
    }),

  /** Beyaz etiketleme ayarları (kendi SMTP sunucusu, ana renk). */
  updateBranding: (input: {
    smtpHost?: string
    smtpPort?: number
    smtpUser?: string
    smtpPassword?: string
    smtpFromAddress?: string
    smtpFromName?: string
    primaryColorHex?: string
  }) =>
    apiFetch<{ message: string; hasCustomSmtp: boolean; primaryColorHex: string | null }>(
      `${BASE}/my-tenant/branding`,
      { method: 'PUT', body: input },
    ),

  /** Logo yükler. PNG/JPEG/SVG/WebP, en fazla 2 MB. */
  uploadLogo: (file: File) =>
    apiUploadFile<{ message: string; logoUrl: string }>(`${BASE}/my-tenant/logo`, file),

  deleteLogo: () =>
    apiFetch<{ message: string }>(`${BASE}/my-tenant/logo`, { method: 'DELETE' }),

  /** Tenant'taki tüm çalışanlar ve varsa Keycloak rolleri. */
  members: (signal?: AbortSignal) =>
    apiFetch<TeamMember[]>(`${BASE}/my-tenant/members`, { signal }),

  /** Çalışana giriş erişimi verir (Keycloak hesabı + süreli parola linki; e-posta açıksa e-postayla da). */
  inviteMember: (employeeId: string) =>
    apiFetch<{ message: string; keycloakUserId: string; passwordLink?: PasswordLinkView | null }>(
      `${BASE}/my-tenant/members/${employeeId}/invite`,
      { method: 'POST' },
    ),

  assignRole: (keycloakUserId: string, role: string) =>
    apiFetch<{ message: string }>(`${BASE}/my-tenant/members/roles`, {
      method: 'POST',
      body: { keycloakUserId, role },
    }),

  removeRole: (keycloakUserId: string, role: string) =>
    apiFetch<{ message: string }>(`${BASE}/my-tenant/members/roles`, {
      method: 'DELETE',
      body: { keycloakUserId, role },
    }),

  /**
   * Tek bir izni, kullanıcının rolünden bağımsız olarak doğrudan atar/
   * kaldırır - backend'de sadece tenant-admin/platform-admin çağırabilir
   * (RequireTenantAdmin). roles.ts'teki Permission tipiyle aynı stringleri
   * bekler (örn. "compensation:view").
   */
  assignExtraPermission: (keycloakUserId: string, permission: string) =>
    apiFetch<{ message: string }>(`${BASE}/my-tenant/members/permissions`, {
      method: 'POST',
      body: { keycloakUserId, permission },
    }),

  removeExtraPermission: (keycloakUserId: string, permission: string) =>
    apiFetch<{ message: string }>(`${BASE}/my-tenant/members/permissions`, {
      method: 'DELETE',
      body: { keycloakUserId, permission },
    }),

  list: (filters: TenantFilters = {}, signal?: AbortSignal) =>
    apiFetch<Tenant[]>(`${BASE}/tenants${qs(filters)}`, { signal }),

  get: (id: string, signal?: AbortSignal) =>
    apiFetch<TenantDetail>(`${BASE}/tenants/${id}`, { signal }),

  suspend: (id: string, reason: string) =>
    apiFetch<Tenant>(`${BASE}/tenants/${id}/suspend`, { method: 'POST', body: { reason } }),

  reactivate: (id: string) =>
    apiFetch<Tenant>(`${BASE}/tenants/${id}/reactivate`, { method: 'POST' }),

  /** Platform yöneticisi yeni kiracı oluşturur (kayıtla aynı akış). */
  create: (input: TenantRegistrationInput) =>
    apiFetch<TenantRegistrationResult>(`${BASE}/tenants`, { method: 'POST', body: input }),

  // --- Anonim uçlar: token GÖNDERİLMEZ (kayıt sırasında kullanıcı henüz yok) ---

  /** Kurulum ayarları: herkese açık şirket kaydı açık mı (PUBLIC_SIGNUP_ENABLED). */
  publicConfig: (signal?: AbortSignal) =>
    apiFetch<PublicConfig>(`${BASE}/public/config`, { signal, anonymous: true }),

  register: (input: TenantRegistrationInput) =>
    apiFetch<TenantRegistrationResult>(`${BASE}/registration`, {
      method: 'POST',
      body: input,
      anonymous: true,
    }),

  slugAvailable: (slug: string, signal?: AbortSignal) =>
    apiFetch<SlugAvailability>(`${BASE}/registration/slug-available${qs({ slug })}`, {
      signal,
      anonymous: true,
    }),
}

/**
 * Şirket adından URL güvenli slug türetir. Kullanıcı düzenleyebilir;
 * bu yalnızca ilk öneri.
 */
export function slugify(value: string): string {
  const map: Record<string, string> = {
    ç: 'c', Ç: 'c', ğ: 'g', Ğ: 'g', ı: 'i', İ: 'i',
    ö: 'o', Ö: 'o', ş: 's', Ş: 's', ü: 'u', Ü: 'u',
  }
  return value
    .split('')
    .map((ch) => map[ch] ?? ch)
    .join('')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40)
}
