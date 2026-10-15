import { apiFetch, qs } from './client'
import { tx } from '@/lib/i18n'

const BASE = '/api/timeshift'

/* ------------------------------------------------------------------ tipler */

export type TimeEntrySource = 'Manual' | 'Device' | 'Import'

export const timeEntrySourceLabels: Record<TimeEntrySource, string> = {
  Manual: tx('Elle'),
  Device: tx('Cihaz'),
  Import: tx('İçe aktarım'),
}

export interface Shift {
  id: string
  name: string
  startTime: string
  endTime: string
  breakMinutes: number
  departmentId: string | null
}

export interface RosterEntry {
  id: string
  shiftId: string
  shiftName: string
  employeeId: string
  date: string
  startTime: string
  endTime: string
}

export interface TimeEntry {
  id: string
  employeeId: string
  date: string
  clockIn: string | null
  clockOut: string | null
  workedMinutes: number
  /** 480 dakikayı (8 saat) aşan kısım. */
  overtimeMinutes: number
  source: TimeEntrySource
}

export interface TimeSummary {
  employeeId: string
  year: number
  month: number
  totalWorkedMinutes: number
  totalOvertimeMinutes: number
  daysWorked: number
}

export interface CreateShiftInput {
  name: string
  startTime: string
  endTime: string
  breakMinutes: number
  departmentId?: string
}

export interface ClockInput {
  employeeId: string
  at: string
  source: TimeEntrySource
}

/* ------------------------------------------------------------ vardiya motoru */

/** Desenin bir günü: Gündüz, Gece ya da Tatil (çalışılmayan gün). */
export type ShiftDayType = 'Day' | 'Night' | 'Off'

/**
 * Takvimde desenin önüne geçen istisnalar (ShiftOverride). İzin onaylanınca
 * backend `Leave` override'ını kendisi oluşturur; arayüz yalnızca gösterir.
 */
export type ShiftOverrideType = 'Leave' | 'Holiday' | 'Manual'

export type RosterDayType = ShiftDayType | ShiftOverrideType

export interface ShiftPatternDay {
  dayIndex: number
  type: ShiftDayType
  /** "21:00:00" — `Off` günlerinde null. */
  startTime: string | null
  endTime: string | null
}

export interface ShiftPattern {
  id: string
  name: string
  isActive: boolean
  days: ShiftPatternDay[]
}

export interface ShiftPatternDayInput {
  type: ShiftDayType
  /** "21:00" — `Off` günlerinde null. */
  startTime: string | null
  endTime: string | null
}

/** `days` dizisinin sırası `dayIndex`'i belirler; index'i backend atar. */
export interface CreateShiftPatternInput {
  name: string
  days: ShiftPatternDayInput[]
}

export interface ShiftTeamMember {
  id: string
  employeeId: string
  /** Küçük sayı = daha kıdemli; 1 en üstte gösterilir. */
  rank: number
  tag: string | null
  effectiveFrom: string
  effectiveTo: string | null
}

export interface ShiftTeam {
  id: string
  name: string
  shiftPatternId: string
  shiftPattern: ShiftPattern | null
  /** Ekibin desenin 1. gününde (dayIndex 0) olduğu tarih. */
  anchorDate: string
  departmentId: string | null
  /** Yalnızca etkin (effectiveTo = null) üyeler. */
  members: ShiftTeamMember[]
}

export interface CreateShiftTeamInput {
  name: string
  shiftPatternId: string
  anchorDate: string
  departmentId: string | null
}

export interface AddShiftTeamMemberInput {
  employeeId: string
  rank: number
  tag: string | null
  effectiveFrom: string
}

/** Hesaplanmış takvim günü — tipine göre üç ayrı şema taşır. */
/** Hesaplanmış takvim günü — tipine göre şema değişir. `type: null`,
 * üyeliğin henüz başlamadığı/bittiği gün için boş hücre demektir. */
export type RosterDay =
  | { date: string; type: 'Day' | 'Night'; startTime: string; endTime: string; note: null; overrideId: null }
  | { date: string; type: 'Off'; startTime: null; endTime: null; note: null; overrideId: null }
  | {
      date: string
      type: ShiftOverrideType
      startTime: string | null
      endTime: string | null
      note: string | null
      overrideId: string
      /** Kısmi gün izni (yarım gün / saatlik): çalışan o gün kısmen çalışır (dalga 9). */
      partial?: boolean
    }
  | { date: string; type: null; startTime: null; endTime: null; note: null; overrideId: null }

