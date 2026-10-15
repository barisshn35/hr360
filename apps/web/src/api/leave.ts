import { apiFetch, qs, type Paged } from './client'
import { tx } from '@/lib/i18n'

const BASE = '/api/leave'

/* ------------------------------------------------------------------ tipler */

export type LeaveType =
  | 'Annual'
  | 'Sick'
  | 'Unpaid'
  | 'Maternity'
  | 'Paternity'
  | 'Marriage'
  | 'Bereavement'

export type LeaveStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Cancelled'

export const leaveTypeLabels: Record<LeaveType, string> = {
  Annual: tx('Yıllık izin'),
  Sick: tx('Hastalık izni'),
  Unpaid: tx('Ücretsiz izin'),
  Maternity: tx('Doğum izni'),
  Paternity: tx('Babalık izni'),
  Marriage: tx('Evlilik izni'),
  Bereavement: tx('Vefat izni'),
}

export const leaveStatusLabels: Record<LeaveStatus, string> = {
  Draft: tx('Taslak'),
  Submitted: tx('Onayda'),
  Approved: tx('Onaylandı'),
  Rejected: tx('Reddedildi'),
  Cancelled: tx('İptal edildi'),
}

export interface LeaveBalance {
  id: string
  employeeId: string
  year: number
  type: LeaveType
  entitledDays: number
  usedDays: number
  pendingDays: number
  /** Backend'de hesaplanır, salt okunur. */
  remainingDays: number
}

export interface LeaveRequest {
  id: string
  employeeId: string
  type: LeaveType
  status: LeaveStatus
  startDate: string
  endDate: string
  days: number
  /** Saatlik izin (tek gün) */
  hours?: number | null
  reason: string | null
  workflowRequestId: string | null
  createdAt: string
}

export interface CreateLeaveRequestInput {
  employeeId: string
  type: LeaveType
  startDate: string
  endDate: string
  days: number
  reason?: string
  workflowRequestId?: string
  /** Saatlik izin: yalnızca tek gün; gün = saat / günlük çalışma saati (şirket ayarı, varsayılan 7,5) */
  hours?: number
}

export interface CreateLeaveBalanceInput {
  employeeId: string
  year: number
  type: LeaveType
  entitledDays: number
}

/** Şirketin resmi tatil takvimindeki bir gün (izin günü hesabında düşülür). */
export interface PublicHoliday {
  id: string
  date: string
  name: string
  /** Arife ve 28 Ekim: öğleden sonra tatil, izin hesabında 0,5 gün. */
  isHalfDay?: boolean
}

/** Şirketin izin ayarları (dalga 9). `dayHours`: geçerli günlük çalışma saati (şirket ayarı ya da varsayılan). */
export interface LeaveSettings {
  dayHours: number
  customDayHours: number | null
  conflictWarnEnabled: boolean
  conflictThresholdPercent: number
  carryOverMaxDays: number | null
}

/**
 * Ekip izin çakışması (madde 69). Talep edene yalnızca sayı (ekip 5 kişiden küçükse o da yok);
 * yönetici/İK `people` ile adları görür. İzin türü hiç dönmez.
 */
export interface TeamConflict {
  enabled: boolean
  thresholdPercent: number
  exceeds: boolean
  teamSize: number | null
  overlapping: number | null
  percent: number | null
  people: Array<{ employeeId: string; name: string | null; startDate: string; endDate: string }> | null
}

export interface StatutoryRow {
  employeeId: string
  serviceYears: number
  anniversary: string
  statutoryDays: number
  ageRule: boolean
  currentEntitled: number | null
  carriedOver: number
  /** Hak bu yılın yıl dönümünde doğdu mu (bugün itibarıyla). */
  accrued?: boolean
  /** Uygulanınca bakiye (değişmeyecekse null). */
  proposedEntitled?: number | null
}

export interface LeaveRequestFilters {
  employeeId?: string
  status?: LeaveStatus
}

/** Sunucu tarafı sayfalama parametreleri (G24). `qTypes`: aramayla adı eşleşen izin türleri. */
export interface LeaveRequestPageParams extends LeaveRequestFilters {
  page: number
  pageSize: number
  q?: string
  qTypes?: string
  sort?: 'createdAt' | 'startDate' | 'days' | 'status' | 'type'
  dir?: 'asc' | 'desc'
}

/* ------------------------------------------------------------------ servis */

