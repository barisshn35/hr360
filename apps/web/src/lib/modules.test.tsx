// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import { hasPermission, type Permission, type Role } from '@/auth/roles'
import { mockApi, renderWithProviders } from '@/test/render'
import { MODULE_IDS, parseModules, useModules } from './modules'
import { filterByPermission, flattenItems, navGroups } from '@/components/layout/nav-config'
import { CommandPalette } from '@/components/layout/CommandPalette'
import { RequireModule } from '@/features/shared/ModuleGate'
import { useMyTodos } from '@/features/todos/useMyTodos'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const SITE = 'GET /api/tenant/public/site'
const site = (modules?: string[], tenancy?: string) => ({ faq: [], searchIndexing: false, modules, tenancy })

describe('parseModules', () => {
  it('alanlar yoksa (eski sunucu) hepsi açık, çok kiracılı', () => {
    for (const input of [null, undefined, site()]) {
      const r = parseModules(input as never)
      expect([...r.enabled].sort()).toEqual([...MODULE_IDS].sort())
      expect(r.tenancy).toBe('multi')
    }
  })
  it('liste verilirse yalnız onlar açık; bilinmeyen kimlikler atılır', () => {
    const r = parseModules(site(['learning', 'ml', 'bilinmeyen'], 'single') as never)
    expect([...r.enabled].sort()).toEqual(['learning', 'ml'])
    expect(r.tenancy).toBe('single')
  })
  it('boş liste: hepsi kapalı', () => expect(parseModules(site([]) as never).enabled.size).toBe(0))
})

function Probe() {
  const m = useModules()
  if (m.loading) return <p>yükleniyor</p>
  return <p data-testid="mods">{[...m.enabled].sort().join(',')}|{m.tenancy}|{String(m.isEnabled('recruitment'))}</p>
}

describe('useModules', () => {
  it('site ucu yanıt vermezse (taklit yok, 404) hepsi açık sayılır', async () => {
    mockApi({})
    renderWithProviders(<Probe />)
    await waitFor(() => expect(screen.getByTestId('mods').textContent).toBe(`${[...MODULE_IDS].sort().join(',')}|multi|true`))
  })
  it('sunucunun listesini kullanır', async () => {
    mockApi({ [SITE]: site(['learning'], 'single') })
    renderWithProviders(<Probe />)
    await waitFor(() => expect(screen.getByTestId('mods').textContent).toBe('learning|single|false'))
  })
})

describe('menü süzgeci', () => {
  const roles: Role[] = ['hr-admin']
  const can = (p: Permission) => hasPermission(roles, p)
  const ids = (enabled: (m: string) => boolean) =>
    flattenItems(filterByPermission(navGroups, can, roles, true, {}, enabled)).map((i) => i.id)

  it('hepsi açıkken bugünkü menüyle aynı', () => {
    expect(ids(() => true)).toEqual(flattenItems(filterByPermission(navGroups, can, roles, true, {})).map((i) => i.id))
  })
  it('kapalı modülün girdileri (alt girdileriyle) düşer, çekirdek kalır', () => {
    const list = ids((m) => m !== 'recruitment' && m !== 'expense' && m !== 'compensation')
    for (const gone of ['postings', 'candidates', 'my-interviews', 'refer', 'expense', 'travel', 'payroll', 'my-payslips', 'benefits'])
      expect(list).not.toContain(gone)
    for (const kept of ['leave', 'employees', 'cases', 'documents', 'my-signatures', 'doc-request', 'learning', 'perf-me'])
      expect(list).toContain(kept)
  })
  it('hiçbir modül yokken bile çekirdek gruplar durur', () => {
    const groups = filterByPermission(navGroups, can, roles, true, {}, () => false).map((g) => g.id)
    expect(groups).toEqual(expect.arrayContaining(['daily', 'people', 'admin']))
  })
})

