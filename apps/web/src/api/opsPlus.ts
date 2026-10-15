/**
 * Dalga 5c operasyon eklentileri: işe alışma şablonları / buddy / karşılama (G14),
 * zimmet QR, iade ve bakım (G16), offboarding zimmet istisnası ve hesap kapatma (G15),
 * eNPS eğilimi (G18), vardiya tercihleri ve takas (G6), puantaj raporu (G7).
 */
import { apiFetch, qs } from './client'
import type { AssetType, OnboardingPlan, TaskCategory } from './onboarding'
import { tx } from '@/lib/i18n'

const ON = '/api/onboarding'
const EN = '/api/engagement'
const TS = '/api/timeshift'

/* ------------------------------------------------------------------ G14 */

export type OwnerRole = 'HR' | 'Manager' | 'IT' | 'Buddy' | 'Employee'
export const ownerRoleLabels: Record<OwnerRole, string> = {
  HR: tx('İK'), Manager: tx('Yönetici'), IT: tx('BT'), Buddy: tx('Yol arkadaşı'), Employee: tx('Yeni çalışan'),
}

export interface TemplateItem { id?: string; title: string; category: TaskCategory; ownerRole: OwnerRole; offsetDays: number; order?: number }
export interface TaskTemplate {
  id: string
  name: string
  positionTitle: string | null
  departmentId: string | null
  isActive: boolean
  items: TemplateItem[]
}
export interface TemplateInput { name: string; positionTitle: string | null; departmentId: string | null; isActive: boolean; items: TemplateItem[] }
export interface OnboardingSettings {
  welcomeSubject: string | null
  welcomeBody: string | null
  hrContactEmployeeId: string | null
  reminderDaysBefore: number
  defaultSubject: string
  defaultBody: string
  placeholders: string[]
}

/* ------------------------------------------------------------------ G16 */

export interface ScanResult {
  asset: { id: string; assetTag: string; type: AssetType; model: string | null; serialNumber: string | null; status: string; qrCode: string }
  holder: { employeeId: string; name: string | null; department: string | null; assignedOn: string; expectedReturnOn: string | null; overdue: boolean } | null
  lastMaintenance: { date: string; type: MaintenanceType; nextMaintenanceOn: string | null } | null
  canManage: boolean
}
export type MaintenanceType = 'Periodic' | 'Repair' | 'Inspection' | 'Other'
export const maintenanceTypeLabels: Record<MaintenanceType, string> = {
  Periodic: tx('Periyodik bakım'), Repair: tx('Onarım'), Inspection: tx('Kontrol'), Other: tx('Diğer'),
}
export interface MaintenanceRecord {
  id: string
  assetId: string
  date: string
  type: MaintenanceType
  cost: number | null
  vendor: string | null
  notes: string | null
  nextMaintenanceOn: string | null
  createdBy: string | null
}
export interface MaintenanceDue { assetId: string; assetTag: string; type: AssetType; model: string | null; lastDate: string; lastType: MaintenanceType; vendor: string | null; nextMaintenanceOn: string; overdue: boolean }
export interface ReturnDue {
  assignmentId: string
  assetId: string
  assetTag: string
  type: AssetType
  model: string | null
  employeeId: string
  holder: string | null
  assignedOn: string
  expectedReturnOn: string
  overdue: boolean
}

/* ------------------------------------------------------------------ G15 */

export type AssetResolution = 'Open' | 'Returned' | 'Lost' | 'WrittenOff'
export const assetResolutionLabels: Record<AssetResolution, string> = {
  Open: tx('İade bekliyor'), Returned: tx('İade alındı'), Lost: tx('Kayıp'), WrittenOff: tx('Kayıttan düşüldü'),
}
export interface AssetCheck {
  assignmentId: string
  assetId: string
  assetTag: string
  label: string
  resolution: AssetResolution
  returnedOn: string | null
  note: string | null
  resolvedBy: string | null
  resolvedAt: string | null
}
export type AccountStatus = 'Disabled' | 'NoAccount' | 'Failed' | 'Skipped'

/* ------------------------------------------------------------------ G18 */

export interface EnpsTrend {
  minResponses: number
  excluded: number
  points: Array<{ surveyId: string; title: string; date: string; responses: number; enps: number; promotersPct: number; detractorsPct: number }>
}

