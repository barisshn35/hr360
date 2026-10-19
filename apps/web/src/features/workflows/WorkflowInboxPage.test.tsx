// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import type { Role } from '@/auth/roles'
import type { ApprovalStep, Workflow } from '@/api/types'
import { mockApi, renderWithProviders } from '@/test/render'
import { WorkflowInboxPage } from './WorkflowInboxPage'
import { DecisionModal } from './DecisionModal'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const ME = 'mgr-1'

function step(id: string, approverEmployeeId: string, order = 1): ApprovalStep {
  return { id, order, approverEmployeeId, decision: 'Pending', comment: null, decidedAt: null }
}

const pending: Workflow[] = [
  {
    id: 'w1',
    type: 'LeaveRequest',
    status: 'Pending',
    requesterEmployeeId: 'emp-1',
    subject: 'Yıllık izin 3-7 Kasım',
    createdAt: '2026-10-15T09:00:00Z',
    slaDueAt: null,
    steps: [step('s1', ME)],
  },
  {
    // Onaycısı başka biri: toplu kararda atlanmalı.
    id: 'w2',
    type: 'ExpenseClaim',
    status: 'Pending',
    requesterEmployeeId: 'emp-2',
    subject: 'Ankara seyahati',
    createdAt: '2026-10-14T09:00:00Z',
    slaDueAt: null,
    steps: [step('s2', 'other-mgr')],
  },
]

function setup(roles: Role[] = ['manager']) {
  const api = mockApi({
    'GET /api/employee/employees/me': { id: ME, firstName: 'Can', lastName: 'Öz' },
    'GET /api/employee/employees/directory': [
      { id: 'emp-1', firstName: 'Ayşe', lastName: 'Yılmaz', fullName: 'Ayşe Yılmaz' },
      { id: 'emp-2', firstName: 'Mehmet', lastName: 'Demir', fullName: 'Mehmet Demir' },
    ],
    'GET /api/workflow/workflows': pending,
    'POST /api/workflow/workflows/bulk-decide': ({ body }: { body: unknown }) => {
      const { items } = body as { items: Array<{ workflowId: string; stepId: string }> }
      return { done: items.length, results: items.map((i) => ({ ...i, ok: true, error: null })) }
    },
  })
  const view = renderWithProviders(<WorkflowInboxPage />, { roles, route: '/panel/onaylar' })
  return { ...view, api }
}

const rowOf = (text: string) => screen.getByText(text).closest('tr')!

