// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import { hasPermissionIn, type Permission, type Role } from '@/auth/roles'
import { mockApi, renderWithProviders } from '@/test/render'
import { filterByPermission, flattenItems, locate, navGroups, navGroupsFor } from '@/components/layout/nav-config'
import { MultiTenantOnly } from '@/features/shared/ModuleGate'
import { NoTenantNotice } from '@/features/shared/FeatureGate'
import { PlatformDashboard } from '@/features/dashboard/PlatformDashboard'
import { SignInPage } from '@/features/auth/SignInPage'
import { SystemSettingsPage } from '@/features/platform/SystemSettingsPage'
import { FaqPage } from '@/features/site/SitePages'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const SITE = 'GET /api/tenant/public/site'
const site = (tenancy?: 'single' | 'multi') => ({ faq: [], searchIndexing: false, tenancy })

describe('tek şirket kipi: menü', () => {
  const ids = (tenancy: 'single' | 'multi', roles: string[]) => {
    const can = (p: Permission) => hasPermissionIn(roles, p, tenancy)
    return filterByPermission(navGroupsFor(tenancy), can, roles, true).map((g) => [g.id, g.items.map((i) => i.id)] as const)
  }

  it('çok şirketli kipte ağaç değişmez', () => {
    expect(navGroupsFor('multi')).toBe(navGroups)
  })

  it('tek şirket kipinde Platform bölümü yok; Sistem ayarları ve Bakım Yönetim altında, Kiracılar yok', () => {
    const groups = navGroupsFor('single')
    expect(groups.map((g) => g.id)).not.toContain('platform')
    const admin = groups.find((g) => g.id === 'admin')!.items.map((i) => i.id)
    expect(admin.slice(-2)).toEqual(['system-settings', 'maintenance'])
    expect(flattenItems(groups).map((i) => i.id)).not.toContain('tenants')
    expect(locate('/panel/platform/bakim', groups).group?.id).toBe('admin')
  })

  it('şirket yöneticisi (single-platform-manage) tek şirket kipinde bakım ekranlarını görür', () => {
    const roles = ['tenant-admin', 'single-platform-manage']
    const admin = new Map(ids('single', roles)).get('admin')!
    expect(admin).toContain('system-settings')
    expect(admin).toContain('maintenance')
  })

  it('single-platform-manage çok şirketli kipte hiçbir şey açmaz', () => {
    const roles = ['tenant-admin', 'single-platform-manage']
    expect(hasPermissionIn(roles, 'platform:manage', 'multi')).toBe(false)
    const all = ids('multi', roles).flatMap(([, items]) => items)
    expect(all).not.toContain('system-settings')
    expect(all).not.toContain('tenants')
  })

  it('rol başka izin vermez; düz şirket yöneticisi bakımı görmez', () => {
    expect(hasPermissionIn(['employee', 'single-platform-manage'], 'tenant:manage', 'single')).toBe(false)
    expect(hasPermissionIn(['tenant-admin'], 'platform:manage', 'single')).toBe(false)
  })
})

describe('tek şirket kipi: yönlendirme', () => {
  const routes = (
    <Routes>
      <Route path="/kayit" element={<MultiTenantOnly to="/giris"><p>kayıt sihirbazı</p></MultiTenantOnly>} />
      <Route path="/giris" element={<p>giriş sayfası</p>} />
    </Routes>
  )

  it('tek şirket kipinde /kayit girişe yönlenir', async () => {
    mockApi({ [SITE]: site('single') })
    renderWithProviders(routes, { route: '/kayit' })
    expect(await screen.findByText('giriş sayfası')).toBeTruthy()
  })

  it('çok şirketli kipte /kayit açılır', async () => {
    mockApi({ [SITE]: site('multi') })
    renderWithProviders(routes, { route: '/kayit' })
    expect(await screen.findByText('kayıt sihirbazı')).toBeTruthy()
  })
})

