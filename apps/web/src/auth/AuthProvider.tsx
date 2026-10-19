import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { clearDeviceData } from '@/lib/push'
import { ApiError } from '@/api/client'
import { useMyTenant, useTenants } from '@/api/queries-tenant'
import { applyTenantBrandColor } from '@/lib/tenant-brand'
import {
  initKeycloak,
  keycloak,
  readTenantSlug,
  scopeForTenant,
  writePreferredTenant,
} from './keycloak'
import { hasPermissionIn, type Permission, type Role } from './roles'
import { useModules } from '@/lib/modules'
import { platformTenantFor, readGrantTenant, writeGrantTenant } from './platformAccess'
import { AuthContext, type AuthContextValue, type AuthUser } from './AuthContext'
import { clearSessionExpired, markSessionExpired } from './sessionExpiry'
import { lang, tx, appLocale } from '@/lib/i18n'

interface TokenClaims {
  sub?: string
  preferred_username?: string
  name?: string
  given_name?: string
  family_name?: string
  email?: string
  realm_access?: { roles?: string[] }
}

function initialsOf(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.charAt(0).toLocaleUpperCase(appLocale))
    .join('')
}

/** Keycloak token'ından uygulama kullanıcısını türetir. */
function readUser(): AuthUser | null {
  const parsed = keycloak.tokenParsed as TokenClaims | undefined
  if (!parsed?.sub) return null

  const fullName =
    parsed.name ||
    [parsed.given_name, parsed.family_name].filter(Boolean).join(' ') ||
    parsed.preferred_username ||
    tx('Kullanıcı')

  return {
    id: parsed.sub,
    username: parsed.preferred_username ?? parsed.sub,
    fullName,
    email: parsed.email ?? null,
    roles: (parsed.realm_access?.roles ?? []) as Role[],
    initials: initialsOf(fullName) || 'HR',
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthContextValue['status']>('loading')
  const [user, setUser] = useState<AuthUser | null>(null)
  const [tenantSlug, setTenantSlug] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    let wasAuthenticated = false

    const sync = () => {
      const u = readUser()
      setUser(u)
      // Platform yöneticisi kiracıya bağlı değildir; Kiracılar ekranında seçtiği şirket
      // (süreli erişim izniyle) şirket ekranlarını açar. İzin servislerde denetlenir.
      setTenantSlug(readTenantSlug() ?? platformTenantFor(u?.roles ?? [], null, readGrantTenant()))
    }

    // Token yenilendiğinde/süresi dolduğunda React state'i senkron tut.
    keycloak.onAuthRefreshSuccess = sync
    keycloak.onAuthLogout = () => {
      // Açık bir oturum kendiliğinden düştüyse (süre doldu, başka sekmede çıkış)
      // panel "oturum sona erdi" ekranını gösterir; ilk açılışta işaretlenmez.
      if (wasAuthenticated) markSessionExpired()
      setUser(null)
      setTenantSlug(null)
      setStatus('anonymous')
    }
    keycloak.onTokenExpired = () => {
      void keycloak.updateToken(30).catch(() => keycloak.clearToken())
    }

    initKeycloak()
      .then((authenticated) => {
        if (cancelled) return
        wasAuthenticated = authenticated
        if (authenticated) {
          clearSessionExpired()
          sync()
        } else {
          setUser(null)
          setTenantSlug(null)
        }
        setStatus(authenticated ? 'authenticated' : 'anonymous')
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof Error
            ? e.message
            : tx('Kimlik sağlayıcıya ulaşılamadı. Ağ bağlantınızı kontrol edin.'),
        )
        setStatus('error')
      })

    return () => {
      cancelled = true
    }
  }, [])

  const roles = useMemo(() => user?.roles ?? [], [user])
  const { tenancy } = useModules()
  const can = useCallback((permission: Permission) => hasPermissionIn(roles, permission, tenancy), [roles, tenancy])

  const isAuthenticated = status === 'authenticated'
  // Tek şirket kipinde seçilecek başka şirket yok.
  const canSwitchTenant = isAuthenticated && tenancy !== 'single' && can('platform:manage')

  // Kiracıya bağlı olmayan oturum (platform.admin) için /my-tenant her sayfada 404
  // dönüyordu; kiracı bilgisi yoksa istenmez.
  const myTenant = useMyTenant(isAuthenticated && Boolean(tenantSlug))
  const allTenants = useTenants({}, canSwitchTenant)

  // Şirket bir marka rengi ayarlamışsa çalışma zamanında uygula;
  // oturum kapanınca (data undefined olunca) varsayılana döner.
  useEffect(() => {
    applyTenantBrandColor(myTenant.data?.primaryColorHex)
  }, [myTenant.data?.primaryColorHex])

  const login = useCallback((redirectTo?: string) => {
    void keycloak.login({
      redirectUri: redirectTo ? new URL(redirectTo, window.location.origin).href : window.location.href,
      locale: lang,
      // organization scope'u burada da şart: giriş buradan başlıyorsa
      // init'teki scope hiç devreye girmiyor.
      scope: scopeForTenant(null),
    })
  }, [])

  const logout = useCallback(() => {
    writePreferredTenant(null)
    writeGrantTenant(null)
    // KVKK: bu cihazdaki çevrimdışı kuyruk ve anlık bildirim aboneliği silinir.
    void clearDeviceData().finally(() => keycloak.logout({ redirectUri: window.location.origin }))
  }, [])

  /**
   * Kiracı değiştirme yalnızca platform yöneticisinde açık.
   *
   * Veri izolasyonu JWT'deki `organization` claim'ine bağlı olduğu için
   * kiracıyı değiştirmenin tek yolu YENİ TOKEN almak — istemci tarafında
   * bir bayrak çevirmek hiçbir şeyi değiştirmez, backend yine eski kiracıyı
   * görür. Bu yüzden seçim saklanıp Keycloak'a `organization:<slug>`
   * scope'uyla yeniden gidiliyor.
   */
  const switchTenant = useCallback((slug: string) => {
    // Platform yöneticisi organizasyon üyesi değildir: `organization:<slug>` kapsamıyla yeniden
    // giriş Keycloak'ta "invalid_scope" ile reddedilir. Seçilen kiracı bu sekmede tutulur ve
    // istekler X-HR360-Tenant başlığıyla gider; veri erişimi süreli izinle açılır (Kiracılar).
    if ((keycloak.tokenParsed as TokenClaims | undefined)?.realm_access?.roles?.includes('platform-admin')) {
      writeGrantTenant(slug)
      window.location.assign('/panel')
      return
    }
    writePreferredTenant(slug)
    void keycloak.login({
      redirectUri: window.location.href,
      locale: lang,
      scope: scopeForTenant(slug),
    })
  }, [])

  const tenantError = useMemo(() => {
    const err = myTenant.error
    if (!err) return null
    if (err instanceof ApiError) {
      return err.status === 404 || err.status === 502
        ? tx('Kiracı servisine ulaşılamıyor. Gateway’de /api/tenant/* yönlendirmesi tanımlı olmayabilir.')
        : err.message
    }
    return tx('Kiracı bilgisi alınamadı.')
  }, [myTenant.error])

  const value = useMemo<AuthContextValue>(() => {
    // Oturum yokken createAccountUrl() exception fırlatır — güvenli sarmalayıcı.
    let accountUrl = '#'
    if (isAuthenticated) {
      try {
        accountUrl = keycloak.createAccountUrl() ?? '#'
      } catch {
        accountUrl = '#'
      }
    }

    return {
      status,
      user,
      error,
      login,
      logout,
      accountUrl,
      can,
      hasRole: (role: Role) => roles.includes(role),
      roles,
      tenantSlug,
      tenant: myTenant.data ?? null,
      tenantLoading: myTenant.isPending && isAuthenticated,
      tenantError,
      canSwitchTenant,
      availableTenants: allTenants.data ?? [],
      switchTenant,
    }
  }, [
    status, user, error, login, logout, can, roles, tenantSlug,
    myTenant.data, myTenant.isPending, tenantError, canSwitchTenant,
    allTenants.data, switchTenant, isAuthenticated,
  ])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
