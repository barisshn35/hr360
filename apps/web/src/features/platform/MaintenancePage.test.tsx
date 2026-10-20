// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { OpsModules, OpsMonitoring, OpsPitr, OpsSnapshot } from '@/api/platformOps'
import { publicSiteKey } from '@/features/site/usePublicSite'
import { mockApi, renderWithProviders } from '@/test/render'
import { MaintenancePage, validAlertEmails, validWebhook } from './MaintenancePage'

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

const ALL_MODULES = ['recruitment', 'onboarding', 'timeshift', 'performance', 'learning', 'compensation', 'ml', 'expense']

function setupExtra(extra: Partial<OpsSnapshot>, jobs: unknown = []) {
  const snapshot: OpsSnapshot = {
    version: '1.2.0', commit: 'abc1234', commitDate: '2026-10-19', tags: [], backups: [], backupStatus: '', updateStatus: '',
    diskFree: 10 * 1024 ** 3, diskTotal: 50 * 1024 ** 3, host: 'hr360', runnerAt: new Date().toISOString(), ...extra,
  }
  const api = mockApi({
    [`GET ${B}/status`]: { runnerOk: true, updatedAt: new Date().toISOString(), snapshot, activeJobs: 0 },
    [`GET ${B}/jobs`]: jobs as never,
    [`POST ${B}/jobs`]: { id: 'j1', status: 'Pending' },
  })
  return { ...renderWithProviders(<MaintenancePage />, { roles: ['platform-admin'] }), api }
}

function modules(enabled: string[]): OpsModules {
  return {
    enabled, all: ALL_MODULES,
    details: ALL_MODULES.map((id) => ({ id, name: id, memoryMb: id === 'ml' ? 1792 : id === 'expense' ? 0 : 512, services: [] })),
  }
}

