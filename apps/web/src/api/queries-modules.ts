import { useQuery } from '@tanstack/react-query'
import { leaveApi, type LeaveRequestFilters } from './leave'
import { recruitmentApi, type ApplicationStatus, type JobPostingStatus } from './recruitment'
import { onboardingApi, type AssetStatus, type AssetType, type PlanStatus } from './onboarding'
import { timeshiftApi } from './timeshift'
import { learningApi, type CourseCategory } from './learning'
import { compensationApi } from './compensation'
import { expenseApi, type CasePriority, type CaseStatus, type ClaimStatus } from './expense'
import { notificationApi, type NotificationStatus } from './notification'

/**
 * Yeni modüllerin sorgu anahtarları ve hook'ları.
 *
 * Sayfalar useQuery'yi doğrudan çağırmaz; her veri erişimi burada bir hook
 * olarak tanımlanır. Böylece anahtarlar tek yerde durur ve geçersiz kılma
 * (invalidate) hataları önlenir.
 */
export const qkx = {
  leaveBalances: (employeeId?: string, year?: number) =>
    ['leave', 'balances', employeeId ?? 'all', year ?? 'all'] as const,
  leaveRequests: (f: LeaveRequestFilters) => ['leave', 'requests', f] as const,
  leaveRequest: (id: string) => ['leave', 'requests', 'detail', id] as const,
  leaveHolidays: (year?: number) => ['leave', 'holidays', year ?? 'all'] as const,

  postings: (status?: JobPostingStatus) => ['recruitment', 'postings', status ?? 'all'] as const,
  posting: (id: string) => ['recruitment', 'postings', id] as const,
  candidates: (search?: string) => ['recruitment', 'candidates', search ?? ''] as const,
  candidate: (id: string) => ['recruitment', 'candidates', id] as const,
  applications: (f: { jobPostingId?: string; status?: ApplicationStatus }) =>
    ['recruitment', 'applications', f] as const,
  application: (id: string) => ['recruitment', 'applications', 'detail', id] as const,

  plans: (f: { employeeId?: string; status?: PlanStatus }) => ['onboarding', 'plans', f] as const,
  plan: (id: string) => ['onboarding', 'plans', 'detail', id] as const,
  assets: (f: { status?: AssetStatus; type?: AssetType }) => ['onboarding', 'assets', f] as const,
  assetsByEmployee: (employeeId: string) =>
    ['onboarding', 'assets', 'employee', employeeId] as const,

  shifts: (departmentId?: string) => ['timeshift', 'shifts', departmentId ?? 'all'] as const,
  roster: (f: { from?: string; to?: string; employeeId?: string }) =>
    ['timeshift', 'roster', f] as const,
  timeEntries: (f: { employeeId?: string; from?: string; to?: string }) =>
    ['timeshift', 'entries', f] as const,
  timeSummary: (employeeId: string, year: number, month: number) =>
    ['timeshift', 'summary', employeeId, year, month] as const,

  courses: (f: { category?: CourseCategory; mandatoryOnly?: boolean }) =>
    ['learning', 'courses', f] as const,
  course: (id: string) => ['learning', 'courses', 'detail', id] as const,
  compliance: ['learning', 'compliance'] as const,
  certifications: (employeeId?: string) =>
    ['learning', 'certifications', employeeId ?? 'all'] as const,
  expiringCertifications: (withinDays: number) =>
    ['learning', 'certifications', 'expiring', withinDays] as const,

  bands: (year?: number) => ['compensation', 'bands', year ?? 'all'] as const,
  compRecords: (employeeId?: string) => ['compensation', 'records', employeeId ?? 'all'] as const,

  claims: (f: { employeeId?: string; status?: ClaimStatus }) => ['expense', 'claims', f] as const,
  claim: (id: string) => ['expense', 'claims', 'detail', id] as const,
  documents: (f: { employeeId?: string; type?: string }) => ['expense', 'documents', f] as const,
  cases: (f: { employeeId?: string; status?: CaseStatus; priority?: CasePriority }) =>
    ['expense', 'cases', f] as const,
  case: (id: string) => ['expense', 'cases', 'detail', id] as const,

  notifications: (f: { recipientId?: string; status?: NotificationStatus; limit?: number }) =>
    ['notification', 'list', f] as const,
  unreadCount: (recipientId: string) => ['notification', 'unread', recipientId] as const,
  templates: ['notification', 'templates'] as const,
}

