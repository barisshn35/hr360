// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { mockApi, renderWithProviders } from '@/test/render'
import { NewLeaveRequestModal } from './NewLeaveRequestModal'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const balance = (remainingDays: number) => [
  { id: 'b1', employeeId: 'emp-1', type: 'Annual', year: 2026, entitledDays: 14, usedDays: 14 - remainingDays, pendingDays: 0, remainingDays },
]

function setup(remainingDays = 10) {
  const api = mockApi({
    'GET /api/employee/employees/me': { id: 'emp-1', firstName: 'Ayşe', lastName: 'Yılmaz' },
    'GET /api/leave/leave-balances': balance(remainingDays),
    'GET /api/leave/public-holidays': [],
    'GET /api/leave/leave-settings': { dayHours: 8 },
    'GET /api/workflow/workflows/definitions/hidden-fields': [],
    'GET /api/leave/leave-requests/team-conflict': { enabled: false, exceeds: false, thresholdPercent: 30 },
    'POST /api/leave/leave-requests': ({ body }: { body: unknown }) => ({ id: 'lr-1', ...(body as object) }),
  })
  const onClose = vi.fn()
  const view = renderWithProviders(<NewLeaveRequestModal open onClose={onClose} />, { roles: ['employee'] })
  return { ...view, api, onClose }
}

const dialog = () => screen.getByRole('dialog', { name: 'Yeni izin talebi' })
const setDate = (label: RegExp, value: string) => fireEvent.change(within(dialog()).getByLabelText(label), { target: { value } })

describe('NewLeaveRequestModal', () => {
  beforeEach(() => vi.clearAllMocks())

  it('tarih girilmeden gönderilince Türkçe hata gösterir ve API çağrılmaz', async () => {
    const { user, api } = setup()
    await screen.findByText(/Kalan bakiye/)

    await user.click(within(dialog()).getByRole('button', { name: /Talebi gönder/ }))

    const errors = await screen.findAllByText('Başlangıç ve bitiş tarihi zorunlu.')
    expect(errors.length).toBeGreaterThan(0)
    expect(api.callsTo('POST', '/api/leave/leave-requests')).toHaveLength(0)
  })

  it('bitiş başlangıçtan önceyse ters aralık hatası verir', async () => {
    const { user, api } = setup()
    await screen.findByText(/Kalan bakiye/)
    setDate(/Başlangıç/, '2026-11-04')
    setDate(/Bitiş/, '2026-11-02')

    await user.click(within(dialog()).getByRole('button', { name: /Talebi gönder/ }))

    expect((await screen.findAllByText('Bitiş tarihi başlangıçtan önce olamaz.')).length).toBeGreaterThan(0)
    expect(api.callsTo('POST', '/api/leave/leave-requests')).toHaveLength(0)
  })

  it('geçerli aralıkta iş günü sayısıyla talebi gönderir ve pencereyi kapatır', async () => {
    const { user, api, onClose } = setup()
    await screen.findByText(/Kalan bakiye/)
    // 2 Kasım 2026 Pazartesi → 6 Kasım Cuma: 5 iş günü
    setDate(/Başlangıç/, '2026-11-02')
    setDate(/Bitiş/, '2026-11-06')
    await user.type(within(dialog()).getByLabelText(/Gerekçe/), '  Aile ziyareti  ')

    await user.click(within(dialog()).getByRole('button', { name: /Talebi gönder/ }))

    await waitFor(() => expect(api.callsTo('POST', '/api/leave/leave-requests')).toHaveLength(1))
    expect(api.callsTo('POST', '/api/leave/leave-requests')[0].body).toEqual({
      employeeId: 'emp-1',
      type: 'Annual',
      startDate: '2026-11-02',
      endDate: '2026-11-06',
      days: 5,
      reason: 'Aile ziyareti',
      hours: undefined,
    })
    await waitFor(() => expect(onClose).toHaveBeenCalled())
    expect(await screen.findByText('İzin talebi oluşturuldu, onay zincirine gönderildi')).toBeTruthy()
  })

  it('bakiye yetersizse uyarır ve gönder düğmesini kilitler', async () => {
    setup(2)
    await screen.findByText(/Kalan bakiye/)
    setDate(/Başlangıç/, '2026-11-02')
    setDate(/Bitiş/, '2026-11-06')

    expect(await screen.findByText(/Bakiyeniz 3 gün yetersiz/)).toBeTruthy()
    expect((within(dialog()).getByRole('button', { name: /Talebi gönder/ }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('saatlik izinde saat boşsa ve 0,5 adımına uymuyorsa hata verir, geçerli saati güne çevirip gönderir', async () => {
    const { user, api } = setup()
    await screen.findByText(/Kalan bakiye/)
    setDate(/Başlangıç/, '2026-11-03')
    setDate(/Bitiş/, '2026-11-03')

    // Tek günlük talepte "Süre" seçimi açılır.
    await user.click(within(dialog()).getByRole('combobox', { name: /Süre/ }))
    await user.click(await screen.findByRole('option', { name: 'Saatlik' }))
    await user.click(within(dialog()).getByRole('button', { name: /Talebi gönder/ }))
    expect((await screen.findAllByText('Saat girin (ör. 2,5).')).length).toBeGreaterThan(0)

    const hours = within(dialog()).getByLabelText(/^Saat/)
    await user.type(hours, '2,3')
    expect((await screen.findAllByText('Saat 0,5’lik adımlarla girilmeli.')).length).toBeGreaterThan(0)

    await user.clear(hours)
    await user.type(hours, '4')
    await user.click(within(dialog()).getByRole('button', { name: /Talebi gönder/ }))

    await waitFor(() => expect(api.callsTo('POST', '/api/leave/leave-requests')).toHaveLength(1))
    // Günlük 8 saat: 4 saat = 0,5 gün
    expect(api.callsTo('POST', '/api/leave/leave-requests')[0].body).toMatchObject({ hours: 4, days: 0.5, startDate: '2026-11-03' })
  })
})
