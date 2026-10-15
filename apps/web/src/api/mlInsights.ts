import { apiFetch, qs } from './client'
import type { AnomalyFlag } from '@/lib/expenseAudit'

/**
 * ML dalgası 2 uçları. Hepsi yerel, açıklanabilir yöntemler (LLM yok); yalnızca öneri/işaret üretir.
 *   - bordro denetimi (compensation-service), puantaj denetimi (timeshift-service)
 *   - mevsimsellikli izin tahmini + ekip kapasitesi, beceri haritası, anlamsal arama (governance-service)
 *   - anket konu + duygu (engagement-service), ücret adaleti (compensation-service, İK)
 *   - beceri çıkarımı (ml-inference /skills/extract; öneri — kaydı kullanıcı onaylar)
 */

/* ------------------------------------------------------------------ bordro / puantaj denetimi */

export interface PayrollAnomalies {
  periodId: string
  checkedAt: string | null
  calculatedAt: string | null
  items: Array<{ payslipId: string; employeeId: string; flags: AnomalyFlag[] }>
}

export interface TimesheetAnomalies {
  available: boolean
  items: Array<{ employeeId: string; week: string; flags: AnomalyFlag[] }>
  weeksEvaluated: string[]
  historyWeeks?: number
}

/* ------------------------------------------------------------------ izin tahmini */

export interface LeaveForecastWeek {
  week_start: string
  working_days: number
  holidays: string[]
  expected_absent_pct: number
  expected_person_days: number
  expected_absent_avg: number
  peak_day: string | null
  bridge_days: string[]
}

export interface LeaveForecast {
  start: string
  weeks: LeaveForecastWeek[]
  teams: Array<{ team: string; headcount: number; max_pct: number; weeks: Array<{ week_start: string; expected_absent_pct: number; expected_absent_avg: number }> }>
  hidden_people: number
  min_group: number
  effects: {
    base_rate_pct: number
    day_of_week: Array<{ day: number; label: string; factor: number }>
    month: Array<{ month: number; factor: number }>
    bridge_factor: number
    holiday_adjacent_factor: number
    monthly_seasonality: boolean
  }
  backtest: null | {
    weeks: number
    from: string
    to: string
    mape: number | null
    wape: number | null
    baseline_mape: number | null
    baseline_wape: number | null
    series: Array<{ week_start: string; actual: number; forecast: number }>
  }
  method: string
  note: string
}

export type LeaveForecastResponse =
  | { available: false; reason: string }
  | { available: true; historyStart: string; historyEnd: string; forecast: LeaveForecast }

/* ------------------------------------------------------------------ anket konuları */

export interface SurveyTopic {
  label: string
  keywords: string[]
  responses: number
  share_pct: number
  sentiment: { positive: number; negative: number; neutral: number }
  net_sentiment: number
  snippets: string[]
}

export interface SurveyTopicsResponse {
  questionId: string
  available: boolean
  responses?: number
  minGroup?: number
  analysis?: {
    available: boolean
    responses: number
    min_group: number
    topics: SurveyTopic[]
    hidden_responses: number
    sentiment?: { positive: number; negative: number; neutral: number }
    method: string
    note?: string
    reason?: string
  }
}

/* ------------------------------------------------------------------ beceriler */

export interface SkillExtract {
  skills: Array<{ key: string; label: string; evidence: string }>
  already_known: Array<{ key: string; label: string; evidence: string }>
  catalog_matches: Array<{ id: string; name: string; category: string | null; confidence: 'high' | 'medium'; evidence: string }>
  method: string
}

export interface SkillGraph {
  people: number
  people_with_skills: number
  nodes: Array<{ skill: string; people: number; departments: Array<{ department: string; people: number }> }>
  edges: Array<{ a: string; b: string; people: number; jaccard: number }>
  departments: Array<{ department: string; people: number; top_skills: Array<{ skill: string; people: number }> }>
  hidden_skills: number
  hidden_cells: number
  hidden_people: number
  min_group: number
  note: string
}

/* ------------------------------------------------------------------ ücret adaleti */

export interface PayEquityGroup {
  group: string
  people: number
  gap_pct: number
  ci_low_pct: number
  ci_high_pct: number
  significant: boolean
}

export interface PayEquityResponse {
  generatedAt: string
  currency: string | null
  included: number
  excludedCurrency: number
  report: {
    employees: number
    model: { r2: number; controls: string[]; residual_sd_pct: number; tenure_effect_pct_per_year: number; bands: number }
    reports: Array<{ attribute: 'department' | 'tenure_band'; label: string; controls: string[]; r2: number; groups: PayEquityGroup[]; hidden_people: number }>
    outside_2sd: number
    min_group: number
    unavailable_attributes: string[]
    notes: string[]
    method: string
  }
}

/* ------------------------------------------------------------------ anlamsal arama */

export interface SemanticHit {
  id: string
  source: 'kb' | 'library' | 'announcement'
  title: string
  snippet: string
  score: number
}

export const mlInsightsApi = {
  payrollAnomalies: (periodId: string, signal?: AbortSignal) =>
    apiFetch<PayrollAnomalies>(`/api/compensation/compensation/payroll/periods/${periodId}/anomalies`, { signal }),
  timesheetAnomalies: (weeks = 12, signal?: AbortSignal) =>
    apiFetch<TimesheetAnomalies>(`/api/timeshift/timesheet-report/anomalies${qs({ weeks })}`, { signal }),
  leaveForecast: (weeks = 12, signal?: AbortSignal) =>
    apiFetch<LeaveForecastResponse>(`/api/governance/analytics/leave-forecast${qs({ weeks })}`, { signal }),
  surveyTopics: (surveyId: string, questionId: string, signal?: AbortSignal) =>
    apiFetch<SurveyTopicsResponse>(`/api/engagement/surveys/${surveyId}/topics${qs({ questionId })}`, { signal }),
  extractSkills: (body: { text: string; catalog?: Array<{ id: string; name: string; category?: string | null }>; known?: string[] }, signal?: AbortSignal) =>
    apiFetch<SkillExtract>('/ml/skills/extract', { method: 'POST', body: { text: body.text, catalog: body.catalog ?? [], known: body.known ?? [] }, signal }),
  skillGraph: (signal?: AbortSignal) => apiFetch<SkillGraph>('/api/governance/skills/graph', { signal }),
  payEquity: (signal?: AbortSignal) => apiFetch<PayEquityResponse>('/api/compensation/compensation/analytics/pay-equity', { signal }),
  semanticSearch: (q: string, signal?: AbortSignal) =>
    apiFetch<{ hits: SemanticHit[]; documents?: number; method?: string | null }>(`/api/governance/search/semantic${qs({ q })}`, { signal }),
}