/* --------------------------------------------------------------------- İzin */

export function useLeaveBalances(employeeId?: string, year?: number, enabled = true) {
  return useQuery({
    queryKey: qkx.leaveBalances(employeeId, year),
    queryFn: ({ signal }) => leaveApi.listBalances(employeeId, year, signal),
    enabled,
  })
}

/** Resmi tatiller — izin formundaki gün önizlemesi ve İK tatil yönetimi. */
export function useLeaveHolidays(year?: number, enabled = true) {
  return useQuery({
    queryKey: qkx.leaveHolidays(year),
    queryFn: ({ signal }) => leaveApi.listHolidays(year, signal),
    enabled,
    staleTime: 10 * 60_000,
  })
}

export function useLeaveRequests(filters: LeaveRequestFilters = {}, enabled = true) {
  return useQuery({
    queryKey: qkx.leaveRequests(filters),
    queryFn: ({ signal }) => leaveApi.listRequests(filters, signal),
    enabled,
  })
}

/* ----------------------------------------------------------------- İşe alım */

export function useJobPostings(status?: JobPostingStatus) {
  return useQuery({
    queryKey: qkx.postings(status),
    queryFn: ({ signal }) => recruitmentApi.listPostings(status, signal),
  })
}

export function useJobPosting(id: string | undefined) {
  return useQuery({
    queryKey: qkx.posting(id ?? ''),
    queryFn: ({ signal }) => recruitmentApi.getPosting(id!, signal),
    enabled: Boolean(id),
  })
}

export function useCandidates(search?: string, enabled = true) {
  return useQuery({
    queryKey: qkx.candidates(search),
    queryFn: ({ signal }) => recruitmentApi.listCandidates(search, signal),
    enabled,
  })
}

export function useCandidate(id: string | undefined) {
  return useQuery({
    queryKey: qkx.candidate(id ?? ''),
    queryFn: ({ signal }) => recruitmentApi.getCandidate(id!, signal),
    enabled: Boolean(id),
  })
}

export function useApplications(
  filters: { jobPostingId?: string; status?: ApplicationStatus } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.applications(filters),
    queryFn: ({ signal }) => recruitmentApi.listApplications(filters, signal),
    enabled,
  })
}

/* -------------------------------------------------------- Onboarding, zimmet */

export function useOnboardingPlans(
  filters: { employeeId?: string; status?: PlanStatus } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.plans(filters),
    queryFn: ({ signal }) => onboardingApi.listPlans(filters, signal),
    enabled,
  })
}

export function useOnboardingPlan(id: string | undefined) {
  return useQuery({
    queryKey: qkx.plan(id ?? ''),
    queryFn: ({ signal }) => onboardingApi.getPlan(id!, signal),
    enabled: Boolean(id),
  })
}

export function useAssets(filters: { status?: AssetStatus; type?: AssetType } = {}) {
  return useQuery({
    queryKey: qkx.assets(filters),
    queryFn: ({ signal }) => onboardingApi.listAssets(filters, signal),
  })
}

export function useAssetsByEmployee(employeeId: string | undefined) {
  return useQuery({
    queryKey: qkx.assetsByEmployee(employeeId ?? ''),
    queryFn: ({ signal }) => onboardingApi.assetsByEmployee(employeeId!, signal),
    enabled: Boolean(employeeId),
  })
}

/* ------------------------------------------------------------------ Puantaj */

export function useShifts(departmentId?: string) {
  return useQuery({
    queryKey: qkx.shifts(departmentId),
    queryFn: ({ signal }) => timeshiftApi.listShifts(departmentId, signal),
  })
}

export function useRoster(filters: { from?: string; to?: string; employeeId?: string } = {}) {
  return useQuery({
    queryKey: qkx.roster(filters),
    queryFn: ({ signal }) => timeshiftApi.roster(filters, signal),
  })
}