describe('tek şirket kipi: giriş sayfası', () => {
  const anon = { status: 'anonymous' as const, user: null }

  it('kayıt bölümü gizlenir, şirket adı gösterilir', async () => {
    mockApi({
      [SITE]: site('single'),
      'GET /api/tenant/public/config': { signupEnabled: false, tenancy: 'single', companyName: 'Örnek A.Ş.' },
    })
    renderWithProviders(<SignInPage />, { route: '/giris', auth: anon })
    expect(await screen.findByText('Örnek A.Ş.')).toBeTruthy()
    expect(screen.queryByText('Şirketinizi kaydedin')).toBeNull()
  })

  it('çok şirketli kipte kayıt açıkken bölüm görünür', async () => {
    mockApi({ [SITE]: site('multi'), 'GET /api/tenant/public/config': { signupEnabled: true } })
    renderWithProviders(<SignInPage />, { route: '/giris', auth: anon })
    expect(await screen.findByText('Şirketinizi kaydedin')).toBeTruthy()
  })
})

describe('tek şirket kipi: sistem ayarları', () => {
  const settings = {
    smtp: {
      mode: 'env', configured: false, host: null, port: null, user: null, hasPassword: false, fromAddress: 'hr@ornek.com',
      fromName: 'HR360', installHost: null, installSelf: false, tenantsWithOwnSmtp: 0,
    },
    signupEnabled: false,
    passwordMethod: 'invite',
    inviteTtlHours: 72,
    site: {
      operatorName: null, address: null, email: null, phone: null, kep: null, verbisNo: null,
      privacyMd: null, termsMd: null, faq: [], searchIndexing: false,
    },
    updatedAt: null,
    updatedBy: null,
  }

  it.each([
    ['single', false],
    ['multi', true],
  ] as const)('%s kipinde herkese açık kayıt anahtarı görünür: %s', async (tenancy, visible) => {
    mockApi({ [SITE]: site(tenancy), 'GET /api/tenant/platform/settings': settings })
    renderWithProviders(<SystemSettingsPage />, { roles: ['platform-admin'] })
    await screen.findByText('Varsayılan parola belirleme yöntemi')
    await waitFor(() =>
      expect(screen.queryByRole('switch', { name: 'Herkese açık şirket kaydı' }) !== null).toBe(visible),
    )
  })
})

describe('tek şirket kipi: platform yöneticisi', () => {
  const platform: { roles: Role[]; auth: { tenantSlug: null } } = { roles: ['platform-admin'], auth: { tenantSlug: null } }

  it('şirket ekranı bildirimi yonetici hesabını önerir', async () => {
    mockApi({ [SITE]: site('single') })
    renderWithProviders(<NoTenantNotice />, platform)
    expect(await screen.findByText(/Tek şirket kipi: şirket ekranları için yonetici hesabıyla giriş yapın/)).toBeTruthy()
    expect(screen.queryByText('Kiracılara git')).toBeNull()
  })

  it('çok şirketli kipte Kiracılara git bağlantısı kalır', async () => {
    mockApi({ [SITE]: site('multi') })
    renderWithProviders(<NoTenantNotice />, platform)
    expect(await screen.findByText('Kiracılara git')).toBeTruthy()
  })

  it('genel bakış kiracı listesi yerine bakım kısayolları gösterir', async () => {
    mockApi({ [SITE]: site('single'), 'GET /api/tenant/tenants': [] })
    renderWithProviders(<PlatformDashboard />, platform)
    expect(await screen.findByText(/yonetici hesabıyla giriş yapın/)).toBeTruthy()
    expect(screen.getByRole('link', { name: /Bakım/ })).toBeTruthy()
    expect(screen.queryByText('Toplam kiracı')).toBeNull()
  })
})

describe('tek şirket kipi: SSS', () => {
  it('şirket kaydı sorusu gizlenir', async () => {
    mockApi({ [SITE]: site('single') })
    renderWithProviders(<FaqPage />, { auth: { status: 'anonymous', user: null } })
    expect(await screen.findByText('Nasıl giriş yaparım?')).toBeTruthy()
    await waitFor(() => expect(screen.queryByText('Şirketimi nasıl kaydederim?')).toBeNull())
  })

  it('çok şirketli kipte görünür', async () => {
    mockApi({ [SITE]: site('multi') })
    renderWithProviders(<FaqPage />, { auth: { status: 'anonymous', user: null } })
    expect(await screen.findByText('Şirketimi nasıl kaydederim?')).toBeTruthy()
  })
})
