/**
 * Performans modülünün TanStack Query katmanı.
 *
 * Sayfalar `useQuery`'yi doğrudan çağırmaz. Anahtarlar tek fabrikada; her
 * mutasyonun hangi veriyi bayatlattığı da burada, yan yana:
 *
 *   metrik değişti       → metrikler, puanlar, öneriler
 *   ayar sürümü değişti  → ayar, geçmiş, puanlar, analizler, öneriler
 *   ekip değişti         → ekipler, ekip analizleri, öneriler
 *   dönem değişti        → dönemler, hazırlık, dönem sonuçları, öneriler
 *   hedef değişti        → hedefler, puanlar, analizler, öneriler
 *   değerlendirme gitti  → değerlendirmeler, puanlar, analizler, öneriler
 *   taslak kaydedildi    → yalnızca açık form (liste bayat işaretlenir)
 *   geri bildirim        → geri bildirim listeleri, özetler, "benim" raporu
 *
 * Bir ekran bayat veri gösteriyorsa bakılacak yer bu tablo.
 */

import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { ApiError } from '../client'
import {
  analyticsApi,
  cycleAnalyticsApi,
  cyclesApi,
  feedbackApi,
  goalsApi,
  metricsApi,
  recommendationsApi,
  reviewsApi,
  scoringApi,
  teamsApi,
} from './client'
import type {
  AddMemberInput,
  AnalyticsPeriod,
  CreateCycleInput,
  CreateGoalInput,
  CreateReviewInput,
  CycleFilters,
  CycleStatus,
  FeedbackFilters,
  FeedbackInput,
  GoalFilters,
  GoalProgressInput,
  UpdateGoalInput,
  MetricFilters,
  MetricInput,
  MetricTemplate,
  Review,
  ReviewFilters,
  ScoringConfigInput,
  SubmitReviewInput,
  TeamFilters,
  CreateTeamInput,
  UpdateTeamInput,
} from './types'

const ROOT = 'perf'

export const pk = {
  all: [ROOT] as const,

  metricsRoot: [ROOT, 'metrics'] as const,
  metrics: (f: MetricFilters = {}) => [ROOT, 'metrics', f] as const,

  scoring: [ROOT, 'scoring'] as const,
  scoringHistory: [ROOT, 'scoring', 'history'] as const,

  teamsRoot: [ROOT, 'teams'] as const,
  teams: (f: TeamFilters = {}) => [ROOT, 'teams', 'list', f] as const,
  team: (id: string, includeFormer: boolean) => [ROOT, 'teams', 'detail', id, includeFormer] as const,
  teamsByEmployee: (employeeId: string, includeFormer: boolean) =>
    [ROOT, 'teams', 'by-employee', employeeId, includeFormer] as const,

  cyclesRoot: [ROOT, 'cycles'] as const,
  cycles: (f: CycleFilters = {}) => [ROOT, 'cycles', 'list', f] as const,
  cycle: (id: string) => [ROOT, 'cycles', 'detail', id] as const,
  readiness: (id: string) => [ROOT, 'cycles', 'readiness', id] as const,

  goalsRoot: [ROOT, 'goals'] as const,
  goals: (f: GoalFilters) => [ROOT, 'goals', f] as const,

  reviewsRoot: [ROOT, 'reviews'] as const,
  reviews: (f: ReviewFilters) => [ROOT, 'reviews', 'list', f] as const,
  review: (id: string) => [ROOT, 'reviews', 'detail', id] as const,

  scoresRoot: [ROOT, 'score'] as const,
  score: (employeeId: string, cycleId: string) => [ROOT, 'score', employeeId, cycleId] as const,

  analyticsRoot: [ROOT, 'analytics'] as const,
  employeeAnalytics: (id: string, period: AnalyticsPeriod) => [ROOT, 'analytics', 'employee', id, period] as const,
  vsTeam: (id: string, period: AnalyticsPeriod, teamId?: string) =>
    [ROOT, 'analytics', 'vs-team', id, period, teamId ?? 'auto'] as const,
  teamAnalytics: (teamId: string, period: AnalyticsPeriod) => [ROOT, 'analytics', 'team', teamId, period] as const,
  myAnalytics: (period: AnalyticsPeriod) => [ROOT, 'analytics', 'me', period] as const,

  cycleRoot: [ROOT, 'cycle-analytics'] as const,
  employeeCycles: (id: string, year?: number) => [ROOT, 'cycle-analytics', 'employee', id, year ?? 'all'] as const,
  cycleResult: (cycleId: string, teamId?: string) =>
    [ROOT, 'cycle-analytics', 'cycle', cycleId, teamId ?? 'all'] as const,
  cycleCompare: (cycleIds: string[], teamId?: string) =>
    [ROOT, 'cycle-analytics', 'compare', cycleIds.join(','), teamId ?? 'all'] as const,
  myCycles: (year?: number) => [ROOT, 'cycle-analytics', 'me', year ?? 'all'] as const,

  feedbackRoot: [ROOT, 'feedback'] as const,
  received: (employeeId: string, f: FeedbackFilters = {}) => [ROOT, 'feedback', 'received', employeeId, f] as const,
  sent: (employeeId: string) => [ROOT, 'feedback', 'sent', employeeId] as const,
  feedbackSummary: (employeeId: string, since?: string) =>
    [ROOT, 'feedback', 'summary', employeeId, since ?? 'all'] as const,

  recsRoot: [ROOT, 'recommendations'] as const,
  recommendations: (teamId?: string) => [ROOT, 'recommendations', 'list', teamId ?? 'all'] as const,
  employeeRecommendation: (id: string) => [ROOT, 'recommendations', 'employee', id] as const,
}

