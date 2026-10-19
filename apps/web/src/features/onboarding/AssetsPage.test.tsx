// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import type { Asset, Employee } from '@/api/types'
import { mockApi, renderWithProviders, type ApiCall } from '@/test/render'
import { AssetsPage } from './AssetsPage'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const employees: Employee[] = [
  { id: 'e1', firstName: 'Ayşe', lastName: 'Yılmaz', email: 'ayse@acme.com.tr', hireDate: '2023-03-01', status: 0, assignments: [] },
]

function asset(id: string, assetTag: string, type: Asset['type'], extra: Partial<Asset> = {}): Asset {
  return { id, assetTag, type, model: null, serialNumber: null, status: 'Available', assignedEmployeeId: null, assignedOn: null, ...extra }
}

const all = [
  asset('a1', 'DMB-001', 'Laptop', { model: 'ThinkPad' }),
  asset('a2', 'DMB-002', 'Phone', { status: 'Assigned', assignedEmployeeId: 'e1', assignedOn: '2026-09-01' }),
]

/** Sunucu taklidi: q / qTypes / qEmployeeIds / status parametrelerini uygular. */
function pageResponder({ path }: ApiCall) {
  const p = new URLSearchParams(path.split('?')[1] ?? '')
  const q = p.get('q')?.toLocaleLowerCase('tr-TR')
  const types = p.get('qTypes')?.split(',') ?? []
  const people = p.get('qEmployeeIds')?.split(',') ?? []
  const status = p.get('status')
  const items = all.filter(
    (a) =>
      (!q || `${a.assetTag} ${a.model ?? ''}`.toLocaleLowerCase('tr-TR').includes(q) || types.includes(a.type)
        || (a.assignedEmployeeId != null && people.includes(a.assignedEmployeeId))) &&
      (status == null || a.status === status),
  )
  return { items, total: items.length, page: Number(p.get('page')), pageSize: Number(p.get('pageSize')) }
}

function setup() {
  const api = mockApi({
    'GET /api/employee/employees': employees,
    'GET /api/onboarding/assets': pageResponder,
    'GET /api/onboarding/assets/returns-due': [],
    'GET /api/onboarding/assets/maintenance/due': [],
  })
  const view = renderWithProviders(<AssetsPage />, { roles: ['hr-admin'], route: '/panel/zimmet' })
  const params = () => api.callsTo('GET', '/api/onboarding/assets').map((c) => new URLSearchParams(c.path.split('?')[1]))
  return { ...view, api, params }
}

describe('AssetsPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('envanteri sunucudan sayfalı ister ve zimmetli kişiyi gösterir', async () => {
    const { params } = setup()
    const row = (await screen.findByText('DMB-002')).closest('tr')!
    expect(await within(row).findByText('Ayşe Yılmaz')).toBeTruthy()
    expect(screen.getByText('DMB-001')).toBeTruthy()
    expect(params().some((p) => p.get('page') === '1' && p.get('pageSize') === '12')).toBe(true)
  })

  it('arama sunucuya tür ve zimmetli kişi kimlikleriyle gider', async () => {
    const { user, params } = setup()
    await screen.findByText('DMB-001')

    await user.type(screen.getByRole('textbox', { name: 'Demirbaş no, model, seri no veya kişi' }), 'ayşe')

    await waitFor(() => expect(screen.queryByText('DMB-001')).toBeNull())
    expect(screen.getByText('DMB-002')).toBeTruthy()
    expect(params().some((p) => p.get('q') === 'ayşe' && p.get('qEmployeeIds') === 'e1' && p.get('page') === '1')).toBe(true)

    await user.clear(screen.getByRole('textbox', { name: 'Demirbaş no, model, seri no veya kişi' }))
    await user.type(screen.getByRole('textbox', { name: 'Demirbaş no, model, seri no veya kişi' }), 'telefon')
    await waitFor(() => expect(params().some((p) => p.get('q') === 'telefon' && p.get('qTypes') === 'Phone')).toBe(true))
  })

  it('sütun sıralaması sunucuya gönderilir', async () => {
    const { user, params } = setup()
    await screen.findByText('DMB-001')
    await user.click(screen.getByRole('button', { name: /Durum/ }))
    await waitFor(() => expect(params().some((p) => p.get('sort') === 'status')).toBe(true))
  })
})
