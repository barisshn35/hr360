import { apiFetch, qs } from './client'

/**
 * İşyeri uyumu (governance-service, Dalga 5c): duyurular (Y14), doküman kütüphanesi (G19),
 * etik hattı (Y15), İSG (Y6), disiplin (Y7).
 */
const BASE = '/api/governance'

/* ---------------------------------------------------------------- ortak */
export interface AckStats {
  total: number | null
  read: number
  notRead: { employeeId: string; name: string; department: string | null }[]
}
export interface Option { value: string; label: string }

/* ---------------------------------------------------------------- Y14 duyurular */
export interface Announcement {
  id: string
  title: string
  body: string
  publishAt: string
  expireAt: string | null
  requiresAck: boolean
  read: boolean
  readAt: string | null
}
export interface ManagedAnnouncement {
  id: string
  title: string
  body: string
  audience: 'All' | 'Departments'
  departmentIds: string[]
  publishAt: string
  expireAt: string | null
  requiresAck: boolean
  notifiedAt: string | null
  createdBy: string
  createdAt: string
  state: 'Scheduled' | 'Published' | 'Expired'
  stats: AckStats
}
export interface AnnouncementInput {
  title: string
  body: string
  audience: 'All' | 'Departments'
  departmentIds: string[]
  publishAt?: string | null
  expireAt?: string | null
  requiresAck: boolean
}

/* ---------------------------------------------------------------- G19 kütüphane */
export type DocAudience = 'All' | 'Managers' | 'Hr' | 'Departments'
export interface LibraryDoc {
  id: string
  title: string
  category: string
  categoryLabel: string
  audience: DocAudience
  departmentIds: string[]
  requiresAck: boolean
  version: number
  archived: boolean
  publishedAt: string | null
  hasFile: boolean
  acknowledgedVersion: number | null
  acknowledgedAt: string | null
  needsAck: boolean
}
export interface LibraryVersion {
  id: string
  versionNo: number
  title: string
  body: string
  externalUrl: string | null
  storageKey: string | null
  changeNote: string | null
  publishedBy: string
  publishedAt: string
}
export interface LibraryDocDetail extends Omit<LibraryDoc, 'publishedAt' | 'hasFile'> {
  current: LibraryVersion | null
  history: { versionNo: number; publishedAt: string; publishedBy: string; changeNote: string | null }[]
}
export interface SearchHit {
  id: string
  title: string
  category: string
  categoryLabel: string
  version: number
  /** Eşleşmeler ⟦ ⟧ ile işaretli düz metin (HTML değil). */
  snippet: string
  rank: number
  requiresAck: boolean
}
export interface DocInput {
  title: string
  category: string
  audience: DocAudience
  departmentIds: string[]
  requiresAck: boolean
  body: string
  externalUrl?: string | null
  storageKey?: string | null
  changeNote?: string | null
}

/* ---------------------------------------------------------------- Y15 etik */
export interface EthicsPublicInfo { company: string; enabled: boolean; categories: Option[] }
export interface EthicsMessage { fromReporter: boolean; author: string | null; body: string; createdOn: string }
export type EthicsStatus = 'Received' | 'InReview' | 'Closed'
export interface EthicsPublicStatus {
  category: string
  categoryLabel: string
  status: EthicsStatus
  outcome: string | null
  receivedOn: string
  messages: EthicsMessage[]
}
export interface EthicsReportRow {
  id: string
  category: string
  categoryLabel: string
  status: EthicsStatus
  receivedOn: string
  updatedAt: string
  hasContact: boolean
  messages: number
  awaitingReply: boolean
}
export interface EthicsReportDetail {
  id: string
  category: string
  categoryLabel: string
  description: string
  status: EthicsStatus
  outcome: string | null
  receivedOn: string
  hasContact: boolean
  closedAt: string | null
  messages: EthicsMessage[]
}
export interface CommitteeMember { id: string; userId: string; employeeId: string | null; name: string; addedBy: string; addedAt: string }