function invalidate(qc: QueryClient, ...keys: ReadonlyArray<readonly unknown[]>) {
  for (const queryKey of keys) void qc.invalidateQueries({ queryKey })
}

/** 4xx yanıtlar tekrar denenmez — kullanıcıya hemen anlamlı mesaj gösterilir. */
const retry = (count: number, error: unknown) =>
  !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2

/* ---------------------------------- Metrikler --------------------------------- */

export function useMetrics(filters: MetricFilters = {}, enabled = true) {
  return useQuery({
    queryKey: pk.metrics(filters),
    queryFn: ({ signal }) => metricsApi.list(filters, signal),
    enabled,
    retry,
  })
}

function useMetricMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => invalidate(qc, pk.metricsRoot, pk.scoresRoot, pk.recsRoot),
  })
}

export const useCreateMetric = () => useMetricMutation((input: MetricInput) => metricsApi.create(input))

export const useUpdateMetric = () =>
  useMetricMutation(({ id, input }: { id: string; input: MetricInput }) => metricsApi.update(id, input))

export const useArchiveMetric = () => useMetricMutation((id: string) => metricsApi.archive(id))

export const useApplyTemplate = () =>
  useMetricMutation((template: MetricTemplate) => metricsApi.applyTemplate(template))

/* ------------------------------- Puanlama ayarı ------------------------------- */

export function useScoringConfig(enabled = true) {
  return useQuery({
    queryKey: pk.scoring,
    queryFn: ({ signal }) => scoringApi.get(signal),
    enabled,
    retry,
    staleTime: 60_000,
  })
}

export function useScoringHistory(enabled = true) {
  return useQuery({
    queryKey: pk.scoringHistory,
    queryFn: ({ signal }) => scoringApi.history(signal),
    enabled,
    retry,
  })
}

export function useUpdateScoringConfig() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: ScoringConfigInput) => scoringApi.update(input),
    onSuccess: () => invalidate(qc, pk.scoring, pk.scoresRoot, pk.analyticsRoot, pk.cycleRoot, pk.recsRoot),
  })
}

/* ------------------------------------ Ekipler ---------------------------------- */

export function useTeams(filters: TeamFilters = {}, enabled = true) {
  return useQuery({
    queryKey: pk.teams(filters),
    queryFn: ({ signal }) => teamsApi.list(filters, signal),
    enabled,
    retry,
  })
}

