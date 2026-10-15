/**
 * Performans ve ekip uçlarının istemcisi.
 *
 * YOLLAR: Her çağrı gateway üzerinden tam önekle gider. Gateway öneki kırpar;
 * servis kendi içinde `/api/metrics` görür ama biz her zaman tam yolu yazarız.
 * Öneksiz `/api/metrics` SPA fallback'ine düşer ve index.html döner —
 * `apiFetch` bunu anlaşılır bir hataya çevirir.
 *
 * Her yanıt `adapters.ts`'ten geçer: ekranlar normalize edilmiş şekli görür.
 */

import { apiFetch, qs, requireLinked } from '../client'
import {
  toCycle,
  toCycleAnalytics,
  toCycleCompare,
  toCycleHistory,
  toEmployeeAnalytics,
  toFeedback,
  toFeedbackSummary,
  toGoal,
  toMetric,
  toMyAnalytics,
  toReadiness,
  toRecommendation,
  toRecommendationList,
  toReview,
  toScore,
  toScoringConfig,
  toTeam,
  toTeamAnalytics,
  toTeamMember,
  toVsTeam,
} from './adapters'
import type {
  AddMemberInput,
  AnalyticsPeriod,
  CreateCycleInput,
  CreateGoalInput,
  UpdateGoalInput,
  CreateReviewInput,
  CycleFilters,
  CycleStatus,
  FeedbackFilters,
  FeedbackInput,
  GoalFilters,
  GoalProgressInput,
  MetricFilters,
  MetricInput,
  MetricTemplate,
  ReviewFilters,
  ScoringConfigInput,
  SubmitReviewInput,
  TeamFilters,
  CreateTeamInput,
  UpdateTeamInput,
} from './types'

/** Performans servisi öneki. */
export const PERF = '/api/performance'
/** Ekipler organizasyon servisinde. */
export const ORG = '/api/organization'

const list = <T>(v: unknown, map: (x: unknown) => T): T[] => (Array.isArray(v) ? v.map(map) : [])

export const metricsApi = {
  list: async (filters: MetricFilters = {}, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/metrics${qs(filters)}`, { signal }), toMetric),

  create: async (input: MetricInput) =>
    toMetric(await apiFetch<unknown>(`${PERF}/metrics`, { method: 'POST', body: input })),

  /** Kullanılmış metriğin ölçeği değişirse 400 döner — mesaj arayüzde gösterilir. */
  update: async (id: string, input: MetricInput) =>
    toMetric(await apiFetch<unknown>(`${PERF}/metrics/${id}`, { method: 'PUT', body: input })),

  /** SİLMEZ, arşivler. Geçmiş değerlendirmeler korunur. */
  archive: (id: string) => apiFetch<void>(`${PERF}/metrics/${id}`, { method: 'DELETE' }),

  applyTemplate: async (template: MetricTemplate) =>
    list(
      await apiFetch<unknown>(`${PERF}/metrics/apply-template${qs({ template })}`, { method: 'POST' }),
      toMetric,
    ),
}

export const scoringApi = {
  get: async (signal?: AbortSignal) =>
    toScoringConfig(await apiFetch<unknown>(`${PERF}/scoring-config`, { signal })),

  history: async (signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/scoring-config/history`, { signal }), toScoringConfig),

  /** Tüm alanlar gönderilir (kısmi güncelleme yok). Üzerine yazmaz — yeni sürüm oluşturur. */
  update: async (input: ScoringConfigInput) =>
    toScoringConfig(await apiFetch<unknown>(`${PERF}/scoring-config`, { method: 'PUT', body: input })),
}

