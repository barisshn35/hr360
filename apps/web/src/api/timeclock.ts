import { apiFetch, qs } from './client'

const BASE = '/api/timeshift'

/* ------------------------------------------------------------------ fazla mesai */

export type OvertimeStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled'

export interface OvertimeRequest {
  id: string
  employeeId: string
  date: string
  hours: number
  reason: string | null
  status: OvertimeStatus
  workflowRequestId: string | null
  createdAt: string
  decidedAt: string | null
}

export interface OvertimeSummary {
  employeeId: string
  year: number
  approvedHours: number
  pendingHours: number
  limitHours: number
  remainingHours: number
}

export const overtimeApi = {
  list: (f: { employeeId?: string; year?: number; status?: OvertimeStatus; mine?: boolean } = {}, signal?: AbortSignal) =>
    apiFetch<OvertimeRequest[]>(`${BASE}/overtime${qs(f)}`, { signal }),
  summary: (f: { employeeId?: string; year?: number } = {}, signal?: AbortSignal) =>
    apiFetch<OvertimeSummary>(`${BASE}/overtime/summary${qs(f)}`, { signal }),
  create: (body: { employeeId?: string; date: string; hours: number; reason?: string }) =>
    apiFetch<OvertimeRequest>(`${BASE}/overtime`, { method: 'POST', body }),
  cancel: (id: string) => apiFetch<OvertimeRequest>(`${BASE}/overtime/${id}/cancel`, { method: 'POST' }),
  decide: (id: string, approve: boolean) => apiFetch<OvertimeRequest>(`${BASE}/overtime/${id}/decide`, { method: 'POST', body: { approve } }),
}

/* ------------------------------------------------------------------ giriş-çıkış */

export interface ClockSite {
  id: string
  name: string
  allowQr: boolean
  allowTerminal?: boolean
  checkLocation: boolean
  latitude?: number | null
  longitude?: number | null
  radiusMeters?: number
  isActive?: boolean
  hasDeviceKey?: boolean
}

export interface ClockPunch {
  id: string
  employeeId: string
  siteId: string | null
  kind: 'In' | 'Out'
  method: 'Manual' | 'Device' | 'Import' | 'Qr' | 'Card' | 'Pin' | 'Web' | 'Chat'
  onSite: boolean | null
  /** Noktaya uzaklık aralığı (m): "0-50" … "1000+". Koordinat hiç dönmez. */
  distanceBucket?: string | null
  at: string
}

/** Kiosk (paylaşılan tablet) durumu: o anki imzalı QR jetonu (30 sn) ve PIN ekranı açık mı. */
export interface KioskState {
  site: string
  allowQr: boolean
  allowPin: boolean
  checkLocation: boolean
  token: string | null
  expiresAt: string
  windowSeconds: number
}

/** Kiosk anahtarı yalnızca bu tablette saklanır (terminal anahtarıyla aynı; İK oturumu açık bırakılmaz). */
export const KIOSK_KEY_STORAGE = 'hr360.kiosk.deviceKey'

export const kioskApi = {
  state: (deviceKey: string, signal?: AbortSignal) =>
    apiFetch<KioskState>(`${BASE}/time-clock/kiosk/state`, { anonymous: true, signal, headers: { 'X-Device-Key': deviceKey } }),
  pinPunch: (deviceKey: string, badgeCode: string, pin: string) =>
    apiFetch<{ kind: 'In' | 'Out'; at: string; firstName: string | null; site: string }>(`${BASE}/time-clock/terminal/punch`, {
      method: 'POST', anonymous: true, noQueue: true, headers: { 'X-Device-Key': deviceKey }, body: { badgeCode, pin, source: 'kiosk' },
    }),
}

export interface ClockMe {
  employeeId: string | null
  linked: boolean
  badgeCode: string | null
  hasPin: boolean
  hasCard: boolean
  clockedIn: boolean
  openSince: string | null
  punches: ClockPunch[]
}

export interface ClockCredential { employeeId: string; badgeCode: string | null; hasCard: boolean; hasPin: boolean; locked: boolean; updatedAt: string }

export type SiteInput = Pick<ClockSite, 'name' | 'allowQr' | 'checkLocation'> & {
  allowTerminal: boolean
  latitude?: number | null
  longitude?: number | null
  radiusMeters?: number
  isActive?: boolean
}

export const timeClockApi = {
  sites: (signal?: AbortSignal) => apiFetch<ClockSite[]>(`${BASE}/time-clock/sites`, { signal }),
  createSite: (body: SiteInput) => apiFetch<ClockSite>(`${BASE}/time-clock/sites`, { method: 'POST', body }),
  updateSite: (id: string, body: SiteInput) => apiFetch<ClockSite>(`${BASE}/time-clock/sites/${id}`, { method: 'PUT', body }),
  deleteSite: (id: string) => apiFetch<void>(`${BASE}/time-clock/sites/${id}`, { method: 'DELETE' }),
  qr: (id: string, signal?: AbortSignal) => apiFetch<{ token: string; expiresAt: string; site: string }>(`${BASE}/time-clock/sites/${id}/qr`, { signal }),
  deviceKey: (id: string) => apiFetch<{ deviceKey: string }>(`${BASE}/time-clock/sites/${id}/device-key`, { method: 'POST' }),
  credentials: (signal?: AbortSignal) => apiFetch<ClockCredential[]>(`${BASE}/time-clock/credentials`, { signal }),
  setCredential: (employeeId: string, body: { badgeCode?: string; cardNumber?: string; clearCard?: boolean }) =>
    apiFetch<ClockCredential>(`${BASE}/time-clock/credentials/${employeeId}`, { method: 'PUT', body }),
  me: (signal?: AbortSignal) => apiFetch<ClockMe>(`${BASE}/time-clock/me`, { signal }),
  setPin: (pin: string) => apiFetch<{ hasPin: boolean }>(`${BASE}/time-clock/me/pin`, { method: 'PUT', body: { pin } }),
  punch: (body: { token?: string; siteId?: string; kind?: 'auto' | 'in' | 'out'; latitude?: number; longitude?: number }) =>
    apiFetch<{ punch: ClockPunch; site: string | null }>(`${BASE}/time-clock/punch`, { method: 'POST', body }),
  punches: (f: { employeeId?: string; from?: string; to?: string } = {}, signal?: AbortSignal) =>
    apiFetch<ClockPunch[]>(`${BASE}/time-clock/punches${qs(f)}`, { signal }),
}

/**
 * Tarayıcıdan tek seferlik konum (yalnızca noktada konum denetimi açıksa, giriş-çıkış anında istenir).
 * Sunucu "noktada mı" ve uzaklık aralığını saklar; ham koordinat yalnızca şirket açtıysa ve süreli tutulur.
 * Tarayıcı izni gateway'in Permissions-Policy başlığında geolocation=(self) ile yalnızca bu siteye açıktır.
 */
export function currentPosition(timeoutMs = 10_000): Promise<{ latitude: number; longitude: number }> {
  return new Promise((resolve, reject) => {
    if (!('geolocation' in navigator)) { reject(new Error('geolocation')); return }
    navigator.geolocation.getCurrentPosition(
      (p) => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude }),
      (e) => reject(e),
      { enableHighAccuracy: true, timeout: timeoutMs, maximumAge: 0 },
    )
  })
}