export function useTeam(id: string | undefined, includeFormer = false) {
  return useQuery({
    queryKey: pk.team(id ?? '', includeFormer),
    queryFn: ({ signal }) => teamsApi.get(id!, includeFormer, signal),
    enabled: Boolean(id),
    retry,
  })
}

export function useTeamsByEmployee(employeeId: string | undefined, includeFormer = false) {
  return useQuery({
    queryKey: pk.teamsByEmployee(employeeId ?? '', includeFormer),
    queryFn: ({ signal }) => teamsApi.byEmployee(employeeId!, includeFormer, signal),
    enabled: Boolean(employeeId),
    retry,
  })
}

function useTeamMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => invalidate(qc, pk.teamsRoot, pk.analyticsRoot, pk.recsRoot),
  })
}

export const useCreateTeam = () => useTeamMutation((input: CreateTeamInput) => teamsApi.create(input))

export const useUpdateTeam = () =>
  useTeamMutation(({ id, input }: { id: string; input: UpdateTeamInput }) => teamsApi.update(id, input))

export const useSetTeamLead = () =>
  useTeamMutation(({ id, leadEmployeeId }: { id: string; leadEmployeeId: string | null }) =>
    teamsApi.setLead(id, leadEmployeeId),
  )

export const useAddTeamMember = () =>
  useTeamMutation(({ id, input }: { id: string; input: AddMemberInput }) => teamsApi.addMember(id, input))

export const useRemoveTeamMember = () =>
  useTeamMutation(({ id, memberId, leftOn }: { id: string; memberId: string; leftOn?: string }) =>
    teamsApi.removeMember(id, memberId, leftOn),
  )

/* ------------------------------------ Dönemler --------------------------------- */

export function useCycles(filters: CycleFilters = {}, enabled = true) {
  return useQuery({
    queryKey: pk.cycles(filters),
    queryFn: ({ signal }) => cyclesApi.list(filters, signal),
    enabled,
    retry,
  })
}

export function useCycle(id: string | undefined) {
  return useQuery({
    queryKey: pk.cycle(id ?? ''),
    queryFn: ({ signal }) => cyclesApi.get(id!, signal),
    enabled: Boolean(id),
    retry,
  })
}

/** Kapanış onayında çağrılır; her açılışta taze okunur. */
export function useCycleReadiness(id: string | undefined) {
  return useQuery({
    queryKey: pk.readiness(id ?? ''),
    queryFn: ({ signal }) => cyclesApi.readiness(id!, signal),
    enabled: Boolean(id),
    retry,
    staleTime: 0,
    gcTime: 0,
  })
}

export function useCreateCycle() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateCycleInput) => cyclesApi.create(input),
    onSuccess: () => invalidate(qc, pk.cyclesRoot),
  })
}

export function useSetCycleStatus() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, status }: { id: string; status: CycleStatus }) => cyclesApi.setStatus(id, status),
    onSuccess: () => invalidate(qc, pk.cyclesRoot, pk.cycleRoot, pk.scoresRoot, pk.recsRoot),
  })
}

/* ------------------------------------ Hedefler --------------------------------- */

export function useGoals(filters: GoalFilters, enabled = true) {
  return useQuery({
    queryKey: pk.goals(filters),
    queryFn: ({ signal }) => goalsApi.list(filters, signal),
    enabled,
    retry,
  })
}

function useGoalMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => invalidate(qc, pk.goalsRoot, pk.scoresRoot, pk.analyticsRoot, pk.recsRoot),
  })
}

export const useCreateGoal = () => useGoalMutation((input: CreateGoalInput) => goalsApi.create(input))

export const useUpdateGoal = () =>
  useGoalMutation(({ id, input }: { id: string; input: UpdateGoalInput }) => goalsApi.update(id, input))

export const useDeleteGoal = () => useGoalMutation((id: string) => goalsApi.remove(id))