/* ---------------------------------------------------------------- Y6 İSG */
export interface OshMe { canManage: boolean; isPhysician: boolean; isSpecialist: boolean; isManager: boolean }
export type IncidentKind = 'Accident' | 'NearMiss'
export interface OshIncident {
  id: string
  kind: IncidentKind
  occurredOn: string
  occurredTime: string | null
  location: string
  description: string
  injuredEmployeeId: string | null
  injuredName: string | null
  lostDays: number
  rootCause: string | null
  correctiveActions: string | null
  sgkNotifiedOn: string | null
  sgkReference: string | null
  status: 'Open' | 'Closed'
  createdBy: string
  createdAt: string
  sgkDeadline: string | null
  sgkOverdue: boolean
  sgkLate: boolean
  sgkDaysLeft: number | null
}
export interface IncidentInput {
  kind: IncidentKind
  occurredOn: string
  occurredTime?: string | null
  location?: string
  description: string
  injuredEmployeeId?: string | null
  lostDays?: number
  rootCause?: string | null
  correctiveActions?: string | null
  sgkNotifiedOn?: string | null
  sgkReference?: string | null
  status?: 'Open' | 'Closed'
}
export type ExamResult = 'Fit' | 'Unfit' | 'Conditional'
export interface OshExam {
  id: string
  employeeId: string
  employeeName: string | null
  examType: string
  examDate: string
  nextDueDate: string | null
  result: ExamResult
  /** Yalnızca işyeri hekimine dolu; diğerlerinde null. */
  hasNotes: boolean | null
  recordedBy: string
  updatedAt: string
  isLatest: boolean
  dueState: 'Overdue' | 'DueSoon' | 'Ok' | 'None'
}
export interface ExamInput {
  employeeId: string
  examType: string
  examDate: string
  nextDueDate?: string | null
  hazardClass?: string | null
  result: ExamResult
  notes?: string | null
}
export interface OshTraining {
  id: string
  topic: string
  trainingDate: string
  durationHours: number
  validityMonths: number | null
  expiresOn: string | null
  trainer: string | null
  createdBy: string
  participantCount: number
  participants: { id: string; name: string }[]
}
export interface TrainingInput {
  topic: string
  trainingDate: string
  durationHours: number
  validityMonths?: number | null
  trainer?: string | null
  participantIds: string[]
}
export interface OshReminders {
  exams: { employeeId: string; employeeName: string; nextDueDate: string; result: ExamResult; state: 'Overdue' | 'DueSoon' }[]
  trainings: { employeeId: string; employeeName: string; topic: string; expiresOn: string; state: 'Overdue' | 'DueSoon' }[]
}

/* ---------------------------------------------------------------- Y7 disiplin */
export type CaseStatus = 'Open' | 'DefenceRequested' | 'DefenceReceived' | 'Decided' | 'Closed'
export interface DisciplinaryMeta {
  categories: Option[]
  decisions: Option[]
  minDefenceDeadline: string
  minDefenceBusinessDays: number
  canManage: boolean
}
export interface CaseRow {
  id: string
  employeeId: string
  employeeName: string | null
  department: string | null
  incidentDate: string
  category: string
  categoryLabel: string
  status: CaseStatus
  defenceDeadline: string | null
  defenceOverdue: boolean
  decision: string | null
  decisionLabel: string | null
  createdAt: string
}
export interface CaseDetail extends CaseRow {
  description: string
  defenceNotice: string | null
  defenceRequestedAt: string | null
  defenceText: string | null
  defenceSubmittedAt: string | null
  minutesText: string | null
  witnesses: string | null
  decisionNote: string | null
  decidedBy: string | null
  decidedAt: string | null
  closedAt: string | null
  createdBy: string
}
export interface MyCase {
  id: string
  incidentDate: string
  category: string
  categoryLabel: string
  status: CaseStatus
  defenceNotice: string | null
  defenceRequestedAt: string | null
  defenceDeadline: string | null
  defenceText: string | null
  defenceSubmittedAt: string | null
  canSubmitDefence: boolean
  decision: string | null
  decisionLabel: string | null
  decisionNote: string | null
  decidedAt: string | null
}

const post = <T>(path: string, body?: unknown) => apiFetch<T>(`${BASE}${path}`, { method: 'POST', body: body ?? {} })
const put = <T>(path: string, body: unknown) => apiFetch<T>(`${BASE}${path}`, { method: 'PUT', body })
const del = (path: string) => apiFetch<void>(`${BASE}${path}`, { method: 'DELETE' })
const get = <T>(path: string, signal?: AbortSignal) => apiFetch<T>(`${BASE}${path}`, { signal })