/* ------------------------------------------------------------------ G6 */

export interface ShiftPreference {
  employeeId: string
  preferredDays: number[]
  unavailableDays: number[]
  preferredShiftTypes: Array<'Day' | 'Night'>
  avoidShiftTypes: Array<'Day' | 'Night'>
  maxNightsPerWeek: number | null
  note: string | null
  updatedAt: string | null
}
/** Çalışma süresi kural uyarısı (madde 66): overlap, rest, weekly, daily, night, consecutive (takasta ayrıca sameday). */
export interface RuleWarning { code: string; message: string; blocking?: boolean }
export interface PreferenceCheck { conflicts: Array<{ code: string; message: string }>; ruleWarnings?: RuleWarning[]; shiftType: 'Day' | 'Night'; preference: ShiftPreference }
export type SwapStatus = 'PendingPeer' | 'PendingApproval' | 'Approved' | 'Rejected' | 'Declined' | 'Cancelled'
export const swapStatusView: Record<SwapStatus, { label: string; tone: 'neutral' | 'warning' | 'success' | 'danger' | 'info' }> = {
  PendingPeer: { label: tx('Karşı taraf bekleniyor'), tone: 'warning' },
  PendingApproval: { label: tx('Yönetici onayı bekleniyor'), tone: 'info' },
  Approved: { label: tx('Onaylandı'), tone: 'success' },
  Rejected: { label: tx('Reddedildi'), tone: 'danger' },
  Declined: { label: tx('Kabul edilmedi'), tone: 'danger' },
  Cancelled: { label: tx('İptal'), tone: 'neutral' },
}
export interface SwapShift { assignmentId: string; date: string; shiftId: string; name: string; startTime: string; endTime: string }
export interface SwapRequest {
  id: string
  status: SwapStatus
  note: string | null
  rejectReason: string | null
  createdAt: string
  decidedAt: string | null
  decidedBy: string | null
  requesterEmployeeId: string
  requesterName: string | null
  targetEmployeeId: string
  targetName: string | null
  requesterShift: SwapShift | null
  targetShift: SwapShift | null
  giveAway: boolean
  canRespond: boolean
  canApprove: boolean
  canCancel: boolean
  /** Onaycıya kural uyarıları (blocking = onayda takas reddedilir). */
  ruleWarnings?: RuleWarning[] | null
}

/* ------------------------------------------------------------------ G7 */

export interface AttendanceRow {
  employeeId: string
  name: string
  date: string
  source: 'Shift' | 'Override' | 'Default' | 'Weekend' | 'Leave' | 'Holiday'
  status: 'Ok' | 'Late' | 'EarlyLeave' | 'Absent' | 'MissingOut' | 'Off' | 'OffDayWork' | 'Leave' | 'Holiday' | 'NotYet'
  plannedStart?: string | null
  plannedEnd?: string | null
  firstIn?: string | null
  lastOut?: string | null
  lateMinutes?: number
  earlyLeaveMinutes?: number
  workedMinutes?: number
  expectedMinutes?: number
  overtimeMinutes?: number
  suggestedOvertimeHours?: number
  overtimeRequest?: { id: string; status: string; hours: number } | null
}
export interface AttendanceTotals {
  employeeId: string
  name: string
  department: string | null
  lateMinutes: number
  lateDays: number
  earlyLeaveMinutes: number
  workedMinutes: number
  overtimeMinutes: number
  absentDays: number
}
export interface AttendanceReport {
  from: string
  to: string
  scope: 'all' | 'department' | 'self'
  graceMinutes: number
  defaultStart: string
  defaultEnd: string
  note: string
  totals: AttendanceTotals[]
  rows: AttendanceRow[]
}
/** /shifts/roster ham yanıtı (iç içe vardiya). */
export interface RosterAssignment {
  id: string
  employeeId: string
  shiftId: string
  date: string
  shift: { id: string; name: string; startTime: string; endTime: string; breakMinutes: number; isNightShift: boolean } | null
}

export interface TimesheetSettings {
  lateGraceMinutes: number
  defaultStart: string
  defaultEnd: string
  defaultBreakMinutes: number
  // Dalga 9: çalışma süresi kuralları (madde 66) ve konum doğrulama (madde 67); kaydederken isteğe bağlı.
  minRestHours?: number
  weeklyMaxHours?: number
  dailyMaxHours?: number
  nightMaxHours?: number
  maxConsecutiveDays?: number
  geoOutsidePolicy?: 'Block' | 'Flag'
  geoStoreRaw?: boolean
  geoRawRetentionDays?: number
}