export const useUpdateGoalProgress = () =>
  useGoalMutation(({ id, input }: { id: string; input: GoalProgressInput }) => goalsApi.progress(id, input))

/* ------------------------------ Değerlendirme/puan ----------------------------- */

export function useReviews(filters: ReviewFilters, enabled = true) {
  return useQuery({
    queryKey: pk.reviews(filters),
    queryFn: ({ signal }) => reviewsApi.list(filters, signal),
    enabled,
    retry,
  })
}

export function useReview(id: string | undefined) {
  return useQuery({
    queryKey: pk.review(id ?? ''),
    queryFn: ({ signal }) => reviewsApi.get(id!, signal),
    enabled: Boolean(id),
    retry,
  })
}

export function useCreateReview() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateReviewInput) => reviewsApi.create(input),
    onSuccess: () => invalidate(qc, pk.reviewsRoot),
  })
}

/**
 * Taslağı sunucuya yazar. Açık formu yeniden çekmemek için ayrıntı önbelleği
 * yerinde güncellenir (gönderilmiş kayıt geç gelen bir taslak yanıtıyla
 * ezilmez); liste yalnızca bayat işaretlenir, ekrana dönünce tazelenir.
 */
export function useSaveReviewDraft() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: SubmitReviewInput }) => reviewsApi.saveDraft(id, input),
    onSuccess: (review) => {
      qc.setQueryData<Review>(pk.review(review.id), (old) => (old?.isSubmitted ? old : review))
      void qc.invalidateQueries({ queryKey: [ROOT, 'reviews', 'list'], refetchType: 'none' })
    },
  })
}

export function useSubmitReview() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: SubmitReviewInput }) => reviewsApi.submit(id, input),
    onSuccess: () =>
      invalidate(qc, pk.reviewsRoot, pk.scoresRoot, pk.analyticsRoot, pk.cycleRoot, pk.recsRoot, pk.cyclesRoot),
  })
}

export function useScore(employeeId: string | undefined, cycleId: string | undefined) {
  return useQuery({
    queryKey: pk.score(employeeId ?? '', cycleId ?? ''),
    queryFn: ({ signal }) => reviewsApi.score(employeeId!, cycleId!, signal),
    enabled: Boolean(employeeId && cycleId),
    retry,
  })
}

/* -------------------------------- Sürekli izleme ------------------------------- */

export function useEmployeeAnalytics(id: string | undefined, period: AnalyticsPeriod) {
  return useQuery({
    queryKey: pk.employeeAnalytics(id ?? '', period),
    queryFn: ({ signal }) => analyticsApi.employee(id!, period, signal),
    enabled: Boolean(id),
    retry,
  })
}

export function useVsTeam(id: string | undefined, period: AnalyticsPeriod, teamId?: string) {
  return useQuery({
    queryKey: pk.vsTeam(id ?? '', period, teamId),
    queryFn: ({ signal }) => analyticsApi.vsTeam(id!, period, teamId, signal),
    enabled: Boolean(id),
    retry,
  })
}

export function useTeamAnalytics(teamId: string | undefined, period: AnalyticsPeriod) {
  return useQuery({
    queryKey: pk.teamAnalytics(teamId ?? '', period),
    queryFn: ({ signal }) => analyticsApi.team(teamId!, period, signal),
    enabled: Boolean(teamId),
    retry,
  })
}

export function useMyAnalytics(period: AnalyticsPeriod, enabled = true) {
  return useQuery({
    queryKey: pk.myAnalytics(period),
    queryFn: ({ signal }) => analyticsApi.me(period, signal),
    enabled,
    retry,
  })
}

/**
 * Oturumdaki kullanıcının çalışan kimliği.
 *
 * Backend `/me` uçlarında token e-postasını çalışan kaydıyla eşliyor; aynı
 * eşlemeyi arayüzde tekrar yazmak yerine `/analytics/me` yanıtındaki
 * `employeeId` kullanılır ("gönderdiklerim", değerlendiren kimliği…).
 * Kayıt yoksa `notLinked` true döner.
 */
