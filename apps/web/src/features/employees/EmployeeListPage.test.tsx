// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import type { Role } from '@/auth/roles'
import type { Employee } from '@/api/types'
import { mockApi, renderWithProviders, type ApiCall } from '@/test/render'
import { EmployeeListPage } from './EmployeeListPage'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const companies = [
  {
    id: 'c1',
    name: 'Acme',
    taxNumber: null,
    createdAt: '2024-01-01',
    departments: [
      { id: 'd1', name: 'Yazılım' },
      { id: 'd2', name: 'Muhasebe' },
    ],
  },
]

function employee(id: string, firstName: string, lastName: string, departmentId: string, positionTitle: string, status: 0 | 1 | 2 = 0): Employee {
  return {
    id,
    firstName,
    lastName,
    email: `${firstName.toLowerCase()}@acme.com.tr`,
    hireDate: '2023-03-01',
    status,
    assignments: [{ id: `a-${id}`, departmentId, positionTitle, effectiveFrom: '2023-03-01', effectiveTo: null }],
  }
}

const all = [
  employee('e1', 'Ayşe', 'Yılmaz', 'd1', 'Kıdemli Geliştirici'),
  employee('e2', 'Mehmet', 'Demir', 'd2', 'Muhasebe Uzmanı', 1),
]

/** Sunucu taklidi: q / qDepartmentIds / status parametrelerini uygular. */
function pageResponder(source: Employee[]) {
  return ({ path }: ApiCall) => {
    const p = new URLSearchParams(path.split('?')[1] ?? '')
    const q = p.get('q')?.toLocaleLowerCase('tr-TR')
    const deps = p.get('qDepartmentIds')?.split(',') ?? []
    const status = p.get('status')
    const items = source.filter(
      (e) =>
        (!q || `${e.firstName} ${e.lastName} ${e.email}`.toLocaleLowerCase('tr-TR').includes(q) || e.assignments.some((a) => deps.includes(a.departmentId))) &&
        (status == null || String(e.status) === status),
    )
    return { items, total: items.length, page: 1, pageSize: 12 }
  }
}

function setup({ roles = ['manager'] as Role[], source = all } = {}) {
  const api = mockApi({
    'GET /api/organization/companies': companies,
    'GET /api/employee/employees': pageResponder(source),
  })
  const view = renderWithProviders(
    <Routes>
      <Route path="/panel/calisanlar" element={<EmployeeListPage />} />
      <Route path="/panel/calisanlar/:id" element={<p>Çalışan detay sayfası</p>} />
    </Routes>,
    { roles, route: '/panel/calisanlar' },
  )
  return { ...view, api }
}

describe('EmployeeListPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('sunucudan gelen çalışanları departman ve pozisyonla listeler', async () => {
    setup()
    const row = (await screen.findByText('Ayşe Yılmaz')).closest('tr')!
    expect(within(row).getByText('ayşe@acme.com.tr')).toBeTruthy()
    expect(within(row).getByText('Yazılım')).toBeTruthy()
    expect(within(row).getAllByText('Kıdemli Geliştirici').length).toBeGreaterThan(0)
    expect(within(row).getByText('Aktif')).toBeTruthy()

    const second = screen.getByText('Mehmet Demir').closest('tr')!
    expect(within(second).getByText('İzinde')).toBeTruthy()
  })

  it('arama sunucuya ad ve eşleşen departman kimlikleriyle gider', async () => {
    const { user, api } = setup()
    await screen.findByText('Ayşe Yılmaz')

    await user.type(screen.getByRole('textbox', { name: 'Ad, soyad, e-posta veya departman' }), 'muhasebe')

    await waitFor(() => expect(screen.queryByText('Ayşe Yılmaz')).toBeNull())
    expect(screen.getByText('Mehmet Demir')).toBeTruthy()
    const searched = api.callsTo('GET', '/api/employee/employees').map((c) => new URLSearchParams(c.path.split('?')[1]))
    expect(searched.some((p) => p.get('q') === 'muhasebe' && p.get('qDepartmentIds') === 'd2')).toBe(true)
  })

  it('aramayla eşleşme yoksa arama boş durumunu gösterir', async () => {
    const { user } = setup()
    await screen.findByText('Ayşe Yılmaz')

    await user.type(screen.getByRole('textbox', { name: 'Ad, soyad, e-posta veya departman' }), 'zzz')

    expect(await screen.findByText('Aramanızla eşleşen kayıt yok')).toBeTruthy()
  })

  it('hiç çalışan yoksa ilk kaydı ekleme çağrısı yapar', async () => {
    setup({ source: [] })
    expect(await screen.findByText('Çalışan kaydı yok')).toBeTruthy()
    // Başlıktaki ve boş durumdaki "Yeni çalışan" düğmeleri
    expect(screen.getAllByRole('button', { name: /Yeni çalışan/ })).toHaveLength(2)
  })

  it('satıra tıklayınca çalışan detayına gider', async () => {
    const { user } = setup()
    await user.click(await screen.findByText('Mehmet Demir'))
    expect(await screen.findByText('Çalışan detay sayfası')).toBeTruthy()
  })

  it('ekleme izni olmayan rol (muhasebe) ekleme ve içe aktarma düğmelerini görmez', async () => {
    setup({ roles: ['accounting'] })
    await screen.findByText('Ayşe Yılmaz')
    expect(screen.queryByRole('button', { name: /Yeni çalışan/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /Excel'den içe aktar/ })).toBeNull()
  })

  it('yönetici rolü ekleme ve içe aktarma düğmelerini görür', async () => {
    setup({ roles: ['manager'] })
    await screen.findByText('Ayşe Yılmaz')
    expect(screen.getByRole('button', { name: /Yeni çalışan/ })).toBeTruthy()
    expect(screen.getByRole('button', { name: /Excel'den içe aktar/ })).toBeTruthy()
  })
})
