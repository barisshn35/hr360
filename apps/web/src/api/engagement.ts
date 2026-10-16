import { apiFetch, platformTenantHeaders, qs } from './client'
import { getValidToken } from '@/auth/keycloak'
import { env } from '@/lib/env'
import { tx } from '@/lib/i18n'

/* ============================== engagement-service ==============================
 * Çalışan deneyimi: takdir, kutlama, ofis, mentorluk, iç ilan, 1:1, ardıl
 * planlama, anket/eNPS, offboarding, ekip sağlığı, org senaryoları, profil.
 * ============================================================================== */

const BASE = '/api/engagement'

/* ------------------------------------------------------------------ takdir */
export interface Kudos {
  id: string
  fromName: string
  fromEmployeeId: string | null
  toEmployeeId: string
  toName: string
  badge: string
  badgeLabel: string
  message: string
  /** Takdirle gönderilen puan (puanlar kapalıysa 0). */
  points: number
  createdAt: string
  likeCount: number
  likedByMe: boolean
  mine: boolean
}
export interface KudosBadge { id: string; label: string }
export interface KudosLeaderboard {
  total: number
  givers: number
  top: Array<{ employeeId: string; name: string; count: number; topBadge: string }>
  badges: Array<{ badge: string; label: string; count: number }>
}

/* -------------------------------------------------------------- kutlamalar */
export interface Celebration {
  kind: 'birthday' | 'anniversary' | 'newcomer'
  employeeId: string
  name: string
  department: string | null
  date: string
  inDays: number
  years: number | null
}

/* ------------------------------------------------------------------- profil */
export interface MyProfile {
  employeeId: string
  name: string
  email: string | null
  position: string | null
  department: string | null
  hireDate: string
  status: string
  birthDate: string | null
  showBirthday: boolean
  bio: string | null
  pronouns: string | null
  skills: string[]
  interests: string[]
  address: string | null
  emergencyContactName: string | null
  emergencyContactPhone: string | null
  linkedInUrl: string | null
  iban: string | null
  nationalId: string | null
  hasIban: boolean
  hasNationalId: boolean
  updatedAt: string | null
}
export type ProfileUpdate = Partial<
  Pick<MyProfile, 'birthDate' | 'showBirthday' | 'bio' | 'pronouns' | 'skills' | 'interests' | 'address' |
    'emergencyContactName' | 'emergencyContactPhone' | 'iban' | 'nationalId' | 'linkedInUrl'>
>
export interface DirectoryEntry {
  employeeId: string
  name: string
  position: string | null
  department: string | null
  email: string | null
  skills: string[]
  interests: string[]
  bio: string | null
}

/* ------------------------------------------------------------------- ofis */
export type PresenceMode = 'Office' | 'Remote' | 'Travel' | 'Off' | 'Leave' | 'Unknown'
export const presenceLabels: Record<PresenceMode, string> = {
  Office: tx('Ofiste'), Remote: tx('Uzaktan'), Travel: tx('Seyahatte'), Off: tx('Çalışmıyor'), Leave: tx('İzinli'), Unknown: tx('Bildirmedi'),
}
export interface Desk {
  id: string
  code: string
  name: string
  kind: 'Desk' | 'Room'
  floor: string | null
  zone: string | null
  capacity: number
  features: string[]
}
export interface Booking {
  id: string
  deskId: string
  personName: string
  employeeId: string | null
  date: string
  startMinute: number
  endMinute: number
  title: string | null
  mine: boolean
}
export interface PresenceBoard {
  from: string
  to: string
  days: string[]
  people: Array<{
    employeeId: string
    name: string
    department: string | null
    position: string | null
    days: Array<{ date: string; mode: PresenceMode; note: string | null }>
  }>
  today: Record<PresenceMode, number>
}

/* -------------------------------------------------------------- mentorluk */
export interface MentorProfile {
  userId: string
  personName: string
  employeeId: string | null
  isMentor: boolean
  isMentee: boolean
  offers: string[]
  wants: string[]
  capacity: number
  bio: string | null
  activeMentees?: number
  isMe?: boolean
}
export interface MentorMatch {
  userId: string
  personName: string
  department: string | null
  offers: string[]
  bio: string | null
  common: string[]
  freeSlots: number
  score: number
  pending: boolean
}
export type MentorshipStatus = 'Requested' | 'Active' | 'Completed' | 'Declined'
export interface Mentorship {
  id: string
  mentorUserId: string
  mentorName: string
  menteeUserId: string
  menteeName: string
  goal: string | null
  status: MentorshipStatus
  matchScore: number
  createdAt: string
  startedAt: string | null
  endedAt: string | null
  iAmMentor: boolean
  iAmMentee: boolean
}