export function useMyEmployeeId(enabled = true) {
  const q = useQuery({
    queryKey: pk.myAnalytics('quarter'),
    queryFn: ({ signal }) => analyticsApi.me('quarter', signal),
    enabled,
    retry,
    staleTime: 5 * 60_000,
    select: (d) => d.employeeId,
  })
  return {
    employeeId: q.data || undefined,
    isPending: enabled && q.isPending,
    notLinked: isNoEmployeeRecord(q.error),
    error: q.error,
  }
}

/** `/me` uçlarının "çalışan kaydı eşleşmedi" yanıtı. */
export function isNoEmployeeRecord(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404
}

/* ------------------------------- Dönem sonuçları ------------------------------ */

export function useEmployeeCycles(id: string | undefined, year?: number) {
  return useQuery({
    queryKey: pk.employeeCycles(id ?? '', year),
    queryFn: ({ signal }) => cycleAnalyticsApi.employee(id!, year, signal),
    enabled: Boolean(id),
    retry,
  })
}

export function useCycleResult(cycleId: string | undefined, teamId?: string) {
  return useQuery({
    queryKey: pk.cycleResult(cycleId ?? '', teamId),
    queryFn: ({ signal }) => cycleAnalyticsApi.cycle(cycleId!, teamId, signal),
    enabled: Boolean(cycleId),
    retry,
  })
}

export function useCycleCompare(cycleIds: string[], teamId?: string) {
  return useQuery({
    queryKey: pk.cycleCompare(cycleIds, teamId),
    queryFn: ({ signal }) => cycleAnalyticsApi.compare(cycleIds, teamId, signal),
    enabled: cycleIds.length >= 2,
    retry,
  })
}

export function useMyCycles(year?: number, enabled = true) {
  return useQuery({
    queryKey: pk.myCycles(year),
    queryFn: ({ signal }) => cycleAnalyticsApi.me(year, signal),
    enabled,
    retry,
  })
}

/* --------------------------------- Geri bildirim -------------------------------- */

export function useReceivedFeedback(employeeId: string | undefined, filters: FeedbackFilters = {}) {
  return useQuery({
    queryKey: pk.received(employeeId ?? '', filters),
    queryFn: ({ signal }) => feedbackApi.received(employeeId!, filters, signal),
    enabled: Boolean(employeeId),
    retry,
  })
}

export function useSentFeedback(employeeId: string | undefined) {
  return useQuery({
    queryKey: pk.sent(employeeId ?? ''),
    queryFn: ({ signal }) => feedbackApi.sent(employeeId!, signal),
    enabled: Boolean(employeeId),
    retry,
  })
}

export function useFeedbackSummary(employeeId: string | undefined, since?: string) {
  return useQuery({
    queryKey: pk.feedbackSummary(employeeId ?? '', since),
    queryFn: ({ signal }) => feedbackApi.summary(employeeId!, since, signal),
    enabled: Boolean(employeeId),
    retry,
  })
}

export function useCreateFeedback() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: FeedbackInput) => feedbackApi.create(input),
    onSuccess: () => invalidate(qc, pk.feedbackRoot, pk.analyticsRoot),
  })
}

export function useMarkFeedbackRead() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => feedbackApi.markRead(id),
    onSuccess: () => invalidate(qc, pk.feedbackRoot, pk.analyticsRoot),
  })
}

/* ------------------------------------ Öneriler ---------------------------------- */

export function useRecommendations(teamId?: string, enabled = true) {
  return useQuery({
    queryKey: pk.recommendations(teamId),
    queryFn: ({ signal }) => recommendationsApi.list(teamId, true, signal),
    enabled,
    retry,
  })
}

export function useEmployeeRecommendation(id: string | undefined) {
  return useQuery({
    queryKey: pk.employeeRecommendation(id ?? ''),
    queryFn: ({ signal }) => recommendationsApi.employee(id!, signal),
    enabled: Boolean(id),
    retry,
  })
}