export interface TeamRosterMember {
  employeeId: string
  rank: number
  tag: string | null
  effectiveFrom: string
  effectiveTo: string | null
  schedule: RosterDay[]
}

/** Üyeler rank'e göre sıralı gelir (en kıdemliden en yeniye). */
export interface TeamRoster {
  team: string
  patternName: string
  members: TeamRosterMember[]
}

export interface UpdateShiftPatternInput {
  name?: string
  isActive?: boolean
  /** Gönderilirse VE deseni kullanan ekip varsa 409 döner. */
  days?: ShiftPatternDayInput[]
}

export interface UpdateShiftTeamInput {
  name?: string
  anchorDate?: string
  /** Kısıtı kaldırmak için `null`, dokunmamak için alanı hiç göndermeyin. */
  departmentId?: string | null
  /** Sunucu departman alanını yalnızca bu bayrak true ise değiştirir. */
  departmentIdSet?: boolean
  /** Ekibi başka (etkin) desene taşır. */
  shiftPatternId?: string
}

export interface UpdateShiftTeamMemberInput {
  rank?: number
  /** Etiketi temizlemek için `null`, dokunmamak için alanı hiç göndermeyin. */
  tag?: string | null
}

export interface ShiftOverride {
  id: string
  employeeId: string
  date: string
  type: ShiftOverrideType
  note: string | null
  startTime: string | null
  endTime: string | null
  isSystemManaged: boolean
}

export interface CreateShiftOverrideInput {
  /** Üçünden tam olarak biri anlamlı: employeeId (bir kişi), teamId (bir
   * ekibin tüm aktif üyeleri) — ikisi de boşsa tüm çalışanlar hedeflenir. */
  employeeId?: string
  teamId?: string
  from: string
  to: string
  type: 'Holiday' | 'Manual'
  note?: string
  /** `Manual` için zorunlu. */
  startTime?: string
  endTime?: string
}

/* ------------------------------------------------------------------ servis */