/* --------------------------------------------------------------- iç ilan */
export type InternalAppStatus = 'Submitted' | 'Reviewing' | 'Interview' | 'Accepted' | 'Rejected' | 'Withdrawn'
export const internalAppLabels: Record<InternalAppStatus, string> = {
  Submitted: tx('Alındı'), Reviewing: tx('İnceleniyor'), Interview: tx('Görüşme'), Accepted: tx('Kabul'), Rejected: tx('Olumsuz'), Withdrawn: tx('Geri çekildi'),
}
export interface InternalPosting {
  id: string
  title: string
  description: string | null
  employmentType: string | null
  headcount: number
  publishedAt: string | null
  department: string | null
  internalApplicants: number
  myApplication: { id: string; status: InternalAppStatus; createdAt: string } | null
}
export interface InternalApplication {
  id: string
  jobPostingId: string
  jobTitle: string
  personName: string
  employeeId: string | null
  motivation: string | null
  status: InternalAppStatus
  createdAt: string
  updatedAt: string
  currentPosition: string | null
  currentDepartment: string | null
  mine: boolean
}

/* ------------------------------------------------------------------- 1:1 */
export interface AgendaItem { id?: string; text: string; done: boolean; by?: string | null; due?: string | null }
export interface OneOnOne {
  id: string
  managerUserId: string
  managerName: string
  employeeId: string
  employeeName: string
  scheduledAt: string
  status: 'Planned' | 'Done' | 'Cancelled'
  agenda: AgendaItem[]
  sharedNotes: string | null
  privateNotes: string | null
  actionItems: AgendaItem[]
  mood: number | null
  createdAt: string
  iAmManager: boolean
}
export interface OneOnOneTeamRow {
  employeeId: string
  name: string
  position: string | null
  department: string | null
  lastMeetingAt: string | null
  nextMeetingAt: string | null
  daysSinceLast: number | null
  openActions: number
  lastMood: number | null
}

/* ------------------------------------------------------------ ardıl planı */
export type Readiness = 'ReadyNow' | 'OneToTwoYears' | 'ThreePlusYears'
export const readinessLabels: Record<Readiness, string> = {
  ReadyNow: tx('Şimdi hazır'), OneToTwoYears: tx('1–2 yıl'), ThreePlusYears: tx('3+ yıl'),
}
export type Level = 'High' | 'Medium' | 'Low'
export const levelLabels: Record<Level, string> = { High: tx('Yüksek'), Medium: tx('Orta'), Low: tx('Düşük') }
export interface SuccessionPlan {
  id: string
  positionTitle: string
  departmentName: string | null
  incumbentEmployeeId: string | null
  incumbentName: string | null
  criticality: Level
  vacancyRisk: Level
  notes: string | null
  updatedAt: string
  benchStrength: 'Strong' | 'Developing' | 'None'
  candidates: Array<{ employeeId: string; name: string; readiness: Readiness; notes: string | null; score: number | null }>
}
export interface SuccessionInput {
  positionTitle: string
  departmentName?: string | null
  incumbentEmployeeId?: string | null
  criticality: Level
  vacancyRisk: Level
  candidates: Array<{ employeeId: string; name: string; readiness: Readiness; notes?: string | null }>
  notes?: string | null
}
export interface SuccessorSuggestion {
  employeeId: string
  name: string
  position: string | null
  department: string | null
  score: number | null
  tenureYears: number
  sameDepartment: boolean
}