export function useTimeEntries(
  filters: { employeeId?: string; from?: string; to?: string } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.timeEntries(filters),
    queryFn: ({ signal }) => timeshiftApi.listEntries(filters, signal),
    enabled,
  })
}

export function useTimeSummary(
  employeeId: string | undefined,
  year: number,
  month: number,
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.timeSummary(employeeId ?? '', year, month),
    queryFn: ({ signal }) => timeshiftApi.summary(employeeId!, year, month, signal),
    enabled: Boolean(employeeId) && enabled,
  })
}

/* -------------------------------------------------------------------- Eğitim */

export function useCourses(filters: { category?: CourseCategory; mandatoryOnly?: boolean } = {}) {
  return useQuery({
    queryKey: qkx.courses(filters),
    queryFn: ({ signal }) => learningApi.listCourses(filters, signal),
  })
}

export function useCertifications(employeeId?: string) {
  return useQuery({
    queryKey: qkx.certifications(employeeId),
    queryFn: ({ signal }) => learningApi.listCertifications(employeeId, signal),
  })
}

export function useExpiringCertifications(withinDays = 90, enabled = true) {
  return useQuery({
    queryKey: qkx.expiringCertifications(withinDays),
    queryFn: ({ signal }) => learningApi.expiringCertifications(withinDays, signal),
    enabled,
  })
}

export function useCompliance(enabled = true) {
  return useQuery({
    queryKey: qkx.compliance,
    queryFn: ({ signal }) => learningApi.compliance(signal),
    enabled,
  })
}

/* --------------------------------------------------------------------- Ücret */

export function useCompensationBands(year?: number, enabled = true) {
  return useQuery({
    queryKey: qkx.bands(year),
    queryFn: ({ signal }) => compensationApi.listBands(year, signal),
    enabled,
  })
}

export function useCompensationRecords(employeeId?: string, enabled = true) {
  return useQuery({
    queryKey: qkx.compRecords(employeeId),
    queryFn: ({ signal }) => compensationApi.listRecords(employeeId, signal),
    enabled,
  })
}

/* ------------------------------------------------------- Masraf, doküman, vaka */

export function useExpenseClaims(
  filters: { employeeId?: string; status?: ClaimStatus } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.claims(filters),
    queryFn: ({ signal }) => expenseApi.listClaims(filters, signal),
    enabled,
  })
}

export function useExpenseClaim(id: string | undefined) {
  return useQuery({
    queryKey: qkx.claim(id ?? ''),
    queryFn: ({ signal }) => expenseApi.getClaim(id!, signal),
    enabled: Boolean(id),
  })
}

export function useHrCases(
  filters: { employeeId?: string; status?: CaseStatus; priority?: CasePriority } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.cases(filters),
    queryFn: ({ signal }) => expenseApi.listCases(filters, signal),
    enabled,
  })
}

export function useHrCase(id: string | undefined) {
  return useQuery({
    queryKey: qkx.case(id ?? ''),
    queryFn: ({ signal }) => expenseApi.getCase(id!, signal),
    enabled: Boolean(id),
  })
}

export function useDocuments(
  filters: { employeeId?: string; type?: string } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.documents(filters),
    queryFn: ({ signal }) => expenseApi.listDocuments(filters, signal),
    enabled,
  })
}

/* --------------------------------------------------------------- Bildirimler */

export function useNotifications(
  filters: { recipientId?: string; status?: NotificationStatus; limit?: number } = {},
  enabled = true,
) {
  return useQuery({
    queryKey: qkx.notifications(filters),
    queryFn: ({ signal }) => notificationApi.list(filters, signal),
    enabled,
  })
}

export function useUnreadCount(recipientId: string | undefined) {
  return useQuery({
    queryKey: qkx.unreadCount(recipientId ?? ''),
    queryFn: ({ signal }) => notificationApi.unreadCount(recipientId!, signal),
    enabled: Boolean(recipientId),
    // Çan rozetinin güncel kalması için düzenli tazelenir.
    refetchInterval: 60_000,
  })
}

export function useNotificationTemplates(enabled = true) {
  return useQuery({
    queryKey: qkx.templates,
    queryFn: ({ signal }) => notificationApi.listTemplates(signal),
    enabled,
  })
}
