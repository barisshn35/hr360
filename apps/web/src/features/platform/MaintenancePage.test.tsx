// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { OpsPitr, OpsSnapshot } from '@/api/platformOps'
import { mockApi, renderWithProviders } from '@/test/render'
import { MaintenancePage } from './MaintenancePage'

vi.mock('@/api/client', async (orig) => ({ ...(await orig<typeof import('@/api/client')>()), apiFetch: vi.fn() }))

const B = '/api/tenant/platform/ops'
const twoDaysAgo = new Date(Date.now() - 2 * 86_400_000).toISOString()

function pitr(extra: Partial<OpsPitr> = {}): OpsPitr {
  return {
    state: 'on', enabled: true, walFiles: 42, walSize: '180M', baseSize: '96M',
    bases: [{ name: '20261017T030012Z', finishedAt: twoDaysAgo }], oldestRecoverable: twoDaysAgo,
    lastArchivedWal: '00000001000000000000002A', lastArchivedAt: new Date().toISOString(), failedCount: 0, lastFailedWal: null, preRestore: [],
    ...extra,
  }
}

function setup(p: OpsPitr) {
  const snapshot: OpsSnapshot = {
    version: '1.2.0', commit: 'abc1234', commitDate: '2026-10-19', tags: [], backups: [], backupStatus: '', updateStatus: '',
    diskFree: 10 * 1024 ** 3, diskTotal: 50 * 1024 ** 3, host: 'hr360', runnerAt: new Date().toISOString(),
    security: { dbIsolationOff: false, dbRoles: 15, rlsTables: '10/10', tls: true, backupEncrypted: true, backupScheduled: true, backupOffsite: false,
      keyRotation: true, redisPassword: true, monitoring: true, publicSignup: false },
    pitr: p,
  }
  const api = mockApi({
    [`GET ${B}/status`]: { runnerOk: true, updatedAt: new Date().toISOString(), snapshot, activeJobs: 0 },
    [`GET ${B}/jobs`]: [],
    [`POST ${B}/jobs`]: { id: 'j1', status: 'Pending' },
  })
  return { ...renderWithProviders(<MaintenancePage />, { roles: ['platform-admin'] }), api }
}

describe('MaintenancePage › Anlık geri dönüş', () => {
  beforeEach(() => vi.clearAllMocks())

  it('durumu, arşiv boyutunu ve arşiv hatasını gösterir; güvenlik kartında da yer alır', async () => {
    setup(pitr({ failedCount: 3, lastFailedWal: '00000001000000000000002B' }))
    expect((await screen.findAllByText('Anlık geri dönüş')).length).toBe(2)  // kart başlığı + güvenlik kartı
    expect(screen.getByText('Açık')).toBeTruthy()
    expect(screen.getByText(/180M/)).toBeTruthy()
    expect(screen.getByText(/3 başarısız deneme/)).toBeTruthy()
    expect(screen.getByText(/Açık: veritabanı son 5 dakikaya kadar/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Eski veriyi sil' })).toBeNull()
  })

  it('bir ana dönüş yazılı onay ister ve İstanbul saatiyle gönderilir', async () => {
    const { user, api } = setup(pitr())
    await user.click(await screen.findByRole('button', { name: 'Bir ana dön' }))
    const dialog = await screen.findByRole('dialog')
    const go = within(dialog).getByRole('button', { name: 'Bir ana dön' })
    expect((go as HTMLButtonElement).disabled).toBe(true)

    const date = within(dialog).getByLabelText('Tarih') as HTMLInputElement
    const time = within(dialog).getByLabelText('Saat (İstanbul)') as HTMLInputElement
    fireEvent.change(time, { target: { value: '00:00' } })
    fireEvent.change(date, { target: { value: '2000-01-01' } })  // aralık dışı
    await user.type(within(dialog).getByLabelText(/Onay için/), 'GERİ DÖN')
    expect((go as HTMLButtonElement).disabled).toBe(true)
    expect(within(dialog).getByText('Seçilen an geri dönülebilecek aralığın dışında.')).toBeTruthy()

    const yesterday = new Date(Date.now() - 86_400_000)
    const ymd = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Istanbul', year: 'numeric', month: '2-digit', day: '2-digit' }).format(yesterday)
    fireEvent.change(date, { target: { value: ymd } })
    fireEvent.change(time, { target: { value: '12:30' } })
    expect((go as HTMLButtonElement).disabled).toBe(false)
    await user.click(go)
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)).toHaveLength(1))
    expect(api.callsTo('POST', `${B}/jobs`)[0].body).toEqual({ kind: 'pitr-restore', args: { target: `${ymd} 12:30` }, confirm: 'GERİ DÖN' })
  })

  it('kapalıyken onayla açılır', async () => {
    const { user, api } = setup(pitr({ state: 'off', enabled: false }))
    await user.click(await screen.findByRole('button', { name: 'Aç' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Aç' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'pitr-enable', args: {}, confirm: undefined }))
  })

  it('kapatırken arşivi de silmek seçilebilir', async () => {
    const { user, api } = setup(pitr())
    await user.click(await screen.findByRole('button', { name: 'Kapat' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('checkbox'))
    await user.click(within(dialog).getByRole('button', { name: 'Anlık geri dönüşü kapat' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'pitr-disable', args: { purge: true }, confirm: undefined }))
  })

  it('geri dönüş öncesi veri varsa onayla silinir', async () => {
    const { user, api } = setup(pitr({ preRestore: [{ name: 'pre-restore-20261019T101500Z', size: '1.2G' }] }))
    await user.click(await screen.findByRole('button', { name: 'Eski veriyi sil' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Sil' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'pitr-cleanup', args: {}, confirm: undefined }))
  })
})