describe('WorkflowInboxPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('bekleyen talepleri talep eden adıyla listeler', async () => {
    const { api } = setup()
    expect(await screen.findByText('Yıllık izin 3-7 Kasım')).toBeTruthy()
    await waitFor(() => expect(within(rowOf('Yıllık izin 3-7 Kasım')).getAllByText('Ayşe Yılmaz').length).toBeGreaterThan(0))
    expect(within(rowOf('Ankara seyahati')).getAllByText('Masraf Talebi').length).toBeGreaterThan(0)
    // Varsayılan sekme "Bekleyen": liste durum filtresiyle istenir.
    expect(api.callsTo('GET', '/api/workflow/workflows')[0].path).toContain('status=Pending')
  })

  it('toplu ret gerekçesiz/kısa gerekçeyle gönderilmez, geçerli gerekçeyle yalnız sırası gelen adımı reddeder', async () => {
    const { user, api } = setup()
    await screen.findByText('Yıllık izin 3-7 Kasım')
    await waitFor(() => expect(within(rowOf('Yıllık izin 3-7 Kasım')).getAllByText('Ayşe Yılmaz').length).toBeGreaterThan(0))

    await user.click(screen.getByRole('checkbox', { name: 'Sayfadaki tüm satırları seç' }))
    expect(await screen.findByText('Kararınızı bekleyen: 1')).toBeTruthy()

    await user.click(screen.getByRole('button', { name: /Toplu reddet/ }))
    const modal = await screen.findByRole('dialog', { name: '1 talep reddedilsin mi?' })
    const reason = within(modal).getByLabelText(/Gerekçe/)

    await user.type(reason, 'ab')
    await user.click(within(modal).getByRole('button', { name: 'Toplu reddet' }))
    expect(await within(modal).findByText('Ret gerekçesi zorunlu (en az 3 karakter).')).toBeTruthy()
    expect(api.callsTo('POST', '/api/workflow/workflows/bulk-decide')).toHaveLength(0)

    await user.type(reason, 'c bütçe yok')
    await user.click(within(modal).getByRole('button', { name: 'Toplu reddet' }))

    await waitFor(() => expect(api.callsTo('POST', '/api/workflow/workflows/bulk-decide')).toHaveLength(1))
    expect(api.callsTo('POST', '/api/workflow/workflows/bulk-decide')[0].body).toEqual({
      items: [{ workflowId: 'w1', stepId: 's1' }],
      decision: 'Rejected',
      comment: 'abc bütçe yok',
    })
    // Sonuç penceresi: biri reddedildi, onaycısı olunmayan talep atlandı.
    const result = await screen.findByRole('dialog', { name: 'Toplu ret sonucu' })
    expect(within(result).getByText('Reddedildi')).toBeTruthy()
    expect(within(result).getByText('Sırası size gelmemiş ya da onaycısı değilsiniz')).toBeTruthy()
  })

  it('toplu onay onay penceresinden sonra API\'ye gider', async () => {
    const { user, api } = setup()
    await screen.findByText('Yıllık izin 3-7 Kasım')

    await user.click(within(rowOf('Yıllık izin 3-7 Kasım')).getByRole('checkbox', { name: 'Satırı seç' }))
    await user.click(await screen.findByRole('button', { name: /Toplu onayla/ }))

    const confirm = await screen.findByRole('alertdialog', { name: '1 talep onaylansın mı?' })
    expect(api.callsTo('POST', '/api/workflow/workflows/bulk-decide')).toHaveLength(0)
    await user.click(within(confirm).getByRole('button', { name: 'Toplu onayla' }))

    await waitFor(() => expect(api.callsTo('POST', '/api/workflow/workflows/bulk-decide')).toHaveLength(1))
    expect(api.callsTo('POST', '/api/workflow/workflows/bulk-decide')[0].body).toMatchObject({
      items: [{ workflowId: 'w1', stepId: 's1' }],
      decision: 'Approved',
    })
    expect(await screen.findByText('1 talep karara bağlandı')).toBeTruthy()
  })

  it('karar yetkisi olmayan çalışan seçim kutusu, vekâlet ve gecikmiş sekmesini görmez', async () => {
    setup(['employee'])
    await screen.findByText('Yıllık izin 3-7 Kasım')
    expect(screen.queryByRole('checkbox')).toBeNull()
    expect(screen.queryByRole('button', { name: /Vekâlet/ })).toBeNull()
    expect(screen.queryByRole('tab', { name: 'Süresi geçen' })).toBeNull()
    // Talep açma izni çalışanda da var.
    expect(screen.getByRole('button', { name: /Yeni talep/ })).toBeTruthy()
  })

  it('yönetici vekâlet düğmesini ve gecikmiş sekmesini görür', async () => {
    setup(['manager'])
    await screen.findByText('Yıllık izin 3-7 Kasım')
    expect(screen.getByRole('button', { name: /Vekâlet/ })).toBeTruthy()
    expect(screen.getByRole('tab', { name: 'Süresi geçen' })).toBeTruthy()
  })
})

describe('DecisionModal', () => {
  const s = step('s1', ME, 2)

  it('ret kararında gerekçe en az 3 karakter olmalı', async () => {
    const onConfirm = vi.fn()
    const { user } = renderWithProviders(<DecisionModal state={{ step: s, approve: false }} onClose={() => {}} onConfirm={onConfirm} pending={false} />)
    const dialog = screen.getByRole('dialog', { name: 'Adımı reddet' })
    expect(within(dialog).getByText('2. onay adımı için kararınızı kaydedin.')).toBeTruthy()

    await user.click(within(dialog).getByRole('button', { name: 'Reddet' }))
    expect(within(dialog).getByText('Ret gerekçesi zorunlu (en az 3 karakter).')).toBeTruthy()

    await user.type(within(dialog).getByLabelText(/Gerekçe/), '  ab  ')
    await user.click(within(dialog).getByRole('button', { name: 'Reddet' }))
    expect(onConfirm).not.toHaveBeenCalled()

    await user.clear(within(dialog).getByLabelText(/Gerekçe/))
    await user.type(within(dialog).getByLabelText(/Gerekçe/), ' Belge eksik ')
    await user.click(within(dialog).getByRole('button', { name: 'Reddet' }))
    expect(onConfirm).toHaveBeenCalledWith('Belge eksik')
  })

  it('onay kararında gerekçe isteğe bağlıdır', async () => {
    const onConfirm = vi.fn()
    const { user } = renderWithProviders(<DecisionModal state={{ step: s, approve: true }} onClose={() => {}} onConfirm={onConfirm} pending={false} />)
    await user.click(within(screen.getByRole('dialog', { name: 'Adımı onayla' })).getByRole('button', { name: 'Onayla' }))
    expect(onConfirm).toHaveBeenCalledWith(undefined)
  })

  it('karar sürerken düğmeler kilitlenir', () => {
    renderWithProviders(<DecisionModal state={{ step: s, approve: true }} onClose={() => {}} onConfirm={() => {}} pending />)
    const dialog = screen.getByRole('dialog', { name: 'Adımı onayla' })
    expect((within(dialog).getByRole('button', { name: /Onayla/ }) as HTMLButtonElement).disabled).toBe(true)
    expect((within(dialog).getByRole('button', { name: 'Vazgeç' }) as HTMLButtonElement).disabled).toBe(true)
  })
})