/* ------------------------------------------------------------------ istemci */

export const opsApi = {
  // G14
  templates: (signal?: AbortSignal) => apiFetch<TaskTemplate[]>(`${ON}/onboarding-config/templates`, { signal }),
  createTemplate: (body: TemplateInput) => apiFetch<TaskTemplate>(`${ON}/onboarding-config/templates`, { method: 'POST', body }),
  updateTemplate: (id: string, body: TemplateInput) => apiFetch<TaskTemplate>(`${ON}/onboarding-config/templates/${id}`, { method: 'PUT', body }),
  deleteTemplate: (id: string) => apiFetch<void>(`${ON}/onboarding-config/templates/${id}`, { method: 'DELETE' }),
  settings: (signal?: AbortSignal) => apiFetch<OnboardingSettings>(`${ON}/onboarding-config/settings`, { signal }),
  saveSettings: (body: { welcomeSubject: string | null; welcomeBody: string | null; hrContactEmployeeId: string | null; reminderDaysBefore: number }) =>
    apiFetch<unknown>(`${ON}/onboarding-config/settings`, { method: 'PUT', body }),
  createPlan: (body: { employeeId: string; startDate: string; templateName?: string; useDefaultTasks: boolean; applyTemplates: boolean; buddyEmployeeId?: string | null; location?: string | null }) =>
    apiFetch<OnboardingPlan>(`${ON}/onboarding-plans`, { method: 'POST', body }),
  setBuddy: (planId: string, buddyEmployeeId: string | null) =>
    apiFetch<OnboardingPlan>(`${ON}/onboarding-plans/${planId}/buddy`, { method: 'PUT', body: { buddyEmployeeId } }),
  setLocation: (planId: string, location: string | null) =>
    apiFetch<unknown>(`${ON}/onboarding-plans/${planId}/location`, { method: 'PUT', body: { location } }),
  welcomePreview: (planId: string, signal?: AbortSignal) =>
    apiFetch<{ subject: string; body: string; welcomeSentAt: string | null; scheduledFor: string }>(`${ON}/onboarding-plans/${planId}/welcome-preview`, { signal }),

  // G16
  scan: (code: string, signal?: AbortSignal) => apiFetch<ScanResult>(`${ON}/assets/scan${qs({ kod: code })}`, { signal }),
  maintenance: (assetId: string, signal?: AbortSignal) => apiFetch<MaintenanceRecord[]>(`${ON}/assets/${assetId}/maintenance`, { signal }),
  addMaintenance: (assetId: string, body: { date: string; type: MaintenanceType; cost: number | null; vendor: string | null; notes: string | null; nextMaintenanceOn: string | null }) =>
    apiFetch<MaintenanceRecord>(`${ON}/assets/${assetId}/maintenance`, { method: 'POST', body }),
  deleteMaintenance: (assetId: string, id: string) => apiFetch<void>(`${ON}/assets/${assetId}/maintenance/${id}`, { method: 'DELETE' }),
  maintenanceDue: (days: number, signal?: AbortSignal) => apiFetch<MaintenanceDue[]>(`${ON}/assets/maintenance/due${qs({ days })}`, { signal }),
  returnsDue: (days: number, signal?: AbortSignal) => apiFetch<ReturnDue[]>(`${ON}/assets/returns-due${qs({ days })}`, { signal }),
  setExpectedReturn: (assetId: string, expectedReturnOn: string | null) =>
    apiFetch<unknown>(`${ON}/assets/${assetId}/expected-return`, { method: 'PUT', body: { expectedReturnOn } }),
  assignAsset: (assetId: string, body: { employeeId: string; assignedOn: string; notes?: string; expectedReturnOn?: string | null }) =>
    apiFetch<unknown>(`${ON}/assets/${assetId}/assign`, { method: 'POST', body }),

  // G15
  overrideAsset: (caseId: string, assignmentId: string, resolution: 'Lost' | 'WrittenOff', note: string) =>
    apiFetch<{ assetChecks: AssetCheck[]; warning: string | null }>(`${EN}/offboarding/${caseId}/assets/${assignmentId}`, { method: 'PATCH', body: { resolution, note } }),
  retryDisable: (caseId: string) =>
    apiFetch<{ accountStatus: AccountStatus; accountNote: string | null }>(`${EN}/offboarding/${caseId}/disable-account`, { method: 'POST' }),

  // G18
  enpsTrend: (signal?: AbortSignal) => apiFetch<EnpsTrend>(`${EN}/surveys/enps-trend`, { signal }),

  // G6
  myPreference: (signal?: AbortSignal) => apiFetch<ShiftPreference>(`${TS}/shift-preferences/me`, { signal }),
  savePreference: (body: Omit<ShiftPreference, 'employeeId' | 'updatedAt'>) => apiFetch<ShiftPreference>(`${TS}/shift-preferences/me`, { method: 'PUT', body }),
  preferences: (employeeIds?: string[], signal?: AbortSignal) =>
    apiFetch<ShiftPreference[]>(`${TS}/shift-preferences${qs({ employeeIds: employeeIds?.join(',') })}`, { signal }),
  checkPreference: (body: { employeeId: string; shiftId: string; date: string }) =>
    apiFetch<PreferenceCheck>(`${TS}/shift-preferences/check`, { method: 'POST', body }),
  swaps: (scope?: 'mine' | 'approvals' | 'all', signal?: AbortSignal) => apiFetch<SwapRequest[]>(`${TS}/shift-swaps${qs({ scope })}`, { signal }),
  createSwap: (body: { myAssignmentId: string; targetEmployeeId: string; targetAssignmentId?: string | null; note?: string }) =>
    apiFetch<{ id: string; status: SwapStatus }>(`${TS}/shift-swaps`, { method: 'POST', body }),
  respondSwap: (id: string, accept: boolean) => apiFetch<unknown>(`${TS}/shift-swaps/${id}/respond`, { method: 'POST', body: { accept } }),
  decideSwap: (id: string, approve: boolean, reason?: string) => apiFetch<unknown>(`${TS}/shift-swaps/${id}/decide`, { method: 'POST', body: { approve, reason } }),
  /** Takas için seçilebilecek ekip arkadaşları (aynı vardiya ekibi; ekipsizse aynı departman). */
  swapPeers: (signal?: AbortSignal) => apiFetch<{ id: string; fullName: string }[]>(`${TS}/shift-swaps/peers`, { signal }),
  cancelSwap: (id: string) => apiFetch<unknown>(`${TS}/shift-swaps/${id}/cancel`, { method: 'POST' }),

  roster: (f: { from: string; to: string; employeeId?: string }, signal?: AbortSignal) =>
    apiFetch<RosterAssignment[]>(`${TS}/shifts/roster${qs(f)}`, { signal }),
  assignShift: (shiftId: string, employeeId: string, date: string) =>
    apiFetch<unknown>(`${TS}/shifts/${shiftId}/assign`, { method: 'POST', body: { employeeId, date } }),
  /** Hatalı atamayı geri alır (geçmiş atama ve bekleyen takasa konu atama kaldırılamaz). */
  unassignShift: (assignmentId: string) => apiFetch<unknown>(`${TS}/shifts/assignments/${assignmentId}`, { method: 'DELETE' }),
  ruleCheck: (body: { employeeId: string; date: string; shiftId?: string; startTime?: string; endTime?: string; breakMinutes?: number }) =>
    apiFetch<{ warnings: RuleWarning[] }>(`${TS}/shifts/rule-check`, { method: 'POST', body }),

  // G7
  attendance: (f: { from: string; to: string; employeeId?: string; departmentId?: string }, signal?: AbortSignal) =>
    apiFetch<AttendanceReport>(`${TS}/timesheet-report${qs(f)}`, { signal }),
  timesheetSettings: (signal?: AbortSignal) => apiFetch<TimesheetSettings>(`${TS}/timesheet-report/settings`, { signal }),
  saveTimesheetSettings: (body: TimesheetSettings) => apiFetch<TimesheetSettings>(`${TS}/timesheet-report/settings`, { method: 'PUT', body }),
}

export const isoDayLabels = ['', tx('Pzt'), tx('Sal'), tx('Çar'), tx('Per'), tx('Cum'), tx('Cmt'), tx('Paz')]