export const leaveApi = {
  listHolidays: (year?: number, signal?: AbortSignal) =>
    apiFetch<PublicHoliday[]>(`${BASE}/public-holidays${qs({ year })}`, { signal }),

  createHoliday: (input: { date: string; name: string; isHalfDay?: boolean }) =>
    apiFetch<PublicHoliday>(`${BASE}/public-holidays`, { method: 'POST', body: input }),

  /** Türkiye resmi tatillerini ekler (2025-2030 için dini bayramlar ve yarım gün arifeler dahil). */
  seedTurkishHolidays: (year: number) =>
    apiFetch<{ added: number; skipped: number; halfDays?: number; religiousIncluded: boolean; message: string }>(
      `${BASE}/public-holidays/seed-tr${qs({ year })}`,
      { method: 'POST' },
    ),

  deleteHoliday: (id: string) =>
    apiFetch<void>(`${BASE}/public-holidays/${id}`, { method: 'DELETE' }),

  listBalances: (employeeId?: string, year?: number, signal?: AbortSignal) =>
    apiFetch<LeaveBalance[]>(`${BASE}/leave-balances${qs({ employeeId, year })}`, { signal }),

  createBalance: (input: CreateLeaveBalanceInput) =>
    apiFetch<LeaveBalance>(`${BASE}/leave-balances`, { method: 'POST', body: input }),

  listRequests: (filters: LeaveRequestFilters = {}, signal?: AbortSignal) =>
    apiFetch<LeaveRequest[]>(`${BASE}/leave-requests${qs(filters)}`, { signal }),

  /**
   * Verilen gün onaylı izinde olanların talepleri (organizasyon şemasında "bugün izinde" renklendirmesi).
   * Kapsam sunucuda: İK tümünü, yönetici yalnızca ekibini (başı olduğu departmanlar) ve kendisini görür.
   * Arayüz yalnızca kimin izinde olduğunu kullanır; izin türü/gerekçe gösterilmez (sağlık verisi olabilir).
   */
  approvedOnDay: (day: string, signal?: AbortSignal) =>
    apiFetch<LeaveRequest[]>(`${BASE}/leave-requests${qs({ status: 'Approved', from: day, to: day })}`, { signal }),

  pageRequests: (params: LeaveRequestPageParams, signal?: AbortSignal) =>
    apiFetch<Paged<LeaveRequest>>(`${BASE}/leave-requests${qs(params)}`, { signal }),

  getRequest: (id: string, signal?: AbortSignal) =>
    apiFetch<LeaveRequest>(`${BASE}/leave-requests/${id}`, { signal }),

  /** İK: kıdeme ve yaşa göre yasal yıllık izin (İş Kanunu m.53) ön izlemesi. */
  statutory: (year: number, signal?: AbortSignal) =>
    apiFetch<StatutoryRow[]>(`${BASE}/leave-balances/statutory?year=${year}`, { signal }),
  /** Ön izlemede seçilen kişilere uygular; onlyAccrued: yalnızca yıl dönümü gelmiş olanlar. */
  applyStatutory: (year: number, employeeIds?: string[], onlyAccrued?: boolean) =>
    apiFetch<{ changed: number; skippedNotAccrued?: number }>(`${BASE}/leave-balances/statutory/apply`, { method: 'POST', body: { year, employeeIds, onlyAccrued } }),

  settings: (signal?: AbortSignal) => apiFetch<LeaveSettings>(`${BASE}/leave-settings`, { signal }),
  saveSettings: (body: { dayHours: number | null; conflictWarnEnabled: boolean; conflictThresholdPercent: number; carryOverMaxDays: number | null }) =>
    apiFetch<LeaveSettings>(`${BASE}/leave-settings`, { method: 'PUT', body }),
  teamConflict: (f: { employeeId?: string; startDate?: string; endDate?: string; leaveRequestId?: string }, signal?: AbortSignal) =>
    apiFetch<TeamConflict>(`${BASE}/leave-requests/team-conflict${qs(f)}`, { signal }),
  carryOver: (fromYear: number, maxDays?: number) =>
    apiFetch<{ employees: number; days: number }>(`${BASE}/leave-balances/carry-over`, { method: 'POST', body: { fromYear, maxDays } }),

  createRequest: (input: CreateLeaveRequestInput) =>
    apiFetch<LeaveRequest>(`${BASE}/leave-requests`, { method: 'POST', body: input }),

  cancelRequest: (id: string) =>
    apiFetch<LeaveRequest>(`${BASE}/leave-requests/${id}/cancel`, { method: 'POST' }),

  /** Onaylı, başlamamış izni iptal eder (İK ya da çalışanın yöneticisi; gerekçe zorunlu, bakiye geri döner). */
  cancelApproved: (id: string, reason: string) =>
    apiFetch<LeaveRequest>(`${BASE}/leave-requests/${id}/cancel-approved`, { method: 'POST', body: { reason } }),

  /** Çalışan: onaylı izni için iptal isteği (yöneticisine bildirim gider). */
  requestCancellation: (id: string, reason: string) =>
    apiFetch<{ notified: boolean; message: string }>(`${BASE}/leave-requests/${id}/request-cancellation`, { method: 'POST', body: { reason } }),

  /**
   * NOT: Normal akışta çağrılmaz. Workflow onaylanınca Kafka olayı bu işi
   * kendiliğinden yapar; arayüzde "onayla" düğmesi yoktur.
   */
  resolveRequest: (id: string, approved: boolean) =>
    apiFetch<LeaveRequest>(`${BASE}/leave-requests/${id}/resolve`, {
      method: 'POST',
      body: { approved },
    }),
}