export const complianceApi = {
  /* Y14 */
  announcements: (s?: AbortSignal) => get<Announcement[]>('/announcements', s),
  manageAnnouncements: (s?: AbortSignal) => get<ManagedAnnouncement[]>('/announcements/manage', s),
  announcementStats: (id: string, s?: AbortSignal) => get<AckStats>(`/announcements/${id}/stats`, s),
  createAnnouncement: (b: AnnouncementInput) => post<{ id: string; notified: number }>('/announcements', b),
  /** Planlı ya da yayımdaki duyuruyu düzenler; okuma kayıtları korunur. */
  updateAnnouncement: (id: string, b: AnnouncementInput) => put<{ id: string; notified: number }>(`/announcements/${id}`, b),
  readAnnouncement: (id: string) => post<{ read: boolean }>(`/announcements/${id}/read`),
  expireAnnouncement: (id: string) => post<{ expired: boolean }>(`/announcements/${id}/expire`),
  deleteAnnouncement: (id: string) => del(`/announcements/${id}`),
  departments: (s?: AbortSignal) => get<{ id: string; name: string }[]>('/announcements/departments', s),

  /* G19 */
  libraryMeta: (s?: AbortSignal) => get<{ categories: Option[]; audiences: DocAudience[] }>('/library/meta', s),
  library: (category?: string, s?: AbortSignal) => get<LibraryDoc[]>(`/library${qs({ category })}`, s),
  libraryDoc: (id: string, s?: AbortSignal) => get<LibraryDocDetail>(`/library/${id}`, s),
  libraryVersion: (id: string, no: number, s?: AbortSignal) => get<Omit<LibraryVersion, 'id'>>(`/library/${id}/versions/${no}`, s),
  librarySearch: (q: string, s?: AbortSignal) => get<SearchHit[]>(`/library/search${qs({ q })}`, s),
  createDoc: (b: DocInput) => post<{ id: string; version: number }>('/library', b),
  newVersion: (id: string, b: { title?: string; body: string; externalUrl?: string | null; storageKey?: string | null; changeNote?: string | null }) =>
    post<{ id: string; version: number }>(`/library/${id}/versions`, b),
  updateDocMeta: (id: string, b: Partial<{ category: string; audience: DocAudience; departmentIds: string[]; requiresAck: boolean; archived: boolean }>) =>
    put<{ id: string }>(`/library/${id}`, b),
  ackDoc: (id: string, version: number) => post<{ acknowledgedVersion: number }>(`/library/${id}/ack`, { version }),
  docStats: (id: string, s?: AbortSignal) => get<{ version: number; stats: AckStats }>(`/library/${id}/stats`, s),

  /* Y15 — herkese açık uçlar oturumsuz çağrılır (jeton gönderilmez) */
  ethicsPublicInfo: (tenant: string, s?: AbortSignal) =>
    apiFetch<EthicsPublicInfo>(`${BASE}/ethics/public/${encodeURIComponent(tenant)}`, { signal: s, anonymous: true }),
  /** Bot koruması: imzalı zaman jetonu (form açılırken alınır, gönderimde eklenir). */
  ethicsFormToken: (tenant: string) =>
    apiFetch<{ token: string; minSeconds: number }>(`${BASE}/ethics/public/${encodeURIComponent(tenant)}/form-token`, { anonymous: true }),
  ethicsSubmit: (tenant: string, b: { category: string; description: string; contact?: string | null; formToken?: string; website?: string }) =>
    apiFetch<{ followUpCode: string; message: string }>(`${BASE}/ethics/public/${encodeURIComponent(tenant)}/reports`, { method: 'POST', body: b, anonymous: true, noQueue: true }),
  /** Kod URL'de taşınmaz (kayıtlara düşmesin): gövdede gönderilir. */
  ethicsStatus: (tenant: string, code: string) =>
    apiFetch<EthicsPublicStatus>(`${BASE}/ethics/public/${encodeURIComponent(tenant)}/reports/status`, { method: 'POST', body: { code }, anonymous: true, noQueue: true }),
  ethicsFollowUp: (tenant: string, code: string, body: string) =>
    apiFetch<{ sent: boolean }>(`${BASE}/ethics/public/${encodeURIComponent(tenant)}/reports/messages`, { method: 'POST', body: { code, body }, anonymous: true, noQueue: true }),
  ethicsMe: (s?: AbortSignal) => get<{ isMember: boolean; canManage: boolean; tenant: string }>('/ethics/me', s),
  ethicsCommittee: (s?: AbortSignal) => get<CommitteeMember[]>('/ethics/committee', s),
  addCommitteeMember: (employeeId: string) => post<{ id: string }>('/ethics/committee', { employeeId }),
  removeCommitteeMember: (id: string) => del(`/ethics/committee/${id}`),
  ethicsReports: (status?: string, s?: AbortSignal) => get<EthicsReportRow[]>(`/ethics/reports${qs({ status })}`, s),
  ethicsReport: (id: string, s?: AbortSignal) => get<EthicsReportDetail>(`/ethics/reports/${id}`, s),
  ethicsContact: (id: string) => get<{ contact: string }>(`/ethics/reports/${id}/contact`),
  ethicsSetStatus: (id: string, status: EthicsStatus, outcome?: string) => post<{ status: string }>(`/ethics/reports/${id}/status`, { status, outcome }),
  ethicsReply: (id: string, body: string) => post<{ sent: boolean }>(`/ethics/reports/${id}/messages`, { body }),

  /* Y6 */
  oshMe: (s?: AbortSignal) => get<OshMe>('/osh/me', s),
  incidents: (s?: AbortSignal) => get<OshIncident[]>('/osh/incidents', s),
  createIncident: (b: IncidentInput) => post<OshIncident>('/osh/incidents', b),
  updateIncident: (id: string, b: IncidentInput) => put<OshIncident>(`/osh/incidents/${id}`, b),
  deleteIncident: (id: string) => del(`/osh/incidents/${id}`),
  exams: (latest: boolean, s?: AbortSignal) => get<OshExam[]>(`/osh/exams${qs({ latest: latest || undefined })}`, s),
  createExam: (b: ExamInput) => post<{ id: string; nextDueDate: string }>('/osh/exams', b),
  deleteExam: (id: string) => del(`/osh/exams/${id}`),
  examNotes: (id: string) => get<{ notes: string | null }>(`/osh/exams/${id}/notes`),
  setExamNotes: (id: string, notes: string) => put<{ saved: boolean }>(`/osh/exams/${id}/notes`, { notes }),
  trainings: (s?: AbortSignal) => get<OshTraining[]>('/osh/trainings', s),
  createTraining: (b: TrainingInput) => post<{ id: string; expiresOn: string | null }>('/osh/trainings', b),
  deleteTraining: (id: string) => del(`/osh/trainings/${id}`),
  oshReminders: (s?: AbortSignal) => get<OshReminders>('/osh/reminders', s),

  /* Y7 */
  disciplinaryMeta: (s?: AbortSignal) => get<DisciplinaryMeta>('/disciplinary/meta', s),
  cases: (status?: string, s?: AbortSignal) => get<CaseRow[]>(`/disciplinary${qs({ status })}`, s),
  caseDetail: (id: string, s?: AbortSignal) => get<CaseDetail>(`/disciplinary/${id}`, s),
  createCase: (b: { employeeId: string; incidentDate: string; category: string; description: string }) =>
    post<{ id: string; warnings: string[] }>('/disciplinary', b),
  requestDefence: (id: string, deadline?: string | null, noticeText?: string | null) =>
    post<{ deadline: string; notice: string; warnings: string[] }>(`/disciplinary/${id}/defence-request`, { deadline: deadline || null, noticeText: noticeText || null }),
  saveMinutes: (id: string, minutesText: string, witnesses: string) => put<{ warnings: string[] }>(`/disciplinary/${id}/minutes`, { minutesText, witnesses }),
  decide: (id: string, decision: string, note?: string) => post<{ warnings: string[] }>(`/disciplinary/${id}/decision`, { decision, note }),
  closeCase: (id: string) => post<{ status: string }>(`/disciplinary/${id}/close`),
  myCases: (s?: AbortSignal) => get<MyCase[]>('/disciplinary/mine', s),
  submitDefence: (id: string, text: string) => post<{ warnings: string[] }>(`/disciplinary/mine/${id}/defence`, { text }),
}
