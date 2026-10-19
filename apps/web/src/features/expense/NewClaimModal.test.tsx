// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { Role } from '@/auth/roles'
import { mockApi, renderWithProviders } from '@/test/render'
import { NewClaimModal } from './NewClaimModal'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

function setup(roles: Role[] = ['employee']) {
  const api = mockApi({
    'GET /api/employee/employees/me': { id: 'emp-1', firstName: 'Ayşe', lastName: 'Yılmaz' },
    'GET /api/expense/expense-policy': { kmRate: 5 },
    'POST /api/expense/expense-claims': ({ body }: { body: unknown }) => ({ id: 'c-1', status: 'Draft', ...(body as object) }),
  })
  const onClose = vi.fn()
  const view = renderWithProviders(<NewClaimModal open onClose={onClose} />, { roles })
  return { ...view, api, onClose }
}

const dialog = () => screen.getByRole('dialog', { name: 'Yeni masraf talebi' })
const submit = () => within(dialog()).getByRole('button', { name: /Taslağı oluştur/ })
const posted = (api: ReturnType<typeof mockApi>) => api.callsTo('POST', '/api/expense/expense-claims')

describe('NewClaimModal', () => {
  beforeEach(() => vi.clearAllMocks())

  it('boş formda başlık ve tutar hatalarını gösterir, API çağrılmaz', async () => {
    const { user, api } = setup()
    await screen.findByText('Beyan sizin adınıza oluşturulacak.')

    await user.click(submit())

    expect((await screen.findAllByText('Başlık en az 3 karakter olmalı.')).length).toBeGreaterThan(0)
    expect(screen.getAllByText('Her kalemin tutarı 0\'dan büyük olmalı.').length).toBeGreaterThan(0)
    expect(screen.getByText('Tutar girilmeli.')).toBeTruthy()
    expect(posted(api)).toHaveLength(0)
  })

  it('negatif tutarı reddeder', async () => {
    const { user, api } = setup()
    await user.type(within(dialog()).getByLabelText(/Başlık/), 'Ankara ziyareti')
    await user.type(within(dialog()).getByLabelText(/^Tutar/), '-50')

    await user.click(submit())

    expect(await screen.findByText('Değer 0\'dan büyük olmalı.')).toBeTruthy()
    expect(posted(api)).toHaveLength(0)
  })

  it('çok kalemli talepte toplamı gösterir ve virgüllü tutarlarla gönderir', async () => {
    const { user, api, onClose } = setup()
    await screen.findByText('Beyan sizin adınıza oluşturulacak.')
    await user.type(within(dialog()).getByLabelText(/Başlık/), '  Ekim seyahati  ')

    await user.click(within(dialog()).getByRole('button', { name: /Kalem ekle/ }))
    const amounts = within(dialog()).getAllByLabelText(/^Tutar/)
    const dates = within(dialog()).getAllByLabelText(/^Tarih/)
    expect(amounts).toHaveLength(2)
    await user.type(amounts[0], '123,45')
    await user.type(amounts[1], '10')
    fireEvent.change(dates[0], { target: { value: '2026-10-05' } })
    fireEvent.change(dates[1], { target: { value: '2026-10-06' } })
    await user.type(within(dialog()).getAllByLabelText(/^Açıklama/)[1], 'Taksi')

    expect(within(dialog()).getByText(/133,45/)).toBeTruthy()

    await user.click(submit())

    await waitFor(() => expect(posted(api)).toHaveLength(1))
    expect(posted(api)[0].body).toMatchObject({
      employeeId: 'emp-1',
      title: 'Ekim seyahati',
      currency: 'TRY',
      items: [
        { category: 'Travel', amount: 123.45, expenseDate: '2026-10-05', description: undefined },
        { category: 'Travel', amount: 10, expenseDate: '2026-10-06', description: 'Taksi' },
      ],
    })
    await waitFor(() => expect(onClose).toHaveBeenCalled())
    expect(await screen.findByText('Masraf talebi taslak olarak oluşturuldu')).toBeTruthy()
  })

  it('kilometre kaleminde tutar km × kilometre ücretiyle hesaplanır, km gönderilir', async () => {
    const { user, api } = setup()
    await screen.findByText('Beyan sizin adınıza oluşturulacak.')
    await user.type(within(dialog()).getByLabelText(/Başlık/), 'Müşteri ziyareti')

    await user.click(within(dialog()).getByRole('combobox', { name: /^Tür/ }))
    await user.click(await screen.findByRole('option', { name: 'Kilometre' }))
    await user.type(within(dialog()).getByRole('textbox', { name: /^Kilometre/ }), '100')

    // Politika: km başına 5 TL
    expect(within(dialog()).getByText(/km × ₺5 = ₺500$/)).toBeTruthy()

    await user.click(submit())
    await waitFor(() => expect(posted(api)).toHaveLength(1))
    const body = posted(api)[0].body as { items: Array<Record<string, unknown>> }
    expect(body.items[0]).toMatchObject({ category: 'Mileage', amount: 0, km: 100 })
  })

  it('İK rolünde çalışan seçici çıkar; seçilmeden gönderilemez', async () => {
    const { user, api } = setup(['hr-admin'])
    expect(screen.queryByText('Beyan sizin adınıza oluşturulacak.')).toBeNull()
    await user.type(within(dialog()).getByLabelText(/Başlık/), 'Eğitim gideri')
    await user.type(within(dialog()).getByLabelText(/^Tutar/), '250')

    await user.click(submit())

    expect((await screen.findAllByText('Çalışan seçilmeli.')).length).toBeGreaterThan(0)
    expect(posted(api)).toHaveLength(0)
  })
})
