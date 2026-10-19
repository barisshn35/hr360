/**
 * Bileşen testleri için ortak düzen.
 *
 * Kullanım: test dosyası `// @vitest-environment jsdom` ile başlar ve ağı modül
 * sınırında keser (gerçek HTTP yok):
 *
 *   vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))
 *
 * Ardından `mockApi({...})` uç → yanıt eşlemesini kurar, `renderWithProviders`
 * bileşeni uygulamadaki sağlayıcılarla (sorgu istemcisi, yönlendirici, oturum,
 * bildirim, onay penceresi) sarar. Dil her zaman Türkçedir.
 */
import type { ReactElement, ReactNode } from 'react'
import { afterEach, vi } from 'vitest'
import { cleanup, render } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { AuthContext, type AuthContextValue } from '@/auth/AuthContext'
import { hasPermission, type Permission, type Role } from '@/auth/roles'
import { ToastProvider } from '@/components/ui/Toast'
import { ConfirmProvider } from '@/components/ui/Confirm'
import { ApiError, apiFetch } from '@/api/client'
import { setDictionary } from '@/lib/i18n'

// Testler sözlüksüz Türkçe kaynak metinle çalışır.
setDictionary({}, 'tr')

// jsdom'da olmayan tarayıcı API'leri (Radix ve motion bunlara dokunur).
if (typeof window !== 'undefined') {
  if (!window.matchMedia) {
    window.matchMedia = (query: string) =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      }) as MediaQueryList
  }
  if (!('ResizeObserver' in window)) {
    class RO {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    ;(window as unknown as { ResizeObserver: typeof RO }).ResizeObserver = RO
  }
  window.scrollTo = () => {}
  const proto = Element.prototype as unknown as Record<string, unknown>
  proto.hasPointerCapture ??= () => false
  proto.setPointerCapture ??= () => {}
  proto.releasePointerCapture ??= () => {}
  proto.scrollIntoView ??= () => {}
}

// globals kapalı: Testing Library kendi temizliğini kaydedemez.
afterEach(() => {
  cleanup()
  try {
    localStorage.clear()
  } catch {
    /* depolama yok */
  }
})

/* --------------------------------- Ağ taklidi -------------------------------- */

export interface ApiCall {
  method: string
  path: string
  body: unknown
}

/** Yanıt: sabit değer ya da çağrıya göre değer üreten işlev (hata için `throw`). */
type Responder = unknown | ((call: ApiCall) => unknown)

/**
 * `"GET /api/leave/leave-settings"` biçimli anahtarlar; yol sorgu dizesi olmadan eşleşir.
 * Anahtar `*` ile biterse önek eşleşmesi yapılır. Tanımsız uç 404 döner (sorgu hata durumuna düşer).
 */
export function mockApi(routes: Record<string, Responder>) {
  const calls: ApiCall[] = []
  const entries = Object.entries(routes).map(([key, value]) => {
    const [method, pattern] = key.split(' ')
    return { method, pattern, value }
  })
  vi.mocked(apiFetch).mockImplementation(async (path: string, options: { method?: string; body?: unknown } = {}) => {
    const method = (options.method ?? 'GET').toUpperCase()
    const call: ApiCall = { method, path, body: options.body }
    calls.push(call)
    const bare = path.split('?')[0]
    const hit = entries.find(
      (e) => e.method === method && (e.pattern.endsWith('*') ? bare.startsWith(e.pattern.slice(0, -1)) : bare === e.pattern),
    )
    if (!hit) throw new ApiError(404, `Taklit edilmemiş uç: ${method} ${path}`)
    return (typeof hit.value === 'function' ? (hit.value as (c: ApiCall) => unknown)(call) : hit.value) as never
  })
  return {
    calls,
    /** Belirli yöntem + yol (sorgusuz) için yapılan çağrılar. */
    callsTo: (method: string, path: string) => calls.filter((c) => c.method === method && c.path.split('?')[0] === path),
  }
}

/* --------------------------------- Oturum ---------------------------------- */

export function fakeAuth(roles: Role[] = ['employee'], overrides: Partial<AuthContextValue> = {}): AuthContextValue {
  return {
    status: 'authenticated',
    user: { id: 'kc-user-1', username: 'test', fullName: 'Test Kullanıcı', email: 'test@ornek.com.tr', roles, initials: 'TK' },
    error: null,
    login: () => {},
    logout: () => {},
    accountUrl: '#',
    can: (p: Permission) => hasPermission(roles, p),
    hasRole: (r: Role) => roles.includes(r),
    roles,
    tenantSlug: 'acme',
    tenant: null,
    tenantLoading: false,
    tenantError: null,
    canSwitchTenant: false,
    availableTenants: [],
    switchTenant: () => {},
    ...overrides,
  }
}

/* --------------------------------- Çizim ----------------------------------- */

export function renderWithProviders(
  ui: ReactElement,
  { roles = ['employee'], route = '/', auth }: { roles?: Role[]; route?: string; auth?: Partial<AuthContextValue> } = {},
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } },
  })
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <AuthContext.Provider value={fakeAuth(roles, auth)}>
          <ToastProvider>
            <ConfirmProvider>{children}</ConfirmProvider>
          </ToastProvider>
        </AuthContext.Provider>
      </MemoryRouter>
    </QueryClientProvider>
  )
  const user = userEvent.setup()
  return { ...render(ui, { wrapper }), user, queryClient }
}