export const teamsApi = {
  list: async (filters: TeamFilters = {}, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${ORG}/teams${qs(filters)}`, { signal }), toTeam),

  get: async (id: string, includeFormer = false, signal?: AbortSignal) =>
    toTeam(
      await apiFetch<unknown>(`${ORG}/teams/${id}${qs({ includeFormer: includeFormer || undefined })}`, {
        signal,
      }),
    ),

  byEmployee: async (employeeId: string, includeFormer = false, signal?: AbortSignal) =>
    list(
      await apiFetch<unknown>(
        `${ORG}/teams/by-employee/${employeeId}${qs({ includeFormer: includeFormer || undefined })}`,
        { signal },
      ),
      toTeam,
    ),

  create: async (input: CreateTeamInput) =>
    toTeam(await apiFetch<unknown>(`${ORG}/teams`, { method: 'POST', body: input })),

  update: async (id: string, input: UpdateTeamInput) =>
    toTeam(await apiFetch<unknown>(`${ORG}/teams/${id}`, { method: 'PUT', body: input })),

  /** `null` lideri kaldırır. Atanan lider otomatik üye olur. */
  setLead: (id: string, leadEmployeeId: string | null) =>
    apiFetch<unknown>(`${ORG}/teams/${id}/lead`, { method: 'POST', body: { leadEmployeeId } }),

  addMember: async (id: string, input: AddMemberInput) =>
    toTeamMember(await apiFetch<unknown>(`${ORG}/teams/${id}/members`, { method: 'POST', body: input })),

  /** Kayıt silinmez; ayrılma tarihi yazılır. */
  removeMember: (id: string, memberId: string, leftOn?: string) =>
    apiFetch<unknown>(`${ORG}/teams/${id}/members/${memberId}/remove`, {
      method: 'POST',
      body: leftOn ? { leftOn } : {},
    }),
}

export const cyclesApi = {
  list: async (filters: CycleFilters = {}, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/review-cycles${qs(filters)}`, { signal }), toCycle),

  get: async (id: string, signal?: AbortSignal) =>
    toCycle(await apiFetch<unknown>(`${PERF}/review-cycles/${id}`, { signal })),

  create: async (input: CreateCycleInput) =>
    toCycle(await apiFetch<unknown>(`${PERF}/review-cycles`, { method: 'POST', body: input })),

  /** Kapanış (`Closed`) geri alınamaz; önce `readiness` gösterilmeli. */
  setStatus: async (id: string, status: CycleStatus) =>
    toCycle(
      await apiFetch<unknown>(`${PERF}/review-cycles/${id}/status`, { method: 'POST', body: { status } }),
    ),

  readiness: async (id: string, signal?: AbortSignal) =>
    toReadiness(await apiFetch<unknown>(`${PERF}/review-cycles/${id}/readiness`, { signal }), id),
}

export const goalsApi = {
  list: async (filters: GoalFilters = {}, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/goals${qs(filters)}`, { signal }), toGoal),

  create: async (input: CreateGoalInput) =>
    toGoal(await apiFetch<unknown>(`${PERF}/goals`, { method: 'POST', body: input })),

  update: async (id: string, input: UpdateGoalInput) =>
    toGoal(await apiFetch<unknown>(`${PERF}/goals/${id}`, { method: 'PUT', body: input })),

  remove: (id: string) => apiFetch<void>(`${PERF}/goals/${id}`, { method: 'DELETE' }),

  progress: async (id: string, input: GoalProgressInput) =>
    toGoal(await apiFetch<unknown>(`${PERF}/goals/${id}/progress`, { method: 'POST', body: input })),
}

export const reviewsApi = {
  list: async (filters: ReviewFilters = {}, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/reviews${qs(filters)}`, { signal }), toReview),

  /** Taslağı `scores[]` ile döndürür — yarım kalan form buradan geri yüklenir. */
  get: async (id: string, signal?: AbortSignal) =>
    toReview(await apiFetch<unknown>(`${PERF}/reviews/${id}`, { signal })),

  create: async (input: CreateReviewInput) =>
    toReview(await apiFetch<unknown>(`${PERF}/reviews`, { method: 'POST', body: input })),

  /**
   * Taslak kaydı: zorunlu metrik kontrolü yok, kilitlemez, tekrar çağrılabilir.
   * Değer aralığı yine kontrol edilir (ör. 1–5 ölçekte 8 → 400).
   */
  saveDraft: async (id: string, input: SubmitReviewInput) =>
    toReview(await apiFetch<unknown>(`${PERF}/reviews/${id}/draft`, { method: 'PUT', body: input })),

  /** Zorunlu metrik eksikse 400 + `missing[]`. Gönderilmiş değerlendirme kilitlidir; kapalı dönemde 400. */
  submit: async (id: string, input: SubmitReviewInput) =>
    toReview(await apiFetch<unknown>(`${PERF}/reviews/${id}/submit`, { method: 'POST', body: input })),

  score: async (employeeId: string, cycleId: string, signal?: AbortSignal) =>
    toScore(await apiFetch<unknown>(`${PERF}/reviews/score${qs({ employeeId, cycleId })}`, { signal })),
}