/* ------------------------------------------------------------------ anket */
export type QuestionType = 'Nps' | 'Scale' | 'Choice' | 'Text'
export interface SurveyQuestion { id: string; text: string; type: QuestionType; options: string[]; required: boolean }
export interface Survey {
  id: string
  title: string
  description: string | null
  kind: 'eNPS' | 'Pulse' | 'Custom'
  questions: SurveyQuestion[]
  isAnonymous: boolean
  status: 'Draft' | 'Open' | 'Closed'
  closesAt: string | null
  createdByName: string | null
  createdAt: string
  responseCount: number
  answered: boolean
}
export interface SurveyAnswer { questionId: string; score?: number | null; choice?: string | null; text?: string | null }
export interface SurveyResults {
  survey: { id: string; title: string; kind: string; status: string; isAnonymous: boolean; closesAt: string | null }
  /** G18: toplam yanıt en küçük grup sayısının (5) altındaysa hiçbir kırılım dönmez. */
  hidden?: boolean
  responseCount: number
  eligible: number
  participation: number
  anonymityThreshold: number
  questions: Array<{
    id: string
    text: string
    type: QuestionType
    count: number
    enps?: number | null
    promoters?: number
    passives?: number
    detractors?: number
    average?: number | null
    favorable?: number | null
    distribution?: number[]
    options?: Array<{ option: string; count: number }>
    texts?: string[]
    hiddenForAnonymity?: boolean
    /** G18: yerel sözlük tabanlı duygu özeti (en az 5 metin yanıtta). */
    sentiment?: { positive: number; negative: number; neutral: number; topKeywords: Array<{ word: string; count: number }>; method: string } | null
  }>
  byDepartment: Array<{ department: string; count: number | null; hidden: boolean; enps: number | null; favorable: number | null }>
}

/* -------------------------------------------------------------- offboarding */
export type OffboardingReason = 'Resignation' | 'Termination' | 'Retirement' | 'ContractEnd' | 'Other'
export const offboardingReasonLabels: Record<OffboardingReason, string> = {
  Resignation: tx('İstifa'), Termination: tx('İşveren feshi'), Retirement: tx('Emeklilik'), ContractEnd: tx('Sözleşme sonu'), Other: tx('Diğer'),
}
export interface ChecklistItem { key: string; title: string; owner: string; done: boolean; doneAt: string | null; doneBy: string | null; hint: string | null }
export interface ExitInterview {
  primaryReason?: string | null
  managerScore?: number | null
  cultureScore?: number | null
  growthScore?: number | null
  compensationScore?: number | null
  wouldRecommend?: boolean | null
  comments?: string | null
}
export interface OffboardingSummary {
  id: string
  employeeId: string
  employeeName: string
  lastWorkingDay: string
  reason: OffboardingReason
  status: 'Open' | 'Completed' | 'Cancelled'
  createdAt: string
  completedAt: string | null
  rehireEligible: boolean | null
  progress: number
  total: number
  done: number
  hasInterview: boolean
  openAssets?: number
  accountStatus?: import('./opsPlus').AccountStatus | null
  plannedAnonymizationOn?: string | null
}
export interface OffboardingCase {
  id: string
  employeeId: string
  employeeName: string
  lastWorkingDay: string
  reason: OffboardingReason
  status: 'Open' | 'Completed' | 'Cancelled'
  checklist: ChecklistItem[]
  exitInterview: ExitInterview | null
  rehireEligible: boolean | null
  createdAt: string
  completedAt: string | null
  /** G15: zimmet iade listesi, hesap kapatma durumu ve imha planı. */
  assetChecks?: import('./opsPlus').AssetCheck[]
  accountStatus?: import('./opsPlus').AccountStatus | null
  accountDisabledAt?: string | null
  accountNote?: string | null
  retentionMonths?: number | null
  plannedAnonymizationOn?: string | null
}
export interface Settlement {
  employee: string
  hireDate: string
  lastWorkingDay: string
  reason: OffboardingReason
  tenureYears: number
  grossMonthly: number | null
  /** Bordro parametrelerinden; girilmemişse null (kıdem tutarı hesaplanmaz). */
  severanceCeiling: number | null
  severanceCeilingMissing?: boolean
  severance: { eligible: boolean; gross: number | null; stampTax: number | null; net: number | null; basis: string }
  notice: { weeks: number; applies: boolean; gross: number; basis: string }
  unusedLeave: { days: number; gross: number; basis: string }
  totalGross: number
  disclaimer: string
  hasSalary: boolean
}

/* ------------------------------------------------------------ ekip sağlığı */
export interface TeamHealthMember {
  employeeId: string
  name: string
  position: string | null
  department: string | null
  daysSinceLeave: number
  leaveDays90: number
  overtimeHours30: number
  workedDays30: number
  latestScore: number | null
  scoreTrend: number
  kudos90: number
  lastOneOnOne: string | null
  lastMood: number | null
  flags: string[]
  risk: 'High' | 'Medium' | 'Low'
}
export interface TeamHealth {
  members: TeamHealthMember[]
  summary: {
    size: number
    atRisk?: number
    watch?: number
    avgOvertimeHours?: number
    avgDaysSinceLeave?: number
    kudos90?: number
    oneOnOneCoverage?: number
  }
}

