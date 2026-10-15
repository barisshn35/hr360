import Keycloak from 'keycloak-js'
import { env } from '@/lib/env'
import { tx } from '@/lib/i18n'

/**
 * Tek Keycloak örneği. hr360-web public client'ı PKCE (S256) zorunlu.
 * React StrictMode iki kez init çağırdığı için init sözü modül düzeyinde önbelleklenir.
 */
export const keycloak = new Keycloak({
  url: env.keycloakUrl,
  realm: env.keycloakRealm,
  clientId: env.keycloakClientId,
})

/**
 * ÇOK KİRACILILIĞIN BEL KEMİĞİ.
 *
 * `organization` scope'u istenmezse Keycloak JWT'ye `organization` claim'ini
 * KOYMAZ. Backend'deki veri izolasyonu tamamen o claim'e dayandığı için
 * kullanıcı hata almaz — her listeyi BOŞ görür. Sessiz başarısızlık olduğu
 * için hata ayıklaması çok pahalı; bu yüzden scope tek sabitte tutuluyor ve
 * init/login/switch yollarının üçü de buradan okuyor.
 */
export const BASE_SCOPE = tx('openid organization')

/**
 * Platform yöneticisi başka bir kiracıya geçtiğinde talep edilen scope.
 * Keycloak Organizations, `organization:<alias>` biçimiyle belirli bir
 * organizasyonun claim'e yazılmasını destekliyor.
 */
export function scopeForTenant(slug?: string | null): string {
  return slug ? `openid organization:${slug}` : BASE_SCOPE
}

/** Seçili kiracı sekme ömrü boyunca hatırlanır — yenilemede geri düşmesin. */
const TENANT_KEY = 'hr360.tenant'

export function readPreferredTenant(): string | null {
  try {
    return window.sessionStorage.getItem(TENANT_KEY)
  } catch {
    return null
  }
}

export function writePreferredTenant(slug: string | null) {
  try {
    if (slug) window.sessionStorage.setItem(TENANT_KEY, slug)
    else window.sessionStorage.removeItem(TENANT_KEY)
  } catch {
    /* özel sekme / depolama kapalı — geçişsiz devam ederiz */
  }
}

let initPromise: Promise<boolean> | null = null

/** Sessiz SSO iframe'i yanıt vermezse (ör. redirect URI kayıtlı değilse) beklenen süre. */
const SILENT_SSO_TIMEOUT_MS = 6000

export function initKeycloak(): Promise<boolean> {
  initPromise ??= Promise.race([
    keycloak.init({
      onLoad: 'check-sso',
      pkceMethod: 'S256',
      scope: scopeForTenant(readPreferredTenant()),
      checkLoginIframe: false,
      silentCheckSsoRedirectUri: `${window.location.origin}/silent-check-sso.html`,
      enableLogging: import.meta.env.DEV,
    }),
    // Keycloak iframe'i hata sayfası döndürdüğünde postMessage gelmez ve init
    // asılı kalır. Bu durumda kullanıcıyı sonsuz yükleme yerine giriş ekranına düşür.
    new Promise<boolean>((resolve) => {
      window.setTimeout(() => {
        if (!keycloak.authenticated) {
          console.warn(
            '[HR360] Sessiz SSO kontrolü zaman aşımına uğradı. ' +
              'hr360-web client redirect URI listesinde ' +
              `${window.location.origin}/* kayıtlı mı kontrol edin.`,
          )
          resolve(false)
        }
      }, SILENT_SSO_TIMEOUT_MS)
    }),
  ])
  return initPromise
}

/**
 * Token'ı gerekiyorsa yeniler ve geçerli access token'ı döner.
 * 30 sn'den az ömrü kalmışsa yenilenir.
 */
export async function getValidToken(minValiditySeconds = 30): Promise<string | null> {
  if (!keycloak.authenticated) return null
  try {
    await keycloak.updateToken(minValiditySeconds)
    return keycloak.token ?? null
  } catch {
    // Refresh token da süresi dolmuşsa oturumu yeniden kurmak gerekir.
    return null
  }
}

/** JWT'deki `organization` claim'inden kiracı slug'ını okur. */
export function readTenantSlug(): string | null {
  const parsed = keycloak.tokenParsed as
    | { organization?: unknown }
    | undefined
  const claim = parsed?.organization

  // Keycloak sürümüne göre claim dizi ya da { "<slug>": {...} } nesnesi gelebilir.
  if (Array.isArray(claim)) {
    const first = claim[0]
    return typeof first === 'string' ? first : null
  }
  if (claim && typeof claim === 'object') {
    const keys = Object.keys(claim as Record<string, unknown>)
    return keys[0] ?? null
  }
  return typeof claim === 'string' ? claim : null
}