describe('komut paleti', () => {
  it('hepsi açıkken (eski sunucu) modül sayfaları listede', async () => {
    mockApi({})
    renderWithProviders(<CommandPalette open onOpenChange={() => {}} />, { roles: ['hr-admin'] })
    expect((await screen.findAllByText('Mülakatlarım')).length).toBeGreaterThan(0)
    expect(screen.getAllByText('Seyahat').length).toBeGreaterThan(0)
  })
  it('kapalı modülün sayfaları "Git" listesinde ve eylemlerde yok', async () => {
    const api = mockApi({ [SITE]: site(['learning', 'performance', 'onboarding', 'timeshift', 'compensation', 'ml']) })
    renderWithProviders(<CommandPalette open onOpenChange={() => {}} />, { roles: ['hr-admin'] })
    await waitFor(() => expect(api.callsTo('GET', '/api/tenant/public/site').length).toBe(1))
    await waitFor(() => expect(screen.queryByText('Mülakatlarım')).toBeNull())
    expect(screen.queryByText('Seyahat')).toBeNull()
    expect(screen.queryByText('Masraf ekle')).toBeNull()
    expect(screen.getAllByText('İzin').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Bordrolarım').length).toBeGreaterThan(0)
  })
})

describe('rota kapısı', () => {
  const at = (roles: Role[]) =>
    renderWithProviders(
      <Routes>
        <Route path="/panel/ise-alim" element={<RequireModule module="recruitment"><p>ilan sayfası</p></RequireModule>} />
      </Routes>,
      { route: '/panel/ise-alim', roles },
    )

  it('kapalı modül: "kurulu değil" ekranı, çalışana yöneticiye başvurması söylenir', async () => {
    mockApi({ [SITE]: site(['learning']) })
    at(['employee'])
    expect(await screen.findByText('Bu modül kurulu değil')).toBeTruthy()
    expect(screen.getByTestId('module-not-installed').textContent).toContain('Yöneticinize başvurun.')
    expect(screen.queryByText('ilan sayfası')).toBeNull()
  })
  it('yöneticiye nereden açılacağı söylenir', async () => {
    mockApi({ [SITE]: site([]) })
    at(['tenant-admin'])
    expect((await screen.findByTestId('module-not-installed')).textContent).toContain('Platform › Bakım › Modüller')
  })
  it('açık modül (ya da eski sunucu): sayfa çizilir', async () => {
    mockApi({})
    at(['employee'])
    expect(await screen.findByText('ilan sayfası')).toBeTruthy()
  })
})

function TodosProbe() {
  const { isPending } = useMyTodos()
  return <p>{isPending ? 'bekliyor' : 'hazır'}</p>
}

describe('yapılacaklar', () => {
  const routes = {
    'GET /api/employee/employees/me': { id: 'e1', firstName: 'Ayşe', lastName: 'Yılmaz' },
  }
  const optionalCalls = (calls: { path: string }[]) =>
    calls.filter((c) => /^\/api\/(learning|onboarding|performance)\//.test(c.path)).map((c) => c.path.split('?')[0])

  it('kapalı modüllerin sorguları hiç atılmaz', async () => {
    const api = mockApi({ ...routes, [SITE]: site(['timeshift']) })
    renderWithProviders(<TodosProbe />, { roles: ['hr-admin'] })
    await waitFor(() => expect(api.callsTo('GET', '/api/employee/employees/me').length).toBeGreaterThan(0))
    await screen.findByText('hazır')
    expect(optionalCalls(api.calls)).toEqual([])
  })
  it('açıkken (eski sunucu) atılır', async () => {
    const api = mockApi(routes)
    renderWithProviders(<TodosProbe />, { roles: ['hr-admin'] })
    await waitFor(() => expect(optionalCalls(api.calls)).toEqual(expect.arrayContaining(['/api/learning/learning-due/me', '/api/performance/feedback360/mine'])))
    await waitFor(() => expect(optionalCalls(api.calls).some((p) => p.startsWith('/api/onboarding/'))).toBe(true))
  })
})