export const analyticsApi = {
  employee: async (id: string, period: AnalyticsPeriod, signal?: AbortSignal) =>
    toEmployeeAnalytics(await apiFetch<unknown>(`${PERF}/analytics/employee/${id}${qs({ period })}`, { signal })),

  vsTeam: async (id: string, period: AnalyticsPeriod, teamId?: string, signal?: AbortSignal) =>
    toVsTeam(
      await apiFetch<unknown>(`${PERF}/analytics/employee/${id}/vs-team${qs({ period, teamId })}`, { signal }),
    ),

  team: async (teamId: string, period: AnalyticsPeriod, signal?: AbortSignal) =>
    toTeamAnalytics(await apiFetch<unknown>(`${PERF}/analytics/team/${teamId}${qs({ period })}`, { signal })),

  /** Token'daki e-postayı çalışan kaydıyla eşler; eşleşme yoksa 404. */
  me: async (period: AnalyticsPeriod, signal?: AbortSignal) =>
    toMyAnalytics(requireLinked(await apiFetch<unknown>(`${PERF}/analytics/me${qs({ period, optional: true })}`, { signal }))),
}

export const cycleAnalyticsApi = {
  employee: async (id: string, year?: number, signal?: AbortSignal) =>
    toCycleHistory(await apiFetch<unknown>(`${PERF}/cycle-analytics/employee/${id}${qs({ year })}`, { signal })),

  cycle: async (cycleId: string, teamId?: string, signal?: AbortSignal) =>
    toCycleAnalytics(
      await apiFetch<unknown>(`${PERF}/cycle-analytics/cycle/${cycleId}${qs({ teamId })}`, { signal }),
    ),

  compare: async (cycleIds: string[], teamId?: string, signal?: AbortSignal) =>
    toCycleCompare(
      await apiFetch<unknown>(
        `${PERF}/cycle-analytics/compare${qs({ cycleIds: cycleIds.join(','), teamId })}`,
        { signal },
      ),
    ),

  me: async (year?: number, signal?: AbortSignal) =>
    toCycleHistory(requireLinked(await apiFetch<unknown>(`${PERF}/cycle-analytics/me${qs({ year, optional: true })}`, { signal }))),
}

export const feedbackApi = {
  received: async (employeeId: string, filters: FeedbackFilters = {}, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/feedback/received/${employeeId}${qs(filters)}`, { signal }), toFeedback),

  sent: async (employeeId: string, signal?: AbortSignal) =>
    list(await apiFetch<unknown>(`${PERF}/feedback/sent/${employeeId}`, { signal }), toFeedback),

  /** `sentiment = Constructive` ve `reasonDetail` boşsa 400 döner. */
  create: async (input: FeedbackInput) =>
    toFeedback(await apiFetch<unknown>(`${PERF}/feedback`, { method: 'POST', body: input })),

  markRead: (id: string) => apiFetch<unknown>(`${PERF}/feedback/${id}/mark-read`, { method: 'POST' }),

  summary: async (employeeId: string, since?: string, signal?: AbortSignal) =>
    toFeedbackSummary(await apiFetch<unknown>(`${PERF}/feedback/summary${qs({ employeeId, since })}`, { signal })),
}

export const recommendationsApi = {
  list: async (teamId?: string, includeMlSignals = true, signal?: AbortSignal) =>
    toRecommendationList(
      await apiFetch<unknown>(`${PERF}/recommendations${qs({ teamId, includeMlSignals })}`, { signal }),
    ),

  employee: async (id: string, signal?: AbortSignal) =>
    toRecommendation(await apiFetch<unknown>(`${PERF}/recommendations/employee/${id}`, { signal })),
}
