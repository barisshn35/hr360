// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import type { Role } from '@/auth/roles'
import type { LeaveRequest } from '@/api/leave'
import { mockApi, renderWithProviders } from '@/test/render'
import { LeavePage } from './LeavePage'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const mine: LeaveRequest = {
  id: 'lr-1',
  employeeId: 'emp-1',
  type: 'Annual',
  status: 'Submitted',
  startDate: '2026-11-02',
  endDate: '2026-11-06',
  days: 5,
  hours: null,
  reason: 'Aile ziyareti',
  workflowRequestId: 'w1',
  createdAt: '2026-10-15T09:00:00Z',
}

function setup(roles: Role[] = ['employee']) {
  // Takvim, köprü günü vb. yan paneller taklit edilmez: 404 ile kendi hata durumlarına düşer.
  const api = mockApi({
    'GET /api/employee/employees/me': { id: 'emp-1', firstName: 'Ayşe', lastName: 'Yılmaz' },
    'GET /api/leave/leave-requests': { items: [mine], total: 1, page: 1, pageSize: 10 },
    'POST /api/leave/leave-requests/lr-1/cancel': { ...mine, status: 'Cancelled' },
  })
  const view = renderWithProviders(<LeavePage />, { roles, route: '/panel/izin' })
  return { ...view, api }
}

describe('LeavePage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('bekleyen talepleri listeler ve sunucudan "Onayda" sekmesiyle ister', async () => {
    const { api } = setup()
    const row = (await screen.findByText('Aile ziyareti')).closest('tr')!
    expect(within(row).getByText('Yıllık izin')).toBeTruthy()
    expect(within(row).getByText('5')).toBeTruthy()
    expect(within(row).getByText('Onayda')).toBeTruthy()
    const p = new URLSearchParams(api.callsTo('GET', '/api/leave/leave-requests')[0].path.split('?')[1])
    expect(p.get('status')).toBe('Submitted')
  })

  it('çalışan kendi bekleyen talebini onay penceresinden sonra iptal eder', async () => {
    const { user, api } = setup()
    const row = (await screen.findByText('Aile ziyareti')).closest('tr')!

    await user.click(within(row).getByRole('button', { name: 'Satır işlemleri' }))
    await user.click(await screen.findByRole('menuitem', { name: 'Talebi iptal et' }))

    const confirm = await screen.findByRole('alertdialog', { name: 'İzin talebi iptal edilsin mi?' })
    expect(api.callsTo('POST', '/api/leave/leave-requests/lr-1/cancel')).toHaveLength(0)
    await user.click(within(confirm).getByRole('button', { name: 'Talebi iptal et' }))

    await waitFor(() => expect(api.callsTo('POST', '/api/leave/leave-requests/lr-1/cancel')).toHaveLength(1))
    expect(await screen.findByText('Talep iptal edildi')).toBeTruthy()
  })

  it('"Yeni izin talebi" formu açar', async () => {
    const { user } = setup()
    await user.click(screen.getByRole('button', { name: /Yeni izin talebi/ }))
    expect(await screen.findByRole('dialog', { name: 'Yeni izin talebi' })).toBeTruthy()
  })

  it.each<[string, Role[]]>([['çalışan', ['employee']], ['yönetici', ['manager']]])('%s rolü bakiye yönetimi düğmelerini görmez', async (_ad, roles) => {
    setup(roles)
    await screen.findByText('Aile ziyareti')
    for (const name of ['Resmi tatiller', 'Yasal hak ve devir', 'İzin ayarları', 'Bakiye tanımla']) {
      expect(screen.queryByRole('button', { name: new RegExp(name) })).toBeNull()
    }
  })

  it('İK rolü bakiye yönetimi düğmelerini görür', async () => {
    setup(['hr-admin'])
    await screen.findByText('Aile ziyareti')
    for (const name of ['Resmi tatiller', 'Yasal hak ve devir', 'İzin ayarları', 'Bakiye tanımla']) {
      expect(screen.getByRole('button', { name: new RegExp(name) })).toBeTruthy()
    }
  })
})
