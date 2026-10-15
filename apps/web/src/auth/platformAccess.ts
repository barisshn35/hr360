/**
 * Güvenlik dalgası 2A (break-glass): platform yöneticisi kiracı verisine yalnızca süreli,
 * gerekçeli erişim izniyle ulaşır. Seçilen kiracı her istekte `X-HR360-Tenant` başlığıyla
 * gönderilir; servislerin kiracı kapısı etkin izin yoksa 403 (platform_access_grant_required)
 * döner, izin varsa isteği yalnızca o kiracıyla sınırlar. Başlık başka rollerde yok sayılır.
 */
export const PLATFORM_TENANT_HEADER = 'X-HR360-Tenant'

/** Platform yöneticisinin hedef kiracısı: jetondaki organizasyon, yoksa seçili kiracı. */
export function platformTenantFor(
  roles: readonly string[],
  orgSlug: string | null | undefined,
  preferred: string | null | undefined,
): string | null {
  if (!roles.includes('platform-admin')) return null
  const slug = (orgSlug || preferred || '').trim().toLowerCase()
  return /^[a-z0-9][a-z0-9-]{0,63}$/.test(slug) ? slug : null
}

/**
 * Platform yöneticisinin seçtiği kiracı (sekme ömrü boyunca). Keycloak'ın "tercih edilen kiracı"
 * anahtarından AYRIDIR: o anahtar girişte `organization:<slug>` kapsamı olarak istenir ve
 * organizasyon üyesi olmayan platform yöneticisi için Keycloak "invalid_scope" ile girişi reddeder.
 */
const GRANT_TENANT_KEY = 'hr360.platformTenant'

export function readGrantTenant(): string | null {
  try {
    return window.sessionStorage.getItem(GRANT_TENANT_KEY)
  } catch {
    return null
  }
}

export function writeGrantTenant(slug: string | null) {
  try {
    if (slug) window.sessionStorage.setItem(GRANT_TENANT_KEY, slug)
    else window.sessionStorage.removeItem(GRANT_TENANT_KEY)
  } catch {
    /* depolama kapalı: kiracı seçimi bu sekmede tutulamaz */
  }
}

/**
 * İsteğin kiracı verisine dokunup dokunmadığı (servislerin PlatformAccessGate muafiyetlerinin
 * istemci karşılığı). tenant-service kapıya girmez. ML (/ml/...) kapı dışındadır.
 */
export function needsPlatformGrant(path: string): boolean {
  const m = /^\/api\/([a-z-]+)(\/[^?#]*)?/.exec(path)
  if (!m) return false
  if (m[1] === 'tenant') return false
  const inner = `/api${m[2] ?? ''}`
  const exempt: string[] = []
  // notification: kişinin kendi arayüz tercihleri (kiracı verisi değil; sunucudaki muafiyetle aynı).
  if (m[1] === 'notification') exempt.push('/api/notifications/ui-prefs')
  // governance: paylaşılan modelin platform düzeyi uçları (sunucudaki muafiyetle aynı).
  if (m[1] === 'governance') exempt.push('/api/model/retrain', '/api/model/versions', '/api/model/promote', '/api/model/rollback', '/api/model/calibration')
  return !exempt.some((x) => inner === x || inner.startsWith(`${x}/`))
}

/**
 * Platform yöneticisinin etkin izinlerinin kısa ömürlü önbelleği. İzin yokken kiracı uçlarına
 * istek hiç gönderilmez (sunucu yine 403 döner; tarayıcı konsolu ve denetim kaydı gereksiz yere
 * dolmasın). İzin açılınca/kapatılınca invalidatePlatformGrants() çağrılır.
 */
interface GrantRow { tenantSlug: string; active: boolean; expiresAt: string }
let grantCache: { at: number; rows: GrantRow[] } | null = null
let grantInflight: Promise<GrantRow[]> | null = null
const GRANT_TTL_MS = 30_000

export function invalidatePlatformGrants() {
  grantCache = null
}

export async function hasPlatformGrant(tenant: string, load: () => Promise<GrantRow[]>): Promise<boolean> {
  const now = Date.now()
  if (!grantCache || now - grantCache.at > GRANT_TTL_MS) {
    grantInflight ??= load().finally(() => {
      grantInflight = null
    })
    try {
      grantCache = { at: Date.now(), rows: await grantInflight }
    } catch {
      // İzin listesi alınamadı: karar sunucuya bırakılır (istek gönderilir).
      return true
    }
  }
  return grantCache.rows.some((g) => g.active && g.tenantSlug === tenant && Date.parse(g.expiresAt) > now)
}