describe('MaintenancePage › Modüller', () => {
  beforeEach(() => vi.clearAllMocks())

  it('eski çalıştırıcıda (özette modül yok) kart gizlenir', async () => {
    setup(pitr())
    await screen.findAllByText('Anlık geri dönüş')
    expect(screen.queryByText('Modüller')).toBeNull()
    expect(screen.queryByText('Şirket kipi')).toBeNull()
  })

  it('modülleri durum, bellek ve açıklamayla listeler; çekirdek her zaman açık', async () => {
    setupExtra({ modules: modules(ALL_MODULES.filter((m) => m !== 'ml')) })
    expect(await screen.findByText('Modüller')).toBeTruthy()
    expect(screen.getByText(/Çekirdek: çalışanlar, organizasyon/)).toBeTruthy()
    for (const name of ['İşe alım', 'İşe alıştırma ve demirbaş', 'Puantaj ve vardiya', 'Performans', 'Eğitim', 'Ücret ve bordro', 'Yapay zekâ', 'Masraf ve seyahat'])
      expect(screen.getByRole('checkbox', { name })).toBeTruthy()
    expect(screen.getByRole('checkbox', { name: 'Yapay zekâ' }).getAttribute('data-state')).toBe('unchecked')
    expect(screen.getByText('7/8 açık')).toBeTruthy()
    expect(screen.getAllByText('Açık')).toHaveLength(7)
    expect(screen.getAllByText('Kapalı')).toHaveLength(1)
    expect(screen.getByText('Bellek: yaklaşık 1792 MB')).toBeTruthy()
    expect(screen.getByText('Ek bellek gerekmez')).toBeTruthy()
    expect(screen.getByText(/Ayrılma riski tahmini/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Uygula' }) as HTMLButtonElement).disabled).toBe(true)  // değişiklik yok
  })

  it('yalnızca açma: özet gösterilir, yazılı onay istenmez', async () => {
    const { user, api } = setupExtra({ modules: modules(['timeshift', 'compensation', 'expense']) })
    await user.click(await screen.findByRole('checkbox', { name: 'Yapay zekâ' }))
    await user.click(screen.getByRole('button', { name: 'Uygula' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('Açılacak: Yapay zekâ')).toBeTruthy()
    expect(within(dialog).getByText(/modül başına birkaç dakika/)).toBeTruthy()
    expect(within(dialog).queryByLabelText(/Onay için/)).toBeNull()
    await user.click(within(dialog).getByRole('button', { name: 'Modül seçimini uygula' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual(
      { kind: 'modules-set', args: { modules: ['timeshift', 'compensation', 'ml', 'expense'] }, confirm: undefined }))
  })

  it('kapatma "MODÜLÜ KAPAT" yazılı onayı ister; veri korunur bilgisi gösterilir', async () => {
    const { user, api } = setupExtra({ modules: modules(ALL_MODULES) })
    await user.click(await screen.findByRole('checkbox', { name: 'Puantaj ve vardiya' }))
    await user.click(screen.getByRole('checkbox', { name: 'İşe alım' }))
    await user.click(screen.getByRole('button', { name: 'Uygula' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('Kapatılacak: İşe alım, Puantaj ve vardiya')).toBeTruthy()
    expect(within(dialog).getByText(/Veriler silinmez/)).toBeTruthy()
    const go = within(dialog).getByRole('button', { name: 'Modül seçimini uygula' }) as HTMLButtonElement
    expect(go.disabled).toBe(true)
    await user.type(within(dialog).getByLabelText(/Onay için/), 'MODÜLÜ KAPAT')
    expect(go.disabled).toBe(false)
    await user.click(go)
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({
      kind: 'modules-set', args: { modules: ['onboarding', 'performance', 'learning', 'compensation', 'ml', 'expense'] }, confirm: 'MODÜLÜ KAPAT',
    }))
  })

  it('seçim geri alınabilir', async () => {
    const { user } = setupExtra({ modules: modules(ALL_MODULES) })
    await user.click(await screen.findByRole('checkbox', { name: 'Eğitim' }))
    await user.click(screen.getByRole('button', { name: 'Seçimi geri al' }))
    expect((screen.getByRole('checkbox', { name: 'Eğitim' }) as HTMLButtonElement).getAttribute('data-state')).toBe('checked')
    expect((screen.getByRole('button', { name: 'Uygula' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('modül işi başarıyla bitince herkese açık site bilgisi (menüler) tazelenir', async () => {
    const job = (status: string) => ({ id: 'm1', kind: 'modules-set', args: { modules: ['ml'] }, status, requestedBy: 'p', requestedAt: new Date().toISOString(),
      startedAt: null, finishedAt: null, exitCode: null, output: null })
    let status = 'Running'
    const { queryClient } = setupExtra({ modules: modules(['ml']) }, () => [job(status)])
    const spy = vi.spyOn(queryClient, 'invalidateQueries')
    expect(await screen.findByText(/Son işlem: Modül seçimini uygula/)).toBeTruthy()
    expect(spy.mock.calls.some((c) => JSON.stringify(c[0]?.queryKey) === JSON.stringify(publicSiteKey))).toBe(false)
    status = 'Succeeded'
    await queryClient.refetchQueries({ queryKey: ['platform-ops', 'jobs'] })
    await waitFor(() => expect(spy.mock.calls.some((c) => JSON.stringify(c[0]?.queryKey) === JSON.stringify(publicSiteKey))).toBe(true))
  })
})

describe('MaintenancePage › Şirket kipi', () => {
  beforeEach(() => vi.clearAllMocks())

  it('çok şirketli ve tek şirket varken tek şirket kipine onayla geçilir', async () => {
    const { user, api } = setupExtra({ tenancy: { mode: 'multi', tenants: 1 } })
    expect(await screen.findByText('Şirket sayısı: 1')).toBeTruthy()
    expect(screen.getByText('Çok şirketli')).toBeTruthy()
    await user.click(screen.getByRole('button', { name: 'Tek şirket kipine geç' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Tek şirket kipine geç' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'tenancy-single', args: {}, confirm: undefined }))
  })

  it('birden çok şirket varken tek şirket kipi önerilmez, nedeni açıklanır', async () => {
    setupExtra({ tenancy: { mode: 'multi', tenants: 3 } })
    expect(await screen.findByText(/Bu kurulumda 3 şirket var/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Tek şirket kipine geç' })).toBeNull()
  })

  it('tek şirket kipinden çok şirketli kipe "ÇOK ŞİRKET" yazılı onayla geçilir', async () => {
    const { user, api } = setupExtra({ tenancy: { mode: 'single', tenants: 1 } })
    expect(await screen.findByText('Tek şirket')).toBeTruthy()
    await user.click(screen.getByRole('button', { name: 'Çok şirketli kipe geç' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/platform yönetimi ile şirket yönetimi yeniden ayrılır/)).toBeTruthy()
    const go = within(dialog).getByRole('button', { name: 'Çok şirketli kipe geç' }) as HTMLButtonElement
    await user.type(within(dialog).getByLabelText(/Onay için/), 'ÇOK ŞİRKE')
    expect(go.disabled).toBe(true)
    await user.type(within(dialog).getByLabelText(/Onay için/), 'T')
    await user.click(go)
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'tenancy-multi', args: {}, confirm: 'ÇOK ŞİRKET' }))
  })
})

function mon(extra: Partial<OpsMonitoring> = {}): OpsMonitoring {
  return { enabled: true, running: 10, total: 10, alerts: { email: [], slack: null, teams: null }, tracing: { on: false, ratio: null }, sso: true, ...extra }
}
const GB = 1024 ** 3

describe('MaintenancePage › İzleme ve alarmlar', () => {
  beforeEach(() => vi.clearAllMocks())

  it('kapalıyken bellek bilgisiyle gösterilir ve onayla açılır', async () => {
    const { user, api } = setupExtra({ monitoring: mon({ enabled: false, running: null }), memory: { total: 8 * GB, available: 1 * GB } })
    expect(await screen.findByText(/İzleme kapalı: bir servis çöktüğünde/)).toBeTruthy()
    expect(screen.getByText(/Bellek az/)).toBeTruthy()
    expect(screen.queryByRole('link', { name: "Grafana'yı aç" })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Deneme alarmı gönder' })).toBeNull()
    await user.click(screen.getByRole('button', { name: 'İzlemeyi aç' }))
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Aç' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'monitoring-enable', args: {}, confirm: undefined }))
  })

  it('açıkken bileşen sayısı, Grafana bağlantısı ve maskeli webhook gösterilir', async () => {
    setupExtra({ monitoring: mon({ running: 8, alerts: { email: ['ops@a.com'], slack: 'https://hooks.slack.com/…', teams: null } }) })
    expect(await screen.findByText(/8\/10 bileşen çalışıyor/)).toBeTruthy()
    expect(screen.getByText(/Bazı izleme bileşenleri çalışmıyor/)).toBeTruthy()
    expect(screen.getByText(/HR360 hesabınızla girersiniz/)).toBeTruthy()
    expect(screen.getByRole('link', { name: "Grafana'yı aç" }).getAttribute('href')).toBe('/grafana/')
    expect(screen.getByText('https://hooks.slack.com/…')).toBeTruthy()
    expect(screen.getByText('ops@a.com')).toBeTruthy()
  })

  it('e-posta kanalı doğrulanır ve kaydedilir; Slack kapatılabilir', async () => {
    const { user, api } = setupExtra({ monitoring: mon({ alerts: { email: [], slack: 'https://hooks.slack.com/…', teams: null } }) })
    await user.click(await screen.findByRole('button', { name: 'E-posta kanalını ayarla' }))
    const field = screen.getByLabelText('E-posta')
    await user.type(field, 'ops@a.com; it')
    expect(screen.getByText(/En fazla 10 geçerli e-posta adresi/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Kaydet' }) as HTMLButtonElement).disabled).toBe(true)
    await user.clear(field)
    await user.type(field, 'ops@a.com, it@b.com.tr')
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'monitoring-alerts', args: { channel: 'email', value: 'ops@a.com, it@b.com.tr' }, confirm: undefined }))
    await user.click(screen.getByRole('button', { name: 'Slack kanalını kapat' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[1]?.body).toEqual({ kind: 'monitoring-alerts', args: { channel: 'slack', value: null }, confirm: undefined }))
  })

  it('kanal yokken deneme alarmı kapalı; varken gönderilir', async () => {
    const { user, api } = setupExtra({ monitoring: mon({ alerts: { email: ['ops@a.com'], slack: null, teams: null } }) })
    await user.click(await screen.findByRole('button', { name: 'Deneme alarmı gönder' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'monitoring-test', args: {}, confirm: undefined }))
  })

  it('dağıtık izleme oranla açılır; izleme kapatılırken veriler de silinebilir', async () => {
    const { user, api } = setupExtra({ monitoring: mon() })
    expect((await screen.findByRole('button', { name: 'Deneme alarmı gönder' }) as HTMLButtonElement).disabled).toBe(true)
    const ratio = screen.getByLabelText('Örnekleme oranı (0–1)')
    await user.clear(ratio)
    await user.type(ratio, '0,25')
    await user.click(screen.getByRole('button', { name: 'Aç' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[0]?.body).toEqual({ kind: 'monitoring-tracing', args: { on: true, ratio: 0.25 }, confirm: undefined }))
    await user.click(screen.getByRole('button', { name: 'İzlemeyi kapat' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('checkbox'))
    await user.click(within(dialog).getByRole('button', { name: 'İzlemeyi kapat' }))
    await waitFor(() => expect(api.callsTo('POST', `${B}/jobs`)[1]?.body).toEqual({ kind: 'monitoring-disable', args: { purge: true }, confirm: undefined }))
  })

  it('doğrulayıcılar', () => {
    expect(validAlertEmails('a@b.co')).toBe(true)
    expect(validAlertEmails('a@b.co, c+x@d.com.tr')).toBe(true)
    expect(validAlertEmails('')).toBe(false)
    expect(validAlertEmails('a@b')).toBe(false)
    expect(validAlertEmails(Array(11).fill('a@b.co').join(','))).toBe(false)
    expect(validWebhook('https://hooks.slack.com/services/T/B/X')).toBe(true)
    expect(validWebhook('http://hooks.slack.com/services/T/B/X')).toBe(false)
    expect(validWebhook('https://a.com/x y')).toBe(false)
    expect(validWebhook('https://a.com/#frag')).toBe(false)
  })
})