/* --------------------------------------------------------- org senaryosu */
export interface OrgMove {
  employeeId: string
  name: string
  fromDepartmentId?: string | null
  toDepartmentId?: string | null
  newPosition?: string | null
  kind: 'Move' | 'Exit' | 'Hire'
  plannedSalary?: number | null
}
export interface OrgScenario {
  id: string
  name: string
  description: string | null
  moves: OrgMove[]
  status: 'Draft' | 'Shared' | 'Archived'
  createdByName: string | null
  createdAt: string
  updatedAt: string
}
export interface OrgBaseline {
  departments: Array<{ id: string; name: string; parentId: string | null; headEmployeeId: string | null }>
  people: Array<{ employeeId: string; name: string; position: string | null; departmentId: string | null; department: string | null }>
}
export interface ScenarioImpact {
  scenario: OrgScenario
  departments: Array<{ departmentId: string | null; department: string; before: number; after: number; monthlyCostBefore: number | null; monthlyCostAfter: number | null }>
  totals: { headcountBefore: number; headcountAfter: number; monthlyCostDelta: number | null; hireCost: number | null; movedPeople: number }
  costVisible: boolean
}

/** Dosya (ics/json/csv) indiren yardımcı — Authorization başlığıyla. */
export async function downloadAuthed(path: string, fileName: string) {
  const token = await getValidToken()
  const res = await fetch(`${env.apiBase}${path}`, { headers: token ? { Authorization: `Bearer ${token}`, ...platformTenantHeaders() } : {} })
  if (!res.ok) throw new Error(tx('İndirilemedi (HTTP {0})', [res.status]))
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 2000)
}