export const timeshiftApi = {
  listShifts: (departmentId?: string, signal?: AbortSignal) =>
    apiFetch<Shift[]>(`${BASE}/shifts${qs({ departmentId })}`, { signal }),

  createShift: (input: CreateShiftInput) =>
    apiFetch<Shift>(`${BASE}/shifts`, { method: 'POST', body: input }),

  /** Atama kaydı bağlı vardiya silinemez (409). */
  deleteShift: (id: string) => apiFetch<void>(`${BASE}/shifts/${id}`, { method: 'DELETE' }),

  assignShift: (shiftId: string, employeeId: string, date: string) =>
    apiFetch<RosterEntry>(`${BASE}/shifts/${shiftId}/assign`, {
      method: 'POST',
      body: { employeeId, date },
    }),

  roster: (
    filters: { from?: string; to?: string; employeeId?: string } = {},
    signal?: AbortSignal,
  ) => apiFetch<RosterEntry[]>(`${BASE}/shifts/roster${qs(filters)}`, { signal }),

  listEntries: (
    filters: { employeeId?: string; from?: string; to?: string } = {},
    signal?: AbortSignal,
  ) => apiFetch<TimeEntry[]>(`${BASE}/time-entries${qs(filters)}`, { signal }),

  clockIn: (input: ClockInput) =>
    apiFetch<TimeEntry>(`${BASE}/time-entries/clock-in`, { method: 'POST', body: input }),

  clockOut: (input: ClockInput) =>
    apiFetch<TimeEntry>(`${BASE}/time-entries/clock-out`, { method: 'POST', body: input }),

  summary: (employeeId: string, year: number, month: number, signal?: AbortSignal) =>
    apiFetch<TimeSummary>(`${BASE}/time-entries/summary${qs({ employeeId, year, month })}`, {
      signal,
    }),

  /* --------------------------------------------------------- vardiya motoru */

  listPatterns: (signal?: AbortSignal) =>
    apiFetch<ShiftPattern[]>(`${BASE}/shift-patterns`, { signal }),

  createPattern: (input: CreateShiftPatternInput) =>
    apiFetch<ShiftPattern>(`${BASE}/shift-patterns`, { method: 'POST', body: input }),

  /** Deseni kullanan ekip varsa 409 döner. */
  deletePattern: (id: string) =>
    apiFetch<void>(`${BASE}/shift-patterns/${id}`, { method: 'DELETE' }),

  /** `days` gönderilirse VE deseni kullanan ekip varsa 409 döner. */
  updatePattern: (id: string, input: UpdateShiftPatternInput) =>
    apiFetch<ShiftPattern>(`${BASE}/shift-patterns/${id}`, { method: 'PUT', body: input }),

  listTeams: (departmentId?: string, signal?: AbortSignal) =>
    apiFetch<ShiftTeam[]>(`${BASE}/shift-teams${qs({ departmentId })}`, { signal }),

  createTeam: (input: CreateShiftTeamInput) =>
    apiFetch<ShiftTeam>(`${BASE}/shift-teams`, { method: 'POST', body: input }),

  updateTeam: (id: string, input: UpdateShiftTeamInput) =>
    apiFetch<ShiftTeam>(`${BASE}/shift-teams/${id}`, { method: 'PUT', body: input }),

  /** Ekipte aktif üye varsa 409 döner. */
  deleteTeam: (id: string) =>
    apiFetch<void>(`${BASE}/shift-teams/${id}`, { method: 'DELETE' }),

  /** Çalışan başka bir ekipte etkinse o üyelik backend'de kendiliğinden kapanır.
   * Ekip bir departmana bağlıysa, çalışanın o departmanda (veya alt
   * departmanlarında) etkin ataması yoksa 400 döner. */
  addTeamMember: (teamId: string, input: AddShiftTeamMemberInput) =>
    apiFetch<ShiftTeamMember>(`${BASE}/shift-teams/${teamId}/members`, {
      method: 'POST',
      body: input,
    }),

  updateTeamMember: (teamId: string, memberId: string, input: UpdateShiftTeamMemberInput) =>
    apiFetch<ShiftTeamMember>(`${BASE}/shift-teams/${teamId}/members/${memberId}`, {
      method: 'PATCH',
      body: input,
    }),

  /** Üyeliği sonlandırır. `effectiveTo` verilmezse bugün kullanılır. */
  removeTeamMember: (teamId: string, memberId: string, effectiveTo?: string) =>
    apiFetch<void>(`${BASE}/shift-teams/${teamId}/members/${memberId}${qs({ effectiveTo })}`, {
      method: 'DELETE',
    }),

  /** En fazla 93 günlük aralık — daha uzunu 400 döner. */
  teamRoster: (teamId: string, from: string, to: string, signal?: AbortSignal) =>
    apiFetch<TeamRoster>(`${BASE}/shift-teams/${teamId}/roster${qs({ from, to })}`, { signal }),

  /** Tek istekte birden çok (ya da tüm) ekibin roster'ı — "Tüm ekipler"
   * görünümü için ekip sayısı kadar istek atmayı önler. */
  bulkRoster: (from: string, to: string, teamIds?: string[], signal?: AbortSignal) =>
    apiFetch<TeamRoster[]>(
      `${BASE}/shift-teams/roster${qs({ from, to, teamIds: teamIds?.join(',') })}`,
      { signal },
    ),

  /** Yıllık özet: üye başına tip bazında gün sayısı + toplam vardiya dakikası. */
  teamSummary: (teamId: string, year: number, signal?: AbortSignal) =>
    apiFetch<{
      team: string
      patternName: string
      year: number
      members: Array<{
        employeeId: string
        rank: number
        tag: string | null
        dayCounts: Record<string, number>
        totalWorkedMinutes: number
      }>
    }>(`${BASE}/shift-teams/${teamId}/summary${qs({ year })}`, { signal }),

  listOverrides: (from: string, to: string, signal?: AbortSignal) =>
    apiFetch<ShiftOverride[]>(`${BASE}/shift-overrides${qs({ from, to })}`, { signal }),

  /** `Leave` tipi elle oluşturulamaz (izin onayıyla otomatik oluşur, 400 döner). */
  createOverride: (input: CreateShiftOverrideInput) =>
    apiFetch<ShiftOverride[]>(`${BASE}/shift-overrides`, { method: 'POST', body: input }),

  /** Sistem tarafından yönetilen (izin kaynaklı) kayıt silinemez, 409 döner. */
  deleteOverride: (id: string) =>
    apiFetch<void>(`${BASE}/shift-overrides/${id}`, { method: 'DELETE' }),
}