export const engagementApi = {
  /* takdir */
  kudosBadges: (signal?: AbortSignal) => apiFetch<KudosBadge[]>(`${BASE}/kudos/badges`, { signal }),
  kudos: (params: { limit?: number; employeeId?: string } = {}, signal?: AbortSignal) =>
    apiFetch<Kudos[]>(`${BASE}/kudos${qs(params)}`, { signal }),
  kudosLeaderboard: (days = 30, signal?: AbortSignal) => apiFetch<KudosLeaderboard>(`${BASE}/kudos/leaderboard${qs({ days })}`, { signal }),
  sendKudos: (body: { toEmployeeId: string; badge: string; message: string; points?: number }) =>
    apiFetch<{ id: string }>(`${BASE}/kudos`, { method: 'POST', body }),
  likeKudos: (id: string) => apiFetch<{ likeCount: number; likedByMe: boolean }>(`${BASE}/kudos/${id}/like`, { method: 'POST' }),
  deleteKudos: (id: string) => apiFetch<void>(`${BASE}/kudos/${id}`, { method: 'DELETE' }),

  celebrations: (days = 30, signal?: AbortSignal) => apiFetch<Celebration[]>(`${BASE}/celebrations${qs({ days })}`, { signal }),

  /* profil */
  myProfile: (signal?: AbortSignal) => apiFetch<MyProfile>(`${BASE}/profile/me`, { signal }),
  updateMyProfile: (body: ProfileUpdate) => apiFetch<MyProfile>(`${BASE}/profile/me`, { method: 'PUT', body }),
  reveal: (employeeId: string, field: 'iban' | 'nationalId', reason?: string) =>
    apiFetch<{ field: string; value: string | null }>(`${BASE}/profile/${employeeId}/reveal${qs({ field, reason })}`),
  profile: (employeeId: string, signal?: AbortSignal) => apiFetch<Partial<MyProfile>>(`${BASE}/profile/${employeeId}`, { signal }),
  directory: (q: string, signal?: AbortSignal) =>
    apiFetch<{ people: DirectoryEntry[]; topSkills: Array<{ skill: string; count: number }> }>(`${BASE}/profile/directory${qs({ q })}`, { signal }),

  /* ofis */
  desks: (signal?: AbortSignal) => apiFetch<Desk[]>(`${BASE}/workplace/desks`, { signal }),
  createDesk: (body: Omit<Desk, 'id'>) => apiFetch<Desk>(`${BASE}/workplace/desks`, { method: 'POST', body }),
  deleteDesk: (id: string) => apiFetch<void>(`${BASE}/workplace/desks/${id}`, { method: 'DELETE' }),
  sampleDesks: () => apiFetch<{ created: number }>(`${BASE}/workplace/desks/sample`, { method: 'POST' }),
  bookings: (date: string, signal?: AbortSignal) => apiFetch<Booking[]>(`${BASE}/workplace/bookings${qs({ date })}`, { signal }),
  book: (body: { deskId: string; date: string; startMinute: number; endMinute: number; title?: string }) =>
    apiFetch<{ id: string }>(`${BASE}/workplace/bookings`, { method: 'POST', body }),
  cancelBooking: (id: string) => apiFetch<void>(`${BASE}/workplace/bookings/${id}`, { method: 'DELETE' }),
  presence: (from?: string, to?: string, signal?: AbortSignal) =>
    apiFetch<PresenceBoard>(`${BASE}/workplace/presence${qs({ from, to })}`, { signal }),
  setPresence: (body: { date: string; mode: PresenceMode; note?: string }) =>
    apiFetch<unknown>(`${BASE}/workplace/presence`, { method: 'PUT', body }),

  /* mentorluk */
  mentorProfiles: (signal?: AbortSignal) => apiFetch<MentorProfile[]>(`${BASE}/mentorship/profiles`, { signal }),
  myMentorProfile: (signal?: AbortSignal) => apiFetch<MentorProfile | null>(`${BASE}/mentorship/me`, { signal }),
  saveMentorProfile: (body: Pick<MentorProfile, 'isMentor' | 'isMentee' | 'offers' | 'wants' | 'capacity' | 'bio'>) =>
    apiFetch<MentorProfile>(`${BASE}/mentorship/me`, { method: 'PUT', body }),
  mentorMatches: (signal?: AbortSignal) => apiFetch<MentorMatch[]>(`${BASE}/mentorship/matches`, { signal }),
  mentorships: (signal?: AbortSignal) => apiFetch<Mentorship[]>(`${BASE}/mentorship`, { signal }),
  requestMentor: (body: { mentorUserId: string; goal?: string; matchScore: number }) =>
    apiFetch<{ id: string }>(`${BASE}/mentorship`, { method: 'POST', body }),
  mentorshipAction: (id: string, action: 'accept' | 'decline' | 'complete' | 'cancel') =>
    apiFetch<{ status: MentorshipStatus }>(`${BASE}/mentorship/${id}/${action}`, { method: 'POST' }),

  /* iç ilan */
  internalPostings: (signal?: AbortSignal) => apiFetch<InternalPosting[]>(`${BASE}/mobility/postings`, { signal }),
  applyInternal: (body: { jobPostingId: string; motivation?: string }) =>
    apiFetch<{ id: string }>(`${BASE}/mobility/applications`, { method: 'POST', body }),
  internalApplications: (signal?: AbortSignal) => apiFetch<InternalApplication[]>(`${BASE}/mobility/applications`, { signal }),
  setInternalStatus: (id: string, status: InternalAppStatus) =>
    apiFetch<{ status: InternalAppStatus }>(`${BASE}/mobility/applications/${id}`, { method: 'PATCH', body: { status } }),

  /* 1:1 */
  oneOnOnes: (employeeId?: string, signal?: AbortSignal) => apiFetch<OneOnOne[]>(`${BASE}/one-on-ones${qs({ employeeId })}`, { signal }),
  oneOnOneTeam: (signal?: AbortSignal) => apiFetch<OneOnOneTeamRow[]>(`${BASE}/one-on-ones/team`, { signal }),
  createOneOnOne: (body: { employeeId: string; scheduledAt: string; agenda?: string[] }) =>
    apiFetch<OneOnOne>(`${BASE}/one-on-ones`, { method: 'POST', body }),
  updateOneOnOne: (id: string, body: Partial<Pick<OneOnOne, 'scheduledAt' | 'status' | 'agenda' | 'sharedNotes' | 'privateNotes' | 'actionItems' | 'mood'>>) =>
    apiFetch<OneOnOne>(`${BASE}/one-on-ones/${id}`, { method: 'PUT', body }),
  deleteOneOnOne: (id: string) => apiFetch<void>(`${BASE}/one-on-ones/${id}`, { method: 'DELETE' }),
  oneOnOneIcs: (o: OneOnOne) => downloadAuthed(`${BASE}/one-on-ones/${o.id}/ics`, `1on1-${o.scheduledAt.slice(0, 10)}.ics`),

  /* ardıl */
  succession: (signal?: AbortSignal) => apiFetch<SuccessionPlan[]>(`${BASE}/succession`, { signal }),
  createSuccession: (body: SuccessionInput) => apiFetch<{ id: string }>(`${BASE}/succession`, { method: 'POST', body }),
  updateSuccession: (id: string, body: SuccessionInput) => apiFetch<{ id: string }>(`${BASE}/succession/${id}`, { method: 'PUT', body }),
  deleteSuccession: (id: string) => apiFetch<void>(`${BASE}/succession/${id}`, { method: 'DELETE' }),
  suggestSuccessors: (incumbentEmployeeId?: string, signal?: AbortSignal) =>
    apiFetch<SuccessorSuggestion[]>(`${BASE}/succession/suggest${qs({ incumbentEmployeeId })}`, { signal }),

  /* anket */
  surveys: (signal?: AbortSignal) => apiFetch<Survey[]>(`${BASE}/surveys`, { signal }),
  createSurvey: (body: { title: string; description?: string; kind: string; questions: SurveyQuestion[]; isAnonymous: boolean; closesAt?: string | null }) =>
    apiFetch<{ id: string }>(`${BASE}/surveys`, { method: 'POST', body }),
  surveyFromTemplate: (kind: 'enps' | 'pulse') => apiFetch<{ id: string }>(`${BASE}/surveys/templates/${kind}`, { method: 'POST' }),
  setSurveyStatus: (id: string, status: Survey['status']) => apiFetch<unknown>(`${BASE}/surveys/${id}`, { method: 'PATCH', body: { status } }),
  deleteSurvey: (id: string) => apiFetch<void>(`${BASE}/surveys/${id}`, { method: 'DELETE' }),
  respond: (id: string, answers: SurveyAnswer[]) => apiFetch<unknown>(`${BASE}/surveys/${id}/responses`, { method: 'POST', body: { answers } }),
  surveyResults: (id: string, signal?: AbortSignal) => apiFetch<SurveyResults>(`${BASE}/surveys/${id}/results`, { signal }),

  /* offboarding */
  offboardings: (signal?: AbortSignal) => apiFetch<OffboardingSummary[]>(`${BASE}/offboarding`, { signal }),
  offboarding: (id: string, signal?: AbortSignal) => apiFetch<OffboardingCase>(`${BASE}/offboarding/${id}`, { signal }),
  startOffboarding: (body: { employeeId: string; lastWorkingDay: string; reason: OffboardingReason }) =>
    apiFetch<OffboardingCase>(`${BASE}/offboarding`, { method: 'POST', body }),
  toggleChecklist: (id: string, key: string, done: boolean) =>
    apiFetch<OffboardingCase>(`${BASE}/offboarding/${id}/checklist/${encodeURIComponent(key)}`, { method: 'PATCH', body: { done } }),
  saveExitInterview: (id: string, interview: ExitInterview, rehireEligible: boolean | null) =>
    apiFetch<OffboardingCase>(`${BASE}/offboarding/${id}/exit-interview`, { method: 'PUT', body: { interview, rehireEligible } }),
  completeOffboarding: (id: string, terminate = true) =>
    apiFetch<{ status: string; warning: string | null; accountStatus?: string | null; plannedAnonymizationOn?: string | null }>(`${BASE}/offboarding/${id}/complete${qs({ terminate })}`, { method: 'POST' }),
  cancelOffboarding: (id: string) => apiFetch<unknown>(`${BASE}/offboarding/${id}/cancel`, { method: 'POST' }),
  settlement: (id: string, signal?: AbortSignal) => apiFetch<Settlement>(`${BASE}/offboarding/${id}/settlement`, { signal }),

  /* ekip sağlığı */
  teamHealth: (departmentId?: string, signal?: AbortSignal) => apiFetch<TeamHealth>(`${BASE}/team-health${qs({ departmentId })}`, { signal }),

  /* org senaryoları */
  orgBaseline: (signal?: AbortSignal) => apiFetch<OrgBaseline>(`${BASE}/org-scenarios/baseline`, { signal }),
  orgScenarios: (signal?: AbortSignal) => apiFetch<OrgScenario[]>(`${BASE}/org-scenarios`, { signal }),
  createScenario: (body: { name: string; description?: string; moves?: OrgMove[] }) =>
    apiFetch<OrgScenario>(`${BASE}/org-scenarios`, { method: 'POST', body }),
  updateScenario: (id: string, body: { name: string; description?: string | null; moves?: OrgMove[]; status?: string }) =>
    apiFetch<OrgScenario>(`${BASE}/org-scenarios/${id}`, { method: 'PUT', body }),
  deleteScenario: (id: string) => apiFetch<void>(`${BASE}/org-scenarios/${id}`, { method: 'DELETE' }),
  scenarioImpact: (id: string, signal?: AbortSignal) => apiFetch<ScenarioImpact>(`${BASE}/org-scenarios/${id}/impact`, { signal }),
}
